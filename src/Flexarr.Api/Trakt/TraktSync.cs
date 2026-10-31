using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Watchlist;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Trakt;

/// <summary>What the first history import would do, shown before it's confirmed.</summary>
/// <param name="TraktFilms">Films watched on Trakt.</param>
/// <param name="TraktEpisodes">Episodes watched on Trakt.</param>
/// <param name="InLibrary">Of those, how many the server has.</param>
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
public sealed partial class TraktSync(FlexarrDbContext db, TraktClient trakt, TraktAccounts accounts, WatchlistService watchlist, ILogger<TraktSync> logger)
{
    /// <summary>A Trakt resume point this close to Flexarr's own last play is that play's own pause coming back.</summary>
    private static readonly TimeSpan EchoWindow = TimeSpan.FromMinutes(2);

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

    /// <summary>Works out the import; with <paramref name="apply"/>, does it too.</summary>
    public async Task<TraktImportPreview?> ImportAsync(string profileId, bool apply, CancellationToken ct)
    {
        var (ok, plays) = await accounts.WithTokenAsync(profileId, token => TraktPlaysAsync(token, ct), ct);
        if (!ok || plays is null)
        {
            return null;
        }

        var titles = await LibraryTitles.LoadAsync(db, ct);
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

        var itemIds = found.Select(f => f.Item).ToHashSet();
        var states = await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind != MediaKind.Track && p.MediaItem.Kind != MediaKind.PodcastEpisode)
            .ToListAsync(ct);
        var byItem = states.ToDictionary(s => s.MediaItemId);
        var newToFlexarr = found.Count(f => !byItem.TryGetValue(f.Item, out var s) || !s.Watched);
        var toTrakt = states.Where(s => s.Watched && !itemIds.Contains(s.MediaItemId)).ToList();
        var toTraktTitles = await TraktOutbox.TitlesAsync(db, toTrakt.Select(s => s.MediaItemId).ToList(), ct);
        var preview = new TraktImportPreview(plays.Count(p => p.Key.Kind == MediaKind.Movie), plays.Count(p => p.Key.Kind == MediaKind.Episode),
            found.Count, newToFlexarr, missing.Count, unmatched, toTraktTitles.Count);
        if (!apply)
        {
            return preview;
        }

        var startedAt = DateTime.UtcNow;
        foreach (var (play, item) in found)
        {
            byItem.TryGetValue(item, out var state);
            if (TraktMerge.ApplyPlay(db, state, profileId, item, play.WatchedAt) && state is null)
            {
                byItem[item] = db.PlaybackStates.Local.First(p => p.UserId == profileId && p.MediaItemId == item);
            }
        }

