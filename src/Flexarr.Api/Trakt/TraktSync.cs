using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Watchlist;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Trakt;

/// <summary>What the first history import would do, shown before it's confirmed.</summary>
/// <param name="TraktFilms">Films watched on Trakt.</param>
/// <param name="TraktEpisodes">Episodes watched on Trakt.</param>
/// <param name="InLibrary">Of those, how many the server has (that this profile can see).</param>
/// <param name="NewToFlexarr">Of those in the library, how many aren't watched in Flexarr yet, and will be.</param>
/// <param name="NotOnServer">Watched on Trakt but not on the server: remembered, so they arrive watched.</param>
/// <param name="Unmatched">Trakt gave no id Flexarr can use: left alone.</param>
/// <param name="ToTrakt">Watched in Flexarr but not on Trakt: sent to Trakt.</param>
public sealed record TraktImportPreview(int TraktFilms, int TraktEpisodes, int InLibrary, int NewToFlexarr, int NotOnServer, int Unmatched, int ToTrakt);

/// <summary>
/// Keeps a profile's Flexarr and Trakt in step: the first import (previewed, then confirmed), then every few minutes
/// whatever changed on Trakt since (<c>/sync/last_activities</c> says what), and the watchlists both ways. Watched
/// always wins and nothing is ever marked unwatched; see <see cref="TraktMerge"/>.
/// </summary>
public sealed partial class TraktSync(FlexarrDbContext db, TraktClient trakt, TraktAccounts accounts, WatchlistService watchlist, TraktLists lists,
    IMemoryCache cache, ILogger<TraktSync> logger)
{
    /// <summary>A Trakt resume point this close to Flexarr's own last play is that play's own pause coming back.</summary>
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMinutes(2);

    /// <summary>The whole watched list is compared at most this often (it can be megabytes).</summary>
    internal static readonly TimeSpan ReconcileEvery = TimeSpan.FromHours(24);

    /// <summary>How long the plays fetched for a preview are kept for its confirm.</summary>
    private static readonly TimeSpan PreviewKeptFor = TimeSpan.FromMinutes(10);

    /// <summary>How long a Trakt watchlist title Flexarr couldn't add is left alone before it's tried again.</summary>
    private static readonly TimeSpan RefusedFor = TimeSpan.FromHours(24);

    private LibraryTitles? _titles;

    /// <summary>The library's titles, loaded once per sync however many parts need them.</summary>
    private async Task<LibraryTitles> TitlesAsync(CancellationToken ct) => _titles ??= await LibraryTitles.LoadAsync(db, ct);

    // ---- Import ----

    private sealed record Play(TitleKey Key, DateTime WatchedAt);

    private async Task<List<Play>> TraktPlaysAsync(string token, CancellationToken ct)
    {
        var movies = await trakt.WatchedMoviesAsync(token, ct);
        var shows = await trakt.WatchedShowsAsync(token, ct);
        var plays = new List<Play>();
        foreach (var m in movies)
        {
            plays.Add(new Play(Key(MediaKind.Movie, m.Movie.Ids), m.LastWatchedAt));
        }

        foreach (var s in shows)
        {
            foreach (var season in s.Seasons ?? [])
            {
                foreach (var e in season.Episodes)
                {
                    plays.Add(new Play(Key(MediaKind.Episode, s.Show.Ids, season.Number, e.Number), e.LastWatchedAt));
                }
            }
        }

        return plays;
    }

    private static TitleKey Key(MediaKind kind, TraktIds ids, int? season = null, int? number = null) => new(kind,
        ids.Tmdb?.ToString(CultureInfo.InvariantCulture), ids.Tvdb?.ToString(CultureInfo.InvariantCulture), ids.Imdb, season, number);

    private static bool Usable(TitleKey key) => key.TmdbId is not null || key.TvdbId is not null || key.ImdbId is not null;

    private sealed record Matched(List<(Play Play, Guid Item)> Found, List<Play> Missing, int Unmatched);

    private async Task<Matched> MatchAsync(List<Play> plays, CancellationToken ct)
    {
        var titles = await TitlesAsync(ct);
        var found = new List<(Play Play, Guid Item)>();
        var missing = new List<Play>();
        var unmatched = 0;
        foreach (var play in plays)
        {
            if (!Usable(play.Key))
            {
                unmatched++;
            }
            else if (await titles.FindAsync(play.Key, ct) is { } item)
            {
                found.Add((play, item));
            }
            else
            {
                missing.Add(play);
            }
        }

        return new Matched(found, missing, unmatched);
    }

    /// <summary>Applies plays to the profile's watch state, keeping each state it adds for the next.</summary>
    private async Task ApplyAsync(string profileId, IEnumerable<(Play Play, Guid Item)> found, Dictionary<Guid, PlaybackState>? byItem, CancellationToken ct)
    {
        byItem ??= await db.PlaybackStates.Where(p => p.UserId == profileId).ToDictionaryAsync(s => s.MediaItemId, ct);
        foreach (var (play, item) in found)
        {
            byItem.TryGetValue(item, out var state);
            var (_, after) = TraktMerge.ApplyPlay(db, state, profileId, item, play.WatchedAt);
            byItem[item] = after!;
        }
    }

    /// <summary>
    /// Works out the import; with <paramref name="apply"/>, does it too. A preview's plays are kept a few minutes, so
    /// confirming it doesn't fetch them all from Trakt again.
    /// </summary>
    public async Task<TraktImportPreview?> ImportAsync(string profileId, bool apply, CancellationToken ct)
    {
        // Before fetching: plays made on Trakt while this runs come with the next sync.
        var startedAt = DateTime.UtcNow;
        // Keyed by the connection too: a preview from before reconnecting (perhaps as someone else) isn't confirmed.
        var connectedAt = await db.TraktConnections.AsNoTracking().Where(c => c.ProfileId == profileId).Select(c => (DateTime?)c.ConnectedAt).FirstOrDefaultAsync(ct);
        var key = $"trakt:import:{profileId}:{connectedAt?.Ticks}";
        List<Play>? plays;
        if (apply && cache.TryGetValue(key, out (List<Play> Plays, DateTime At) kept))
        {
            plays = kept.Plays;
            startedAt = kept.At;
        }
        else
        {
            var (ok, fetched) = await accounts.WithTokenAsync(profileId, token => TraktPlaysAsync(token, ct), ct);
            if (!ok || fetched is null)
            {
                return null;
            }

            plays = fetched;
        }

        cache.Remove(key);
        if (!apply)
        {
            cache.Set(key, (plays, startedAt), PreviewKeptFor);
        }

        var (found, missing, unmatched) = await MatchAsync(plays, ct);
        var states = await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind != MediaKind.Track && p.MediaItem.Kind != MediaKind.PodcastEpisode)
            .ToListAsync(ct);
        var byItem = states.ToDictionary(s => s.MediaItemId);
        var itemIds = found.Select(f => f.Item).ToHashSet();
        var toTrakt = states.Where(s => s.Watched && !itemIds.Contains(s.MediaItemId)).ToList();
        var toTraktTitles = await TraktOutbox.TitlesAsync(db, toTrakt.Select(s => s.MediaItemId).ToList(), ct);

        if (!apply)
        {
            // Counted as this profile sees the library: a title in a library hidden from it counts as not on the server.
            var matchedIds = itemIds.ToList();
            var visible = (await db.MediaItems.Where(i => matchedIds.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct)).ToHashSet();
            var shown = found.Where(f => visible.Contains(f.Item)).ToList();
            return new TraktImportPreview(plays.Count(p => p.Key.Kind == MediaKind.Movie), plays.Count(p => p.Key.Kind == MediaKind.Episode),
                shown.Count, shown.Count(f => !byItem.TryGetValue(f.Item, out var s) || !s.Watched), missing.Count + (found.Count - shown.Count), unmatched,
                toTraktTitles.Count);
        }

        await ApplyAsync(profileId, found, byItem, ct);
        await HoldAsync(profileId, missing, ct);
        await TraktOutbox.QueueHistoryAsync(db, profileId,
            toTrakt.Where(s => toTraktTitles.ContainsKey(s.MediaItemId)).Select(s => (s.MediaItemId, s.LastPlayedAt)).ToList(), ct);
        var connection = await db.TraktConnections.FirstAsync(c => c.ProfileId == profileId, ct);
        connection.ImportedAt = DateTime.UtcNow;
        // Later syncs take plays from here on; the import has everything before.
        connection.HistoryCursor = startedAt;
        connection.ReconciledAt = startedAt;
        // So the next sync pulls the plays since then, rather than waiting for Trakt's next watched change.
        connection.LastActivities = null;
        await db.SaveChangesAsync(ct);
        WatchlistService.LookAgain();
        LogImported(profileId, found.Count, missing.Count);
        return new TraktImportPreview(plays.Count(p => p.Key.Kind == MediaKind.Movie), plays.Count(p => p.Key.Kind == MediaKind.Episode),
            found.Count, 0, missing.Count, unmatched, toTraktTitles.Count);
    }

    /// <summary>Keeps plays of titles the server doesn't have, one per title (the latest), to apply when they arrive.</summary>
    private async Task HoldAsync(string profileId, List<Play> plays, CancellationToken ct)
    {
        if (plays.Count == 0)
        {
            return;
        }

        var existing = await db.TraktHistoryHolds.Where(h => h.ProfileId == profileId).ToListAsync(ct);
        // Tolerant of a title held twice (it can't happen now, but a duplicate mustn't stop every later sync).
        var byKey = existing.GroupBy(h => new TitleKey(h.Kind, h.TmdbId, h.TvdbId, h.ImdbId, h.SeasonNumber, h.EpisodeNumber)).ToDictionary(g => g.Key, g => g.First());
        foreach (var play in plays)
        {
            if (byKey.TryGetValue(play.Key, out var hold))
            {
                if (play.WatchedAt > hold.WatchedAt)
                {
                    hold.WatchedAt = play.WatchedAt;
                }

                continue;
            }

            hold = new TraktHistoryHold
            {
                ProfileId = profileId, Kind = play.Key.Kind, TmdbId = play.Key.TmdbId, TvdbId = play.Key.TvdbId, ImdbId = play.Key.ImdbId,
                SeasonNumber = play.Key.SeasonNumber, EpisodeNumber = play.Key.EpisodeNumber, WatchedAt = play.WatchedAt,
            };
            db.TraktHistoryHolds.Add(hold);
            byKey[play.Key] = hold;
        }
    }

    // ---- The regular sync ----

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SemaphoreSlim> Running = new(StringComparer.Ordinal);

    /// <summary>
    /// One sync of one profile: only what changed on Trakt since the last, and the watchlists. One at a time per
    /// profile: with <paramref name="wait"/> false (Sync now), it doesn't run if one already is, and says so.
    /// </summary>
    public async Task<bool> SyncAsync(string profileId, CancellationToken ct, bool wait = true)
    {
        var gate = Running.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(wait ? Timeout.InfiniteTimeSpan : TimeSpan.Zero, ct))
        {
            return false;
        }

        try
        {
            await SyncOnceAsync(profileId, ct);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task SyncOnceAsync(string profileId, CancellationToken ct)
    {
        var connection = await db.TraktConnections.FirstOrDefaultAsync(c => c.ProfileId == profileId, ct);
        if (connection is not { Status: TraktStatus.Connected })
        {
            return;
        }

        try
        {
            var (ok, activities) = await accounts.WithTokenAsync(profileId, token => trakt.LastActivitiesAsync(token, ct), ct);
            if (!ok || activities is null)
            {
                return;
            }

            var before = connection.LastActivities is { } raw ? JsonSerializer.Deserialize<TraktLastActivities>(raw, TraktJson.Options) : null;
            if (await accounts.TokenAsync(profileId, force: false, ct) is not { } token)
            {
                return;
            }

            var watchedChanged = before is null || activities.Movies?.WatchedAt != before.Movies?.WatchedAt || activities.Episodes?.WatchedAt != before.Episodes?.WatchedAt;
            if (connection.ImportedAt is not null && connection.HistoryCursor is { } cursor && watchedChanged)
            {
                connection.HistoryCursor = await PullHistoryAsync(profileId, token, cursor, ct);
                if (connection.ReconciledAt is not { } reconciled || DateTime.UtcNow - reconciled > ReconcileEvery)
                {
                    await ReconcileAsync(profileId, token, ct);
                    connection.ReconciledAt = DateTime.UtcNow;
                }
            }

            if (before is null || activities.Movies?.PausedAt != before.Movies?.PausedAt || activities.Episodes?.PausedAt != before.Episodes?.PausedAt)
            {
                await PullPlaybackAsync(profileId, token, ct);
            }

            var localChange = await db.WatchlistEntries.Where(w => w.ProfileId == profileId)
                .MaxAsync(w => (DateTime?)(w.RemovedAt != null && w.RemovedAt > w.AddedAt ? w.RemovedAt : w.AddedAt), ct);
            if (connection.WatchlistSyncedAt is null || activities.Watchlist?.UpdatedAt != before?.Watchlist?.UpdatedAt || localChange > connection.WatchlistSyncedAt)
            {
                await SyncWatchlistAsync(connection, token, ct);
            }

            if (TraktLists.Due(connection, activities, before))
            {
                await lists.SyncAsync(connection, token, await TitlesAsync(ct), ct);
            }

            connection.LastActivities = JsonSerializer.Serialize(activities, TraktJson.Options);
            connection.LastSyncAt = DateTime.UtcNow;
            connection.LastError = null;
            await db.SaveChangesAsync(ct);
        }
        catch (TraktException ex)
        {
            connection.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
        }
    }

    private static TitleKey? KeyOf(TraktMovie? movie, TraktShow? show, TraktEpisode? episode) =>
        movie is not null ? Key(MediaKind.Movie, movie.Ids)
        : show is not null && episode is not null ? Key(MediaKind.Episode, show.Ids, episode.Season, episode.Number)
        : null;

    /// <summary>Plays on Trakt since <paramref name="cursor"/>, applied (or held); returns the new cursor.</summary>
    private async Task<DateTime> PullHistoryAsync(string profileId, string token, DateTime cursor, CancellationToken ct)
    {
        var history = await trakt.HistorySinceAsync(token, cursor, ct);
        var plays = history.Select(h => (Key: KeyOf(h.Movie, h.Show, h.Episode), h.WatchedAt))
            .Where(p => p.Key is not null && Usable(p.Key)).Select(p => new Play(p.Key!, p.WatchedAt)).ToList();
        if (plays.Count > 0)
        {
            var (found, missing, _) = await MatchAsync(plays, ct);
            await ApplyAsync(profileId, found, null, ct);
            await HoldAsync(profileId, missing, ct);
            await db.SaveChangesAsync(ct);
            WatchlistService.LookAgain();
        }

        // Trakt's start_at includes the play at it; taking it twice changes nothing.
        return history.Count == 0 ? cursor : history.Max(h => h.WatchedAt);
    }

    /// <summary>
    /// Compares Trakt's whole watched list with Flexarr's: plays added there with an earlier date than the cursor (an
    /// import elsewhere, "watched on another date") are only found this way. The merge rules leave anything changed
    /// here since alone.
    /// </summary>
    private async Task ReconcileAsync(string profileId, string token, CancellationToken ct)
    {
        var (found, missing, _) = await MatchAsync(await TraktPlaysAsync(token, ct), ct);
        await ApplyAsync(profileId, found, null, ct);
        await HoldAsync(profileId, missing, ct);
        await db.SaveChangesAsync(ct);
        WatchlistService.LookAgain();
    }

    /// <summary>
    /// Resume points from other Trakt apps: newer than Flexarr's last play of the title and for something not watched
    /// here, they become its place. Flexarr's own pauses (scrobbled) come back too, and change nothing.
    /// </summary>
    private async Task PullPlaybackAsync(string profileId, string token, CancellationToken ct)
    {
        var playback = await trakt.PlaybackAsync(token, ct);
        if (playback.Count == 0)
        {
            return;
        }

        var titles = await TitlesAsync(ct);
        var points = new List<(TraktPlayback Point, Guid Item)>();
        foreach (var p in playback)
        {
            if (KeyOf(p.Movie, p.Show, p.Episode) is { } key && await titles.FindAsync(key, ct) is { } item)
            {
                points.Add((p, item));
            }
        }

        var ids = points.Select(p => p.Item).Distinct().ToList();
        var states = await db.PlaybackStates.Where(s => s.UserId == profileId && ids.Contains(s.MediaItemId)).ToDictionaryAsync(s => s.MediaItemId, ct);
        var durations = await db.MediaFiles.Where(f => ids.Contains(f.MediaItemId) && f.DurationSeconds != null)
            .GroupBy(f => f.MediaItemId).Select(g => new { g.Key, Duration = g.Max(f => f.DurationSeconds) }).ToDictionaryAsync(g => g.Key, g => g.Duration, ct);
        foreach (var (p, item) in points.OrderBy(p => p.Point.PausedAt))
        {
            states.TryGetValue(item, out var state);
            if (state is { Watched: true } || (state is not null && state.LastPlayedAt + EchoWindow >= p.PausedAt))
            {
                continue;
            }

            var duration = state?.DurationSeconds ?? durations.GetValueOrDefault(item);
            if (duration is not > 0)
            {
                continue;
            }

            if (state is null)
            {
                state = states[item] = new PlaybackState { UserId = profileId, MediaItemId = item };
                db.PlaybackStates.Add(state);
            }

            state.PositionSeconds = Math.Round(duration.Value * Math.Clamp(p.Progress, 0, 100) / 100);
            state.DurationSeconds = duration;
            state.LastPlayedAt = p.PausedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Puts the watchlists in step. A title on both is remembered as on Trakt (<see cref="WatchlistEntry.OnTraktAt"/>).
    /// Then: one added on either side goes to the other; one taken off Flexarr's list since it went on Trakt's comes
    /// off Trakt's; one gone from Trakt's that was on it comes off Flexarr's. One Trakt never took (a full free list, a
    /// title it doesn't know) stays on Flexarr's and is offered again. The first time, they're only added together.
    /// </summary>
    private async Task SyncWatchlistAsync(TraktConnection connection, string token, CancellationToken ct)
    {
        var profileId = connection.ProfileId;
        var startedAt = DateTime.UtcNow;
        var first = connection.WatchlistSyncedAt is null;
        var remote = (await trakt.WatchlistAsync(token, ct)).Where(w => w is { Type: "movie", Movie: not null } or { Type: "show", Show: not null }).ToList();
        var entries = await db.WatchlistEntries.Where(w => w.ProfileId == profileId).ToListAsync(ct);
        var titles = await TitlesAsync(ct);

        WatchlistEntry? Match(TraktWatchlistItem w)
        {
            var (kind, ids) = w.Movie is { } m ? (MediaKind.Movie, m.Ids) : (MediaKind.Series, w.Show!.Ids);
            if (ids.Tmdb is { } tmdb && entries.FirstOrDefault(e => e.Kind == kind && e.TmdbId == tmdb) is { } byTmdb)
            {
                return byTmdb;
            }

            return titles.Title(Key(kind, ids)) is { } item ? entries.FirstOrDefault(e => e.MediaItemId == item) : null;
        }

        var matched = new HashSet<Guid>();
        var addToTrakt = new List<WatchlistEntry>();
        var removeFromTrakt = new List<WatchlistEntry>();
        foreach (var w in remote)
        {
            var entry = Match(w);
            if (entry is not null)
            {
                matched.Add(entry.Id);
            }

            if (entry is { RemovedAt: null })
            {
                entry.OnTraktAt = DateTime.UtcNow;
                continue;
            }

            if (entry is { RemovedAt: { } removed } && removed > w.ListedAt)
            {
                // Taken off here after it went on there: off there too, except the first time, which only adds.
                if (!first)
                {
                    removeFromTrakt.Add(entry);
                }

                continue;
            }

            await AddFromTraktAsync(profileId, w, startedAt, ct);
        }

        foreach (var entry in entries.Where(e => e.RemovedAt is null && !matched.Contains(e.Id)))
        {
            if (entry.OnTraktAt is { } onTrakt && onTrakt >= entry.AddedAt && !first)
            {
                // It was on Trakt's list and isn't now: taken off there.
                await watchlist.RemoveAsync(profileId, entry.Id, ct);
                entry.OnTraktAt = null;
            }
            else
            {
                addToTrakt.Add(entry);
            }
        }

        await db.SaveChangesAsync(ct);
        await PushWatchlistAsync(connection, "/sync/watchlist", addToTrakt, token, ct);
        await PushWatchlistAsync(connection, "/sync/watchlist/remove", removeFromTrakt, token, ct);
        connection.WatchlistSyncedAt = startedAt;
    }

    private async Task AddFromTraktAsync(string profileId, TraktWatchlistItem w, DateTime seen, CancellationToken ct)
    {
        var (kind, ids, name, year) = w.Movie is { } m ? (MediaKind.Movie, m.Ids, m.Title, m.Year) : (MediaKind.Series, w.Show!.Ids, w.Show.Title, w.Show.Year);
        if (ids.Tmdb is not { } tmdb)
        {
            return; // Flexarr's watchlist keeps titles by TMDB id.
        }

        // A title Flexarr couldn't add (TMDB doesn't know it, the list is full) isn't tried on every sync.
        var refusedKey = $"trakt:refused:{profileId}:{kind}:{tmdb.ToString(CultureInfo.InvariantCulture)}";
        if (cache.TryGetValue(refusedKey, out _))
        {
            return;
        }

        var mediaType = kind == MediaKind.Movie ? "movie" : "tv";
        var (added, refusal) = await watchlist.AddAsync(profileId, new WatchlistAdd(null, mediaType, tmdb), WatchlistSource.Trakt, ct, mayRequest: false);
        if (added is not null)
        {
            if (await db.WatchlistEntries.FindAsync([added.Entry.Id], ct) is { } entry)
            {
                // From Trakt, so known to be on it. AddedAt stays now: a title watched before it came across mustn't be
                // taken off as watched "since it went on".
                entry.OnTraktAt = DateTime.UtcNow;
            }

            return;
        }

        // No Seerr to describe it (or it didn't answer): kept with what Trakt says, so it's still on the list. A full
        // list (also a 409) isn't overfilled.
        if (refusal is { StatusCode: 409 or 502 } && name is not null && !refusal.Message.Contains("holds up to", StringComparison.Ordinal)
            && !await db.WatchlistEntries.AnyAsync(e => e.ProfileId == profileId && e.Kind == kind && e.TmdbId == tmdb, ct))
        {
            db.WatchlistEntries.Add(new WatchlistEntry
            {
                ProfileId = profileId, Kind = kind, TmdbId = tmdb, ImdbId = ids.Imdb, TvdbId = ids.Tvdb?.ToString(CultureInfo.InvariantCulture),
                Title = name, Year = year, Source = WatchlistSource.Trakt, OnTraktAt = DateTime.UtcNow.AddSeconds(1),
            });
            await db.SaveChangesAsync(ct);
            return;
        }

        cache.Set(refusedKey, true, RefusedFor);
    }

    private async Task PushWatchlistAsync(TraktConnection connection, string path, List<WatchlistEntry> entries, string token, CancellationToken ct)
    {
        var body = WatchlistBody(entries);
        if (body is null)
        {
            return;
        }

        try
        {
            await trakt.SyncAsync(path, body, token, ct);
            if (path == "/sync/watchlist")
            {
                connection.WatchlistFull = false;
            }
        }
        catch (TraktException ex) when (ex.AccountLimit)
        {
            connection.WatchlistFull = true;
        }
    }

    internal static TraktSyncBody? WatchlistBody(IEnumerable<WatchlistEntry> entries)
    {
        var movies = new List<TraktSyncMovie>();
        var shows = new List<TraktSyncShow>();
        foreach (var e in entries)
        {
            if (TraktOutbox.Ids(e.TmdbId?.ToString(CultureInfo.InvariantCulture), e.TvdbId, e.ImdbId) is not { } ids)
            {
                continue;
            }

            if (e.Kind == MediaKind.Movie)
            {
                movies.Add(new TraktSyncMovie(ids));
            }
            else
            {
                shows.Add(new TraktSyncShow(ids));
            }
        }

        return movies.Count + shows.Count == 0 ? null : new TraktSyncBody(movies.Count > 0 ? movies : null, shows.Count > 0 ? shows : null);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Imported Trakt history for profile {Profile}: {Found} in the library, {Held} kept for later")]
    private partial void LogImported(string profile, int found, int held);
}

/// <summary>
/// Runs <see cref="TraktSync"/> for every connected profile every few minutes. A nudge for one profile (its own Sync
/// now, a watchlist edit, a finished episode) syncs just that profile soon, at most once a minute each.
/// </summary>
public sealed partial class TraktSyncWorker(IServiceScopeFactory scopes, ILogger<TraktSyncWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    /// <summary>A profile is synced on request at most this often.</summary>
    internal static readonly TimeSpan NudgeGap = TimeSpan.FromMinutes(1);

    private static readonly SemaphoreSlim Signal = new(0, 1);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> Asked = new(StringComparer.Ordinal);
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> LastRun = new(StringComparer.Ordinal);
    private static bool everyone;

    /// <summary>Whether a profile may sync right now on request (and, if so, that it's syncing now).</summary>
    public static bool TryRunNow(string profileId)
    {
        var now = DateTime.UtcNow;
        var last = LastRun.GetValueOrDefault(profileId);
        if (now - last < NudgeGap)
        {
            return false;
        }

        return LastRun.TryUpdate(profileId, now, last) || LastRun.TryAdd(profileId, now);
    }

    /// <summary>Sync <paramref name="profileId"/> soon (or everyone, after a connect or a list change for all).</summary>
    public static void Nudge(string? profileId = null)
    {
        if (profileId is null)
        {
            everyone = true;
        }
        else
        {
            Asked[profileId] = DateTime.UtcNow;
        }

        if (Signal.CurrentCount == 0)
        {
            try
            {
                Signal.Release();
            }
            catch (SemaphoreFullException)
            {
                // Nudged already.
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        var nextRound = DateTime.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var round = DateTime.UtcNow >= nextRound || everyone;
                everyone = false;
                if (round)
                {
                    nextRound = DateTime.UtcNow + Interval;
                }

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                // Kids profiles have no Trakt: a profile made one after connecting is left alone (it's disconnected then).
                var profiles = await db.TraktConnections.Where(c => c.Status == TraktStatus.Connected && !db.Profiles.Any(p => p.Id == c.ProfileId && p.IsKids))
                    .Select(c => c.ProfileId).ToListAsync(stoppingToken);
                // Nudges for profiles that aren't connected (any more) would otherwise wake this every minute for ever.
                foreach (var stray in Asked.Keys.Where(k => !profiles.Contains(k)).ToList())
                {
                    Asked.TryRemove(stray, out _);
                }
                foreach (var profile in profiles)
                {
                    var asked = Asked.TryRemove(profile, out _);
                    if (!round && !(asked && DateTime.UtcNow - LastRun.GetValueOrDefault(profile) >= NudgeGap))
                    {
                        if (asked)
                        {
                            Asked.TryAdd(profile, DateTime.UtcNow); // Too soon: next time.
                        }

                        continue;
                    }

                    LastRun[profile] = DateTime.UtcNow;
                    await SyncOneAsync(profile, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }

            var wait = Asked.IsEmpty ? Interval : NudgeGap;
            await Signal.WaitAsync(wait, stoppingToken);
            // A burst of nudges (a run of watchlist edits) is one sync.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    /// <summary>One profile, in a scope of its own: whatever goes wrong with it doesn't stop the others.</summary>
    private async Task SyncOneAsync(string profileId, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<TraktSync>().SyncAsync(profileId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogProfileFailed(ex, profileId);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().TraktConnections.Where(c => c.ProfileId == profileId)
                    .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastError, "The last sync didn’t finish. Flexarr tries again shortly."), ct);
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                LogFailed(inner);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Trakt sync failed")]
    private partial void LogFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Trakt sync for profile {Profile} failed")]
    private partial void LogProfileFailed(Exception ex, string profile);
}
