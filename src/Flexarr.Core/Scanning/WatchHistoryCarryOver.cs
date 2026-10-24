using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Scanning;

/// <summary>
/// Keeps watch history and collections with a title when its files come back somewhere new: Radarr or Sonarr renamed
/// the folder, or a drive stayed away past the grace period. The new item, matched by TMDB, TVDB or IMDb id (and, for
/// an episode, its show's ids and its number), takes them from the missing item it replaces, or from the
/// <see cref="WatchTombstone"/> left when that was removed.
/// </summary>
public static class WatchHistoryCarryOver
{
    /// <summary>Records what's about to be removed for good, for items that have any history or collections.</summary>
    public static async Task BuryAsync(FlexarrDbContext db, IQueryable<MediaItem> doomed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doomed);

        var rows = await doomed
            .Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Episode)
            .Where(i => i.PlaybackStates.Any() || db.CollectionItems.Any(c => c.MediaItemId == i.Id))
            .Select(i => new
            {
                i.Kind,
                TmdbId = i.Kind == MediaKind.Episode ? i.Parent!.Parent!.TmdbId : i.TmdbId,
                TvdbId = i.Kind == MediaKind.Episode ? i.Parent!.Parent!.TvdbId : i.TvdbId,
                ImdbId = i.Kind == MediaKind.Episode ? i.Parent!.Parent!.ImdbId : i.ImdbId,
                i.SeasonNumber,
                i.EpisodeNumber,
                i.AirDate,
                Playback = i.PlaybackStates.Select(p => new TombstonePlayback(p.UserId, p.PositionSeconds, p.DurationSeconds, p.Watched, p.PlayCount,
                    p.LastPlayedAt, p.AudioLanguage, p.SubtitleLanguage)).ToList(),
                Collections = db.CollectionItems.Where(c => c.MediaItemId == i.Id).Select(c => new TombstoneCollection(c.CollectionId, c.Position)).ToList(),
            })
            .AsNoTracking().ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows.Where(r => r.TmdbId is not null || r.TvdbId is not null || r.ImdbId is not null))
        {
            db.WatchTombstones.Add(new WatchTombstone
            {
                Kind = row.Kind,
                TmdbId = row.TmdbId,
                TvdbId = row.TvdbId,
                ImdbId = row.ImdbId,
                SeasonNumber = row.SeasonNumber,
                EpisodeNumber = row.EpisodeNumber,
                AirDate = row.AirDate,
                Playback = row.Playback,
                Collections = row.Collections,
            });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Gives items on disk the history and collections of missing items and tombstones with the same ids. A profile
    /// that already has a state for the new item, or a collection that already holds it, keeps what it has.
    /// </summary>
    /// <returns>How many watch states were carried over.</returns>
    public static async Task<int> CarryOverAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        var expired = DateTime.UtcNow - WatchTombstone.Lifetime;
        await db.WatchTombstones.Where(t => t.RemovedAt < expired).ExecuteDeleteAsync(ct).ConfigureAwait(false);

        var items = db.MediaItems.IgnoreQueryFilters();
        var missing = await items
            .Where(i => i.MissingSince != null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series)
                && (i.TmdbId != null || i.TvdbId != null || i.ImdbId != null))
            .Select(i => new Title(i.Id, i.Kind, i.TmdbId, i.TvdbId, i.ImdbId))
            .ToListAsync(ct).ConfigureAwait(false);
        var tombstones = await db.WatchTombstones.ToListAsync(ct).ConfigureAwait(false);
        if (missing.Count == 0 && tombstones.Count == 0)
        {
            return 0;
        }

        var sources = missing.Concat(tombstones.Select(t => new Title(Guid.Empty, t.Kind == MediaKind.Episode ? MediaKind.Series : t.Kind, t.TmdbId, t.TvdbId, t.ImdbId))).ToList();
        var tmdb = sources.Select(s => s.TmdbId).OfType<string>().Distinct().ToList();
        var tvdb = sources.Select(s => s.TvdbId).OfType<string>().Distinct().ToList();
        var imdb = sources.Select(s => s.ImdbId).OfType<string>().Distinct().ToList();
        var present = await items
            .Where(i => i.MissingSince == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series)
                && (tmdb.Contains(i.TmdbId!) || tvdb.Contains(i.TvdbId!) || imdb.Contains(i.ImdbId!)))
            .Select(i => new Title(i.Id, i.Kind, i.TmdbId, i.TvdbId, i.ImdbId))
            .ToListAsync(ct).ConfigureAwait(false);
        if (present.Count == 0)
        {
            return 0;
        }

        var carried = 0;
        foreach (var from in missing)
        {
            if (present.FirstOrDefault(p => p.Same(from)) is not { } to)
            {
                continue;
            }

            await CopyCollectionsAsync(db, from.Id, to.Id, ct).ConfigureAwait(false);
            if (from.Kind == MediaKind.Movie)
            {
                carried += await CopyPlaybackAsync(db, from.Id, to.Id, ct).ConfigureAwait(false);
                continue;
            }

            var toEpisodes = await EpisodesAsync(db, to.Id, ct).ConfigureAwait(false);
            foreach (var (key, episodeId) in await EpisodesAsync(db, from.Id, ct).ConfigureAwait(false))
            {
                if (toEpisodes.TryGetValue(key, out var target))
                {
                    carried += await CopyPlaybackAsync(db, episodeId, target, ct).ConfigureAwait(false);
                }
            }
        }

        var episodesBySeries = new Dictionary<Guid, Dictionary<(int?, int?, DateOnly?), Guid>>();
        foreach (var tombstone in tombstones)
        {
            var kind = tombstone.Kind == MediaKind.Episode ? MediaKind.Series : tombstone.Kind;
            if (present.FirstOrDefault(p => p.Same(new Title(Guid.Empty, kind, tombstone.TmdbId, tombstone.TvdbId, tombstone.ImdbId))) is not { } to)
            {
                continue;
            }

            var target = to.Id;
            if (tombstone.Kind == MediaKind.Episode)
            {
                if (!episodesBySeries.TryGetValue(to.Id, out var episodes))
                {
                    episodes = episodesBySeries[to.Id] = await EpisodesAsync(db, to.Id, ct).ConfigureAwait(false);
                }

                if (!episodes.TryGetValue((tombstone.SeasonNumber, tombstone.EpisodeNumber, tombstone.AirDate), out target))
                {
                    continue; // That episode isn't back (yet).
                }
            }

            carried += await RestoreAsync(db, tombstone, target, ct).ConfigureAwait(false);
            db.WatchTombstones.Remove(tombstone);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return carried;
    }

    private sealed record Title(Guid Id, MediaKind Kind, string? TmdbId, string? TvdbId, string? ImdbId)
    {
        public bool Same(Title other) => other.Kind == Kind && other.Id != Id
            && (Match(TmdbId, other.TmdbId) || Match(TvdbId, other.TvdbId) || Match(ImdbId, other.ImdbId));

        private static bool Match(string? a, string? b) => a is not null && a == b;
    }

    /// <summary>A show's episodes by season and number (or air date, for daily shows).</summary>
    private static async Task<Dictionary<(int?, int?, DateOnly?), Guid>> EpisodesAsync(FlexarrDbContext db, Guid seriesId, CancellationToken ct)
    {
        var episodes = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.Kind == MediaKind.Episode && i.Parent!.ParentId == seriesId)
            .Select(i => new { i.Id, i.SeasonNumber, i.EpisodeNumber, i.AirDate })
            .ToListAsync(ct).ConfigureAwait(false);
        var byKey = new Dictionary<(int?, int?, DateOnly?), Guid>();
        foreach (var e in episodes)
        {
            byKey.TryAdd((e.SeasonNumber, e.EpisodeNumber, e.AirDate), e.Id);
        }

        return byKey;
    }

    private static async Task<int> CopyPlaybackAsync(FlexarrDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        var states = await db.PlaybackStates.AsNoTracking().Where(p => p.MediaItemId == from).ToListAsync(ct).ConfigureAwait(false);
        if (states.Count == 0)
        {
            return 0;
        }

        var had = (await db.PlaybackStates.Where(p => p.MediaItemId == to).Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var copied = 0;
        foreach (var state in states.Where(s => !had.Contains(s.UserId) && !db.PlaybackStates.Local.Any(l => l.MediaItemId == to && l.UserId == s.UserId)))
        {
            state.MediaItemId = to;
            state.MediaItem = null;
            db.PlaybackStates.Add(state);
            copied++;
        }

        return copied;
    }

    private static async Task CopyCollectionsAsync(FlexarrDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        var memberships = await db.CollectionItems.AsNoTracking().Where(c => c.MediaItemId == from)
            .Select(c => new TombstoneCollection(c.CollectionId, c.Position)).ToListAsync(ct).ConfigureAwait(false);
        await AddCollectionsAsync(db, memberships, to, ct).ConfigureAwait(false);
    }

    private static async Task AddCollectionsAsync(FlexarrDbContext db, List<TombstoneCollection> memberships, Guid to, CancellationToken ct)
    {
        if (memberships.Count == 0)
        {
            return;
        }

        var ids = memberships.Select(m => m.CollectionId).ToList();
        var stillThere = (await db.Collections.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var already = (await db.CollectionItems.Where(c => c.MediaItemId == to).Select(c => c.CollectionId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var m in memberships.Where(m => stillThere.Contains(m.CollectionId) && !already.Contains(m.CollectionId)
            && !db.CollectionItems.Local.Any(l => l.CollectionId == m.CollectionId && l.MediaItemId == to)))
        {
            db.CollectionItems.Add(new CollectionItem { CollectionId = m.CollectionId, MediaItemId = to, Position = m.Position });
        }
    }

    private static async Task<int> RestoreAsync(FlexarrDbContext db, WatchTombstone tombstone, Guid to, CancellationToken ct)
    {
        await AddCollectionsAsync(db, tombstone.Collections, to, ct).ConfigureAwait(false);
        var had = (await db.PlaybackStates.Where(p => p.MediaItemId == to).Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var users = (await db.Profiles.Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var restored = 0;
        foreach (var p in tombstone.Playback.Where(p => users.Contains(p.UserId) && !had.Contains(p.UserId)
            && !db.PlaybackStates.Local.Any(l => l.MediaItemId == to && l.UserId == p.UserId)))
        {
            db.PlaybackStates.Add(new PlaybackState
            {
                UserId = p.UserId,
                MediaItemId = to,
                PositionSeconds = p.PositionSeconds,
                DurationSeconds = p.DurationSeconds,
                Watched = p.Watched,
                PlayCount = p.PlayCount,
                LastPlayedAt = p.LastPlayedAt,
                AudioLanguage = p.AudioLanguage,
                SubtitleLanguage = p.SubtitleLanguage,
            });
            restored++;
        }

        return restored;
    }
}
