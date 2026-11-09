using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Scanning;

/// <summary>
/// Works out which films and shows are copies of one title, from their ids, and marks them and their seasons and
/// episodes with <see cref="MediaItem.LinkGroupId"/>. See docs/multi-versions.md. Safe to run at any time, and it finds
/// little to do: only what changed is written.
/// </summary>
public static class TitleLinker
{
    /// <summary>More titles than this with one id means a bad id, not that many copies. It links nothing.</summary>
    private const int MostCopies = 6;

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Returns how many items' links changed.</summary>
    public static async Task<int> LinkAsync(FlexarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.MissingSince == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series || i.Kind == MediaKind.Season || i.Kind == MediaKind.Episode))
                .Select(i => new Row(i.Id, i.ParentId, i.Kind, i.TmdbId, i.TvdbId, i.TvMazeId, i.ImdbId, i.SeasonNumber, i.EpisodeNumber, i.EpisodeNumberEnd, i.AirDate, i.LinkGroupId))
                .ToListAsync(ct).ConfigureAwait(false);
            var missing = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.MissingSince != null && i.LinkGroupId != null).Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false);

            var wanted = Groups(rows);
            var changes = rows.Where(r => r.LinkGroupId != (wanted.TryGetValue(r.Id, out var group) ? group : null)).ToList();
            if (changes.Count == 0 && missing.Count == 0)
            {
                return 0;
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            var known = await db.TitleGroups.Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
            db.TitleGroups.AddRange(wanted.Values.Distinct().Except(known).Select(id => new TitleGroup { Id = id }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var chunk in missing.Chunk(500))
            {
                await db.MediaItems.IgnoreQueryFilters().Where(i => chunk.Contains(i.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.LinkGroupId, (Guid?)null), ct).ConfigureAwait(false);
            }

            foreach (var change in changes.GroupBy(c => wanted.TryGetValue(c.Id, out var group) ? group : (Guid?)null))
            {
                var linkTo = change.Key;
                foreach (var chunk in change.Select(c => c.Id).Chunk(500))
                {
                    await db.MediaItems.IgnoreQueryFilters().Where(i => chunk.Contains(i.Id))
                        .ExecuteUpdateAsync(u => u.SetProperty(i => i.LinkGroupId, linkTo), ct).ConfigureAwait(false);
                }
            }

            var used = wanted.Values.ToHashSet();
            foreach (var chunk in known.Where(id => !used.Contains(id)).Chunk(500))
            {
                await db.TitleGroups.Where(g => chunk.Contains(g.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            var joined = changes.Where(c => c.Kind is MediaKind.Movie or MediaKind.Episode && wanted.ContainsKey(c.Id)).Select(c => wanted[c.Id]).ToHashSet();
            var members = rows.Where(r => wanted.TryGetValue(r.Id, out var group) && joined.Contains(group)).ToLookup(r => wanted[r.Id], r => r.Id);
            await ShareWatchStateAsync(db, members, ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
            CatalogVersion.Bump();
            DataVersion.Bump();
            return changes.Count + missing.Count;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>Each item that has copies, and the id of its group: the lowest of theirs, so it stays put across runs.</summary>
    private static Dictionary<Guid, Guid> Groups(List<Row> rows)
    {
        var groups = new Dictionary<Guid, Guid>();
        foreach (var kind in new[] { MediaKind.Movie, MediaKind.Series })
        {
            var titles = rows.Where(r => r.Kind == kind).ToList();
            var parent = titles.ToDictionary(t => t.Id, t => t.Id);

            Guid Find(Guid id)
            {
                while (parent[id] != id)
                {
                    parent[id] = parent[parent[id]];
                    id = parent[id];
                }

                return id;
            }

            foreach (var key in new Func<Row, string?>[] { r => r.TmdbId, r => r.TvdbId, r => r.TvMazeId, r => r.ImdbId })
            {
                foreach (var sharing in titles.Where(t => !string.IsNullOrEmpty(key(t))).GroupBy(key, StringComparer.OrdinalIgnoreCase))
                {
                    var ids = sharing.Select(t => t.Id).ToList();
                    if (ids.Count > MostCopies)
                    {
                        continue;
                    }

                    foreach (var id in ids.Skip(1))
                    {
                        parent[Find(id)] = Find(ids[0]);
                    }
                }
            }

            foreach (var group in titles.GroupBy(t => Find(t.Id)).Where(g => g.Count() > 1))
            {
                AddGroup(groups, group.Select(t => t.Id));
            }
        }

        // A linked show's seasons by number, then a linked season's episodes by number or air date.
        var seasons = rows.Where(r => r.Kind == MediaKind.Season && r.ParentId is { } show && groups.ContainsKey(show) && r.SeasonNumber is not null)
            .GroupBy(r => (Show: groups[r.ParentId!.Value], r.SeasonNumber));
        foreach (var season in seasons.Where(s => s.Count() > 1).ToList())
        {
            AddGroup(groups, season.Select(s => s.Id));
        }

        var episodes = rows.Where(r => r.Kind == MediaKind.Episode && r.ParentId is { } season && groups.ContainsKey(season) && (r.EpisodeNumber is not null || r.AirDate is not null))
            .GroupBy(r => (Season: groups[r.ParentId!.Value], r.EpisodeNumber, End: r.EpisodeNumberEnd ?? r.EpisodeNumber, r.AirDate));
        foreach (var episode in episodes.Where(e => e.Count() > 1).ToList())
        {
            AddGroup(groups, episode.Select(e => e.Id));
        }

        return groups;
    }

    private static void AddGroup(Dictionary<Guid, Guid> groups, IEnumerable<Guid> ids)
    {
        var members = ids.ToList();
        var lowest = members.Min();
        foreach (var id in members)
        {
            groups[id] = lowest;
        }
    }

    /// <summary>
    /// Copies that have just linked agree what's been watched, for each profile: watched if any copy was, and the place
    /// and details of whichever was played last.
    /// </summary>
    private static async Task ShareWatchStateAsync(FlexarrDbContext db, ILookup<Guid, Guid> members, CancellationToken ct)
    {
        var ids = members.SelectMany(m => m).ToList();
        var states = new List<PlaybackState>();
        foreach (var chunk in ids.Chunk(500))
        {
            states.AddRange(await db.PlaybackStates.Where(p => chunk.Contains(p.MediaItemId)).ToListAsync(ct).ConfigureAwait(false));
        }

        var byCopy = states.ToDictionary(s => (s.UserId, s.MediaItemId));
        var groupOf = members.SelectMany(m => m.Select(id => (id, m.Key))).ToDictionary(x => x.id, x => x.Key);
        foreach (var group in states.GroupBy(s => (s.UserId, Group: groupOf[s.MediaItemId])))
        {
            var newest = group.OrderByDescending(s => s.LastPlayedAt).First();
            var watched = group.Any(s => s.Watched);
            var plays = group.Max(s => s.PlayCount);
            foreach (var copy in members[group.Key.Group])
            {
                if (!byCopy.TryGetValue((group.Key.UserId, copy), out var state))
                {
                    state = new PlaybackState { UserId = group.Key.UserId, MediaItemId = copy };
                    db.PlaybackStates.Add(state);
                }

                if (!ReferenceEquals(state, newest))
                {
                    state.CopyWatchStateFrom(newest);
                }

                state.Watched = watched;
                state.PlayCount = plays;
            }
        }

        db.LeaveWatchStateAlone = true;
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            db.LeaveWatchStateAlone = false;
        }
    }

    private sealed record Row(Guid Id, Guid? ParentId, MediaKind Kind, string? TmdbId, string? TvdbId, string? TvMazeId, string? ImdbId,
        int? SeasonNumber, int? EpisodeNumber, int? EpisodeNumberEnd, DateOnly? AirDate, Guid? LinkGroupId);
}
