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
    /// <summary>Applies a play at <paramref name="watchedAt"/>; true when it changed anything.</summary>
    public static bool ApplyPlay(FlexarrDbContext db, PlaybackState? state, string profileId, Guid itemId, DateTime watchedAt)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (state is null)
        {
            db.PlaybackStates.Add(new PlaybackState { UserId = profileId, MediaItemId = itemId, Watched = true, PlayCount = 1, LastPlayedAt = watchedAt });
            return true;
        }

        if (state.Watched)
        {
            if (watchedAt > state.LastPlayedAt)
            {
                state.LastPlayedAt = watchedAt;
                return true;
            }

            return false;
        }

        if (state.LastPlayedAt >= watchedAt)
        {
            // Changed here since that play: marked unwatched by hand, or being watched again. That stands.
            return false;
        }

        state.Watched = true;
        state.PositionSeconds = 0;
        state.PlayCount = Math.Max(state.PlayCount, 1);
        state.LastPlayedAt = watchedAt;
        return true;
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
/// Plays on Trakt of titles the server didn't have (see <see cref="TraktHistoryHold"/>), applied when they arrive:
/// after a scan, beside <see cref="WatchHistoryCarryOver"/>, and after each Trakt sync.
/// </summary>
public static class TraktHolds
{
    /// <summary>Applies every hold whose title is in the library now; returns how many were applied.</summary>
    public static async Task<int> ApplyAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (!await db.TraktHistoryHolds.AnyAsync(ct).ConfigureAwait(false))
        {
            return 0;
        }

        var titles = await LibraryTitles.LoadAsync(db, ct).ConfigureAwait(false);
        var applied = 0;
        // In pages, as an import can leave thousands.
        long after = 0;
        while (true)
        {
            var page = await db.TraktHistoryHolds.Where(h => h.Id > after).OrderBy(h => h.Id).Take(500).ToListAsync(ct).ConfigureAwait(false);
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
                if (TraktMerge.ApplyPlay(db, state, hold.ProfileId, item, hold.WatchedAt) && state is null)
                {
                    // Added by ApplyPlay; find it again should a second hold name the same episode.
                    states[(hold.ProfileId, item)] = db.PlaybackStates.Local.First(p => p.UserId == hold.ProfileId && p.MediaItemId == item);
                }

                db.TraktHistoryHolds.Remove(hold);
                applied++;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return applied;
    }
}
