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
    /// <summary>
    /// Records what's about to be removed for good, for items that have any history or collections. A film or show
    /// already handed over (since it went missing) is skipped: its history went to the new item, and what of a show's
    /// had nowhere to go is a tombstone already.
    /// </summary>
    public static async Task BuryAsync(FlexarrDbContext db, IQueryable<MediaItem> doomed, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(doomed);

        var notHandedOver = doomed.Where(i => i.Kind == MediaKind.Episode
            ? !(i.Parent!.Parent!.HandedOverAt != null && i.Parent.Parent.MissingSince != null && i.Parent.Parent.HandedOverAt >= i.Parent.Parent.MissingSince)
            : !(i.HandedOverAt != null && i.MissingSince != null && i.HandedOverAt >= i.MissingSince));
        await AddTombstonesAsync(db, notHandedOver, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private static async Task AddTombstonesAsync(FlexarrDbContext db, IQueryable<MediaItem> doomed, CancellationToken ct)
    {
        var rows = await doomed
            .Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Episode)
            .Where(i => i.PlaybackStates.Any() || db.CollectionItems.Any(c => c.MediaItemId == i.Id) || i.ParentId == null && i.KidsOverride != KidsOverride.None)
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
                // Episodes carry their show's, which its own tombstone keeps.
                KidsOverride = i.ParentId == null ? i.KidsOverride : KidsOverride.None,
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
                KidsOverride = row.KidsOverride,
            });
        }
    }

    /// <summary>
    /// Gives items on disk the history and collections of missing items and tombstones with the same ids. A profile
    /// that already has a state for the new item, or a collection that already holds it, keeps what it has. A missing
    /// item is handed over once: what of a show's history has nowhere to go yet is left as tombstones, for when those
    /// episodes come back.
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
                && (i.HandedOverAt == null || i.HandedOverAt < i.MissingSince)
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

        // What this run adds, so two sources for one title don't both add a row. Not by asking the change tracker: after
        // a full scan it holds every file and item in the library, and re-checks them all on each look.
        var added = new Added();
        var carried = 0;
        var handedOver = new List<Guid>();
        foreach (var from in missing)
        {
            if (present.FirstOrDefault(p => p.Same(from)) is not { } to)
            {
                continue;
            }

            await CopyCollectionsAsync(db, added, from.Id, to.Id, ct).ConfigureAwait(false);
            var kids = await items.Where(i => i.Id == from.Id).Select(i => i.KidsOverride).FirstAsync(ct).ConfigureAwait(false);
            await KeepKidsOverrideAsync(db, kids, to.Id, ct).ConfigureAwait(false);
            if (from.Kind == MediaKind.Movie)
            {
                carried += await CopyPlaybackAsync(db, added, [(from.Id, to.Id)], ct).ConfigureAwait(false);
            }
            else
            {
                var toEpisodes = await EpisodesAsync(db, to.Id, ct).ConfigureAwait(false);
                var pairs = new List<(Guid From, Guid To)>();
                var leftOver = new List<Guid>();
                foreach (var (key, episodeId) in await EpisodesAsync(db, from.Id, ct).ConfigureAwait(false))
                {
                    if (toEpisodes.TryGetValue(key, out var target))
                    {
                        pairs.Add((episodeId, target));
                    }
                    else
                    {
                        leftOver.Add(episodeId);
                    }
                }

                carried += await CopyPlaybackAsync(db, added, pairs, ct).ConfigureAwait(false);
                if (leftOver.Count > 0)
                {
                    // Not back (yet) under the new folder: kept as if removed, so they're picked up when they are.
                    await AddTombstonesAsync(db, items.Where(i => leftOver.Contains(i.Id)), ct).ConfigureAwait(false);
                }
            }

            handedOver.Add(from.Id);
        }

        var episodesBySeries = new Dictionary<Guid, Dictionary<(int?, int?, DateOnly?), Guid>>();
        HashSet<string>? users = null;
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

            users ??= (await db.Profiles.Select(p => p.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            if (tombstone.Kind != MediaKind.Episode)
            {
                await KeepKidsOverrideAsync(db, tombstone.KidsOverride, target, ct).ConfigureAwait(false);
            }

            carried += await RestoreAsync(db, added, users, tombstone, target, ct).ConfigureAwait(false);
            db.WatchTombstones.Remove(tombstone);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (handedOver.Count > 0)
        {
            // Done with: later scans skip it (and its show's left-overs are tombstones now) unless it goes missing anew.
            var now = DateTime.UtcNow;
            await items.Where(i => handedOver.Contains(i.Id)).ExecuteUpdateAsync(u => u.SetProperty(i => i.HandedOverAt, now), ct).ConfigureAwait(false);
        }

        return carried;
    }

    /// <summary>
    /// Gives title <paramref name="to"/> a parent's Allow or Block from the one it replaces, unless a parent has said
    /// something of their own about it since. Its seasons and episodes follow.
    /// </summary>
    private static async Task KeepKidsOverrideAsync(FlexarrDbContext db, KidsOverride kids, Guid to, CancellationToken ct)
    {
        if (kids == KidsOverride.None
            || await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == to && i.KidsOverride == KidsOverride.None)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.KidsOverride, kids), ct).ConfigureAwait(false) == 0)
        {
            return;
        }

        await db.PassDownRatingsAsync([to], ct).ConfigureAwait(false);
    }

    /// <summary>The watch states and collection memberships this run has added but not yet saved.</summary>
    private sealed class Added
    {
        public HashSet<(Guid MediaItemId, string UserId)> States { get; } = [];

        public HashSet<(Guid CollectionId, Guid MediaItemId)> Memberships { get; } = [];
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

    /// <param name="db">The database.</param>
    /// <param name="added">What this run has added so far.</param>
    /// <param name="pairs">Each item whose states are copied, with the item that gets them: a whole show's at once.</param>
    /// <param name="ct">Stops the copy.</param>
    private static async Task<int> CopyPlaybackAsync(FlexarrDbContext db, Added added, List<(Guid From, Guid To)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return 0;
        }

        var fromIds = pairs.Select(p => p.From).ToList();
        var states = await db.PlaybackStates.AsNoTracking().Where(p => fromIds.Contains(p.MediaItemId)).ToListAsync(ct).ConfigureAwait(false);
        if (states.Count == 0)
        {
            return 0;
        }

        var target = pairs.ToDictionary(p => p.From, p => p.To);
        var toIds = states.Select(s => target[s.MediaItemId]).Distinct().ToList();
        var had = (await db.PlaybackStates.Where(p => toIds.Contains(p.MediaItemId)).Select(p => new { p.MediaItemId, p.UserId })
            .ToListAsync(ct).ConfigureAwait(false)).Select(p => (p.MediaItemId, p.UserId)).ToHashSet();
        var copied = 0;
        foreach (var state in states)
        {
            var to = target[state.MediaItemId];
            if (had.Contains((to, state.UserId)) || !added.States.Add((to, state.UserId)))
            {
                continue;
            }

            state.MediaItemId = to;
            state.MediaItem = null;
            db.PlaybackStates.Add(state);
            copied++;
        }

        return copied;
    }

    private static async Task CopyCollectionsAsync(FlexarrDbContext db, Added added, Guid from, Guid to, CancellationToken ct)
    {
        var memberships = await db.CollectionItems.AsNoTracking().Where(c => c.MediaItemId == from)
            .Select(c => new TombstoneCollection(c.CollectionId, c.Position)).ToListAsync(ct).ConfigureAwait(false);
        await AddCollectionsAsync(db, added, memberships, to, ct).ConfigureAwait(false);
    }

    private static async Task AddCollectionsAsync(FlexarrDbContext db, Added added, List<TombstoneCollection> memberships, Guid to, CancellationToken ct)
    {
        if (memberships.Count == 0)
        {
            return;
        }

        var ids = memberships.Select(m => m.CollectionId).ToList();
        var stillThere = (await db.Collections.Where(c => ids.Contains(c.Id)).Select(c => c.Id).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        var already = (await db.CollectionItems.Where(c => c.MediaItemId == to).Select(c => c.CollectionId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var m in memberships.Where(m => stillThere.Contains(m.CollectionId) && !already.Contains(m.CollectionId)
            && added.Memberships.Add((m.CollectionId, to))))
        {
            db.CollectionItems.Add(new CollectionItem { CollectionId = m.CollectionId, MediaItemId = to, Position = m.Position });
        }
    }

    private static async Task<int> RestoreAsync(FlexarrDbContext db, Added added, HashSet<string> users, WatchTombstone tombstone, Guid to, CancellationToken ct)
    {
        await AddCollectionsAsync(db, added, tombstone.Collections, to, ct).ConfigureAwait(false);
        var had = (await db.PlaybackStates.Where(p => p.MediaItemId == to).Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        var restored = 0;
        foreach (var p in tombstone.Playback.Where(p => users.Contains(p.UserId) && !had.Contains(p.UserId)
            && added.States.Add((to, p.UserId))))
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