        await HoldAsync(profileId, missing, ct);
        await TraktOutbox.QueueHistoryAsync(db, profileId,
            toTrakt.Where(s => toTraktTitles.ContainsKey(s.MediaItemId)).Select(s => (s.MediaItemId, s.LastPlayedAt)).ToList(), ct);
        var connection = await db.TraktConnections.FirstAsync(c => c.ProfileId == profileId, ct);
        connection.ImportedAt = DateTime.UtcNow;
        // Later syncs take plays from here on; the import has everything before.
        connection.HistoryCursor = startedAt;
        await db.SaveChangesAsync(ct);
        WatchlistService.LookAgain();
        LogImported(profileId, found.Count, missing.Count);
        return preview;
    }

    /// <summary>Keeps plays of titles the server doesn't have, one per title (the latest), to apply when they arrive.</summary>
    private async Task HoldAsync(string profileId, List<Play> plays, CancellationToken ct)
    {
        if (plays.Count == 0)
        {
            return;
        }

        var existing = await db.TraktHistoryHolds.Where(h => h.ProfileId == profileId).ToListAsync(ct);
        var byKey = existing.ToDictionary(h => new TitleKey(h.Kind, h.TmdbId, h.TvdbId, h.ImdbId, h.SeasonNumber, h.EpisodeNumber));
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

    /// <summary>One sync of one profile: only what changed on Trakt since the last, and the watchlists.</summary>
    public async Task SyncAsync(string profileId, CancellationToken ct)
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
            var token = (await accounts.TokenAsync(profileId, force: false, ct))!;

            if (connection.ImportedAt is not null && connection.HistoryCursor is { } cursor
                && (before is null || activities.Movies?.WatchedAt != before.Movies?.WatchedAt || activities.Episodes?.WatchedAt != before.Episodes?.WatchedAt))
            {
                connection.HistoryCursor = await PullHistoryAsync(profileId, token, cursor, ct);
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

            connection.LastActivities = JsonSerializer.Serialize(activities, TraktJson.Options);
            connection.LastSyncAt = DateTime.UtcNow;
            connection.LastError = null;
            await db.SaveChangesAsync(ct);
            await TraktHolds.ApplyAsync(db, ct);
        }
        catch (TraktException ex)
        {
            connection.LastError = ex.Message;
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Plays on Trakt since <paramref name="cursor"/>, applied (or held); returns the new cursor.</summary>
    private async Task<DateTime> PullHistoryAsync(string profileId, string token, DateTime cursor, CancellationToken ct)
    {
        var history = await trakt.HistorySinceAsync(token, cursor, ct);
        if (history.Count == 0)
        {
            return cursor;
        }

        var titles = await LibraryTitles.LoadAsync(db, ct);
        var missing = new List<Play>();
        var states = new Dictionary<Guid, PlaybackState?>();
        foreach (var h in history)
        {
            var key = h.Movie is { } movie ? Key(MediaKind.Movie, movie.Ids)
                : h is { Show: { } show, Episode: { } episode } ? Key(MediaKind.Episode, show.Ids, episode.Season, episode.Number)
                : null;
            if (key is null || !Usable(key))
            {
                continue;
            }

            if (await titles.FindAsync(key, ct) is not { } item)
            {
                missing.Add(new Play(key, h.WatchedAt));
                continue;
            }

            if (!states.TryGetValue(item, out var state))
            {
                state = states[item] = await db.PlaybackStates.FirstOrDefaultAsync(p => p.UserId == profileId && p.MediaItemId == item, ct);
            }

            if (TraktMerge.ApplyPlay(db, state, profileId, item, h.WatchedAt) && state is null)
            {
                states[item] = db.PlaybackStates.Local.First(p => p.UserId == profileId && p.MediaItemId == item);
            }
        }

        await HoldAsync(profileId, missing, ct);
        await db.SaveChangesAsync(ct);
        WatchlistService.LookAgain();
        // Trakt's start_at includes the play at it; taking it twice changes nothing.
        return history.Max(h => h.WatchedAt);
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

        var titles = await LibraryTitles.LoadAsync(db, ct);
        foreach (var p in playback)
        {
            var key = p.Movie is { } movie ? Key(MediaKind.Movie, movie.Ids)
                : p is { Show: { } show, Episode: { } episode } ? Key(MediaKind.Episode, show.Ids, episode.Season, episode.Number)
                : null;
            if (key is null || await titles.FindAsync(key, ct) is not { } item)
            {
                continue;
            }

            var state = await db.PlaybackStates.FirstOrDefaultAsync(s => s.UserId == profileId && s.MediaItemId == item, ct);
            if (state is { Watched: true } || (state is not null && state.LastPlayedAt + EchoWindow >= p.PausedAt))
            {
                continue;
            }

            var duration = state?.DurationSeconds
                ?? await db.MediaFiles.Where(f => f.MediaItemId == item).Select(f => f.DurationSeconds).FirstOrDefaultAsync(ct);
            if (duration is not > 0)
            {
                continue;
            }

            if (state is null)
            {
                state = new PlaybackState { UserId = profileId, MediaItemId = item };
                db.PlaybackStates.Add(state);
            }

            state.PositionSeconds = Math.Round(duration.Value * Math.Clamp(p.Progress, 0, 100) / 100);
            state.DurationSeconds = duration;
            state.LastPlayedAt = p.PausedAt;
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Puts the watchlists in step. Since the last time: a title added on one side goes to the other, and one taken
    /// off one side comes off the other. The first time, they're only added together.
    /// </summary>
    private async Task SyncWatchlistAsync(TraktConnection connection, string token, CancellationToken ct)
    {
        var profileId = connection.ProfileId;
        var startedAt = DateTime.UtcNow;
        var since = connection.WatchlistSyncedAt;
        var remote = (await trakt.WatchlistAsync(token, ct)).Where(w => w.Type is "movie" or "show").ToList();
        var entries = await db.WatchlistEntries.Where(w => w.ProfileId == profileId).ToListAsync(ct);
        var titles = await LibraryTitles.LoadAsync(db, ct);

        WatchlistEntry? Match(TraktWatchlistItem w)
        {
            var (kind, ids) = w.Movie is { } m ? (MediaKind.Movie, m.Ids) : (MediaKind.Series, w.Show!.Ids);
            var byTmdb = ids.Tmdb is { } tmdb ? entries.FirstOrDefault(e => e.Kind == kind && e.TmdbId == tmdb) : null;
            if (byTmdb is not null)
            {
                return byTmdb;
            }

            var item = titles.Title(Key(kind == MediaKind.Movie ? MediaKind.Movie : MediaKind.Series, ids));
            return item is not null ? entries.FirstOrDefault(e => e.MediaItemId == item) : null;
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
                continue;
            }

            if (entry is { RemovedAt: { } removed } && (removed > w.ListedAt || (since is not null && removed > since)))
            {
                // Taken off here since it went on there.
                removeFromTrakt.Add(entry);
                continue;
            }

            await AddFromTraktAsync(profileId, w, ct);
        }

        foreach (var entry in entries.Where(e => e.RemovedAt is null && !matched.Contains(e.Id)))
        {
            if (since is null || entry.AddedAt > since)
            {
                addToTrakt.Add(entry);
            }
            else
            {
                // On both lists last time, gone from Trakt's now: taken off there.
                await watchlist.RemoveAsync(profileId, entry.Id, ct);
            }
        }

        await PushWatchlistAsync(connection, "/sync/watchlist", addToTrakt, token, ct);
        await PushWatchlistAsync(connection, "/sync/watchlist/remove", removeFromTrakt, token, ct);
        connection.WatchlistSyncedAt = startedAt;
    }

    private async Task AddFromTraktAsync(string profileId, TraktWatchlistItem w, CancellationToken ct)
    {
        var (kind, ids, name, year) = w.Movie is { } m ? (MediaKind.Movie, m.Ids, m.Title, m.Year) : (MediaKind.Series, w.Show!.Ids, w.Show.Title, w.Show.Year);
        var mediaType = kind == MediaKind.Movie ? "movie" : "tv";
        if (ids.Tmdb is not { } tmdb)
        {
            return; // Flexarr's watchlist keeps titles by TMDB id.
        }

        var (added, refusal) = await watchlist.AddAsync(profileId, new WatchlistAdd(null, mediaType, tmdb), WatchlistSource.Trakt, ct, mayRequest: false);
        if (added is not null || refusal is not { StatusCode: 409 or 502 } || name is null)
        {
            return;
        }

        // No Seerr to describe it: kept with what Trakt says, so it's still on the list.
        if (await db.WatchlistEntries.AnyAsync(e => e.ProfileId == profileId && e.Kind == kind && e.TmdbId == tmdb, ct))
        {
            return;
        }

        db.WatchlistEntries.Add(new WatchlistEntry
        {
            ProfileId = profileId, Kind = kind, TmdbId = tmdb, ImdbId = ids.Imdb, TvdbId = ids.Tvdb?.ToString(CultureInfo.InvariantCulture),
            Title = name, Year = year, Source = WatchlistSource.Trakt, AddedAt = w.ListedAt,
        });
        await db.SaveChangesAsync(ct);
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

/// <summary>Runs <see cref="TraktSync"/> for every connected profile every few minutes, and straight away when nudged.</summary>
public sealed partial class TraktSyncWorker(IServiceScopeFactory scopes, ILogger<TraktSyncWorker> logger) : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private static readonly SemaphoreSlim Signal = new(0, 1);

    /// <summary>Something changed (a connect, a watchlist edit, playback ended): sync soon rather than at the next look.</summary>
    public static void Nudge()
    {
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
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var profiles = await db.TraktConnections.Where(c => c.Status == TraktStatus.Connected).Select(c => c.ProfileId).ToListAsync(stoppingToken);
                foreach (var profile in profiles)
                {
                    await using var each = scopes.CreateAsyncScope();
                    await each.ServiceProvider.GetRequiredService<TraktSync>().SyncAsync(profile, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
            }

            await Signal.WaitAsync(Interval, stoppingToken);
            // A burst of nudges (a run of watchlist edits) is one sync.
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The Trakt sync failed")]
    private partial void LogFailed(Exception ex);
}
