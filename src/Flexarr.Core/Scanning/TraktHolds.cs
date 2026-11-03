using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Scanning;

/// <summary>A film or episode named by ids, as Trakt and tombstones name them: for an episode, its show's ids and its number.</summary>
public sealed record TitleKey(MediaKind Kind, string? TmdbId, string? TvdbId, string? ImdbId, int? SeasonNumber = null, int? EpisodeNumber = null);

/// <summary>
/// Trakt's plays brought into Flexarr's watch state, under one rule: watched wins. Nothing is ever marked unwatched;
/// but something changed here after the play (marked unwatched by hand, or being watched again) is left as it is.
/// </summary>
public static class TraktMerge
{
    /// <summary>
    /// Applies a play at <paramref name="watchedAt"/>. Returns whether it changed anything, and the state (the one
    /// given, or the one it added), so callers keep it without looking it up again.
    /// </summary>
    public static (bool Changed, PlaybackState? State) ApplyPlay(FlexarrDbContext db, PlaybackState? state, string profileId, Guid itemId, DateTime watchedAt)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (state is null)
        {
            state = new PlaybackState { UserId = profileId, MediaItemId = itemId, Watched = true, PlayCount = 1, LastPlayedAt = watchedAt };
            db.PlaybackStates.Add(state);
            return (true, state);
        }

        if (state.Watched)
        {
            if (watchedAt > state.LastPlayedAt)
            {
                state.LastPlayedAt = watchedAt;
                return (true, state);
            }

            return (false, state);
        }

        if (state.LastPlayedAt >= watchedAt)
        {
            // Changed here since that play: marked unwatched by hand, or being watched again. That stands.
            return (false, state);
        }

        state.Watched = true;
        state.PositionSeconds = 0;
        state.PlayCount = Math.Max(state.PlayCount, 1);
        state.LastPlayedAt = watchedAt;
        return (true, state);
    }
}

/// <summary>Finds library films and episodes by their ids.</summary>
public sealed class LibraryTitles
{
    private readonly Dictionary<string, Guid> _movies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _shows = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, Dictionary<(int, int), Guid>> _episodes = [];
    private readonly FlexarrDbContext _db;

    private LibraryTitles(FlexarrDbContext db) => _db = db;

    /// <summary>Every film and show in the library (not missing ones), by each of their ids.</summary>
    public static async Task<LibraryTitles> LoadAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var titles = new LibraryTitles(db);
        var rows = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.MissingSince == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series) && (i.TmdbId != null || i.TvdbId != null || i.ImdbId != null))
            .Select(i => new { i.Id, i.Kind, i.TmdbId, i.TvdbId, i.ImdbId })
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var r in rows)
        {
            var map = r.Kind == MediaKind.Movie ? titles._movies : titles._shows;
            if (r.TmdbId is { } tmdb) map.TryAdd("tmdb:" + tmdb, r.Id);
            if (r.TvdbId is { } tvdb) map.TryAdd("tvdb:" + tvdb, r.Id);
            if (r.ImdbId is { } imdb) map.TryAdd("imdb:" + imdb, r.Id);
        }

        return titles;
    }

    /// <summary>The film, or the show, for <paramref name="key"/> (an episode's key finds its show).</summary>
    public Guid? Title(TitleKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var map = key.Kind == MediaKind.Movie ? _movies : _shows;
        // Films by TMDB first; shows by TVDB first, as Sonarr knows them.
        string?[] tries = key.Kind == MediaKind.Movie
            ? [Tag("tmdb", key.TmdbId), Tag("imdb", key.ImdbId), Tag("tvdb", key.TvdbId)]
            : [Tag("tvdb", key.TvdbId), Tag("tmdb", key.TmdbId), Tag("imdb", key.ImdbId)];
        foreach (var t in tries)
        {
            if (t is not null && map.TryGetValue(t, out var id))
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>The library item a key names: a film, or an episode of a show by season and number.</summary>
    public async Task<Guid?> FindAsync(TitleKey key, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Title(key) is not { } title)
        {
            return null;
        }

        if (key.Kind != MediaKind.Episode)
        {
            return title;
        }

        if (key is not { SeasonNumber: { } season, EpisodeNumber: { } number })
        {
            return null;
        }

        if (!_episodes.TryGetValue(title, out var episodes))
        {
            var rows = await _db.MediaItems.IgnoreQueryFilters()
                .Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == title && e.MissingSince == null && e.SeasonNumber != null && e.EpisodeNumber != null)
                .Select(e => new { e.Id, e.SeasonNumber, e.EpisodeNumber, e.EpisodeNumberEnd })
                .ToListAsync(ct).ConfigureAwait(false);
            episodes = [];
            foreach (var e in rows)
            {
                // A file holding two episodes answers for both.
                for (var n = e.EpisodeNumber!.Value; n <= (e.EpisodeNumberEnd ?? e.EpisodeNumber.Value) && n < e.EpisodeNumber.Value + 10; n++)
                {
                    episodes.TryAdd((e.SeasonNumber!.Value, n), e.Id);
                }
            }

            _episodes[title] = episodes;
        }

        return episodes.TryGetValue((season, number), out var id) ? id : null;
    }

    private static string? Tag(string kind, string? id) => string.IsNullOrEmpty(id) ? null : $"{kind}:{id}";
}

/// <summary>
/// Plays on Trakt of titles the server didn't have (see <see cref="TraktHistoryHold"/>), applied when they arrive: after
/// a library scan has finished. Cheap when nothing has changed: it remembers what the library and the holds looked like
/// last time and only looks again when either has moved; and then, as most moves are a refresh restamping titles, only
/// at the holds that are new or name a title added or refreshed since (with a full look once a day, to catch the rest).
/// </summary>
public static class TraktHolds
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static (int Titles, DateTime? Newest, DateTime? Matched, int Holds, long LastHold)? lastLook;
    private static DateTime lastLookAt;
    private static DateTime lastFullLookAt;

    /// <summary>How often every hold is looked at again, not only those a change since the last look could match.</summary>
    private static readonly TimeSpan FullLookEvery = TimeSpan.FromDays(1);

    /// <summary>
    /// How far before the last look a title's change still counts as new: a refresh stamps a title before saving it,
    /// so one saved just after a look can carry a stamp from just before it.
    /// </summary>
    private static readonly TimeSpan Overlap = TimeSpan.FromHours(6);

    /// <summary>Forgets what was seen last time, so the next call looks at every hold (tests).</summary>
    public static void LookAgain() => lastLook = null;

    /// <summary>Applies every hold whose title is in the library now; returns how many were applied.</summary>
    public static async Task<int> ApplyAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        // One at a time: two passes would add the same playback row, or delete the same hold, twice.
        await Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var holds = db.TraktHistoryHolds.AsNoTracking();
            var holdCount = await holds.CountAsync(ct).ConfigureAwait(false);
            if (holdCount == 0)
            {
                return 0;
            }

            // Films, shows and episodes (a new episode of a show already here can be what a hold waits for), and when
            // titles last had their ids filled in (a title can arrive first and be matched later).
            var titles = db.MediaItems.IgnoreQueryFilters()
                .Where(i => i.MissingSince == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Episode));
            var look = (await titles.CountAsync(ct).ConfigureAwait(false), await titles.MaxAsync(i => (DateTime?)i.AddedAt, ct).ConfigureAwait(false),
                await titles.MaxAsync(i => i.MetadataUpdatedAt, ct).ConfigureAwait(false), holdCount, await holds.MaxAsync(h => h.Id, ct).ConfigureAwait(false));
            if (lastLook == look)
            {
                return 0;
            }

            var now = DateTime.UtcNow;
            var full = lastLook is null || now - lastFullLookAt > FullLookEvery;
            var only = full ? null : await ChangedSinceAsync(db, lastLookAt - Overlap, lastLook!.Value.LastHold, ct).ConfigureAwait(false);
            var applied = await ApplyAllAsync(db, only, ct).ConfigureAwait(false);
            lastLook = applied == 0 ? look : null;
            lastLookAt = now;
            if (full)
            {
                lastFullLookAt = now;
            }

            return applied;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The holds that are new since the last look, or carry an id of a title added or refreshed since <paramref name="since"/>.</summary>
    private static async Task<IQueryable<TraktHistoryHold>> ChangedSinceAsync(FlexarrDbContext db, DateTime since, long lastHold, CancellationToken ct)
    {
        var changed = db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.MissingSince == null && (i.AddedAt > since || i.FirstSeenAt > since || i.MetadataUpdatedAt > since));
        // An episode arriving (or refreshed) is looked for by its show's ids.
        var titles = changed.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series)
            .Union(db.MediaItems.IgnoreQueryFilters().Where(s => s.Kind == MediaKind.Series
                && changed.Any(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == s.Id)));
        var ids = await titles.Select(i => new { i.TmdbId, i.TvdbId, i.ImdbId }).ToListAsync(ct).ConfigureAwait(false);
        var tmdb = ids.Where(i => i.TmdbId != null).Select(i => i.TmdbId!).Distinct().ToList();
        var tvdb = ids.Where(i => i.TvdbId != null).Select(i => i.TvdbId!).Distinct().ToList();
        var imdb = ids.Where(i => i.ImdbId != null).Select(i => i.ImdbId!).Distinct().ToList();
        return db.TraktHistoryHolds.Where(h => h.Id > lastHold
            || (h.TmdbId != null && tmdb.Contains(h.TmdbId)) || (h.TvdbId != null && tvdb.Contains(h.TvdbId)) || (h.ImdbId != null && imdb.Contains(h.ImdbId)));
    }

    /// <param name="db">The database.</param>
    /// <param name="only">The holds that could have a match now; null for every one.</param>
    /// <param name="ct">Stops the pass.</param>
    private static async Task<int> ApplyAllAsync(FlexarrDbContext db, IQueryable<TraktHistoryHold>? only, CancellationToken ct)
    {
        var titles = await LibraryTitles.LoadAsync(db, ct).ConfigureAwait(false);
        var applied = 0;
        // In pages, as an import can leave thousands; read untracked, and only matches are touched.
        long after = 0;
        var holds = (only ?? db.TraktHistoryHolds).AsNoTracking();
        while (true)
        {
            var page = await holds.Where(h => h.Id > after).OrderBy(h => h.Id).Take(500).ToListAsync(ct).ConfigureAwait(false);
            if (page.Count == 0)
            {
                break;
            }

            after = page[^1].Id;
            var found = new List<(TraktHistoryHold Hold, Guid Item)>();
            foreach (var hold in page)
            {
                if (await titles.FindAsync(new TitleKey(hold.Kind, hold.TmdbId, hold.TvdbId, hold.ImdbId, hold.SeasonNumber, hold.EpisodeNumber), ct).ConfigureAwait(false) is { } item)
                {
                    found.Add((hold, item));
                }
            }

            if (found.Count == 0)
            {
                continue;
            }

            var ids = found.Select(f => f.Item).Distinct().ToList();
            var profiles = found.Select(f => f.Hold.ProfileId).Distinct().ToList();
            var states = await db.PlaybackStates.Where(p => ids.Contains(p.MediaItemId) && profiles.Contains(p.UserId))
                .ToDictionaryAsync(p => (p.UserId, p.MediaItemId), ct).ConfigureAwait(false);
            foreach (var (hold, item) in found)
            {
                states.TryGetValue((hold.ProfileId, item), out var state);
                states[(hold.ProfileId, item)] = TraktMerge.ApplyPlay(db, state, hold.ProfileId, item, hold.WatchedAt).State!;
                applied++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var done = found.Select(f => f.Hold.Id).ToList();
            await db.TraktHistoryHolds.Where(h => done.Contains(h.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            // Not held onto: a scan's context is busy enough already.
            foreach (var state in states.Values)
            {
                db.Entry(state).State = EntityState.Detached;
            }
        }

        return applied;
    }
}
