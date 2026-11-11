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

    private const int IdsPerStatement = 500;

    private const int ItemsPerBatch = 200;

    private static readonly System.Linq.Expressions.Expression<Func<MediaItem, Row>> AsRow = i =>
        new Row(i.Id, i.ParentId, i.Kind, i.TmdbId, i.TvdbId, i.TvMazeId, i.ImdbId, i.SeasonNumber, i.EpisodeNumber, i.EpisodeNumberEnd, i.AirDate, i.LinkGroupId, i.KidsOverride);

    private static readonly SemaphoreSlim OneAtATime = new(1, 1);

    /// <summary>Most rounds of following ids from title to title in a scoped pass before it looks at everything instead.</summary>
    private const int MostRounds = 8;

    /// <summary>Returns how many items' links changed.</summary>
    public static Task<int> LinkAsync(FlexarrDbContext db, CancellationToken ct) => LinkAsync(db, null, ct);

    /// <summary>Returns how many items' links changed.</summary>
    /// <param name="db">The database.</param>
    /// <param name="touched">
    /// Items whose copies may have changed (films, shows, or their seasons and episodes): only their titles, every title
    /// sharing an id or a link with those, and any copy left on its own are looked at, which links them as a whole pass
    /// would. Null for a whole pass (at startup, after a full scan).
    /// </param>
    /// <param name="ct">Stops it.</param>
    public static async Task<int> LinkAsync(FlexarrDbContext db, IReadOnlyCollection<Guid>? touched, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);

        await OneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var rows = touched is null ? null : await ScopedRowsAsync(db, touched, ct).ConfigureAwait(false);
            var scoped = rows is not null;
            rows ??= await RowsAsync(db, null, ct).ConfigureAwait(false);
            var missing = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                .Where(i => i.MissingSince != null && i.LinkGroupId != null).Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false);

            var wanted = Groups(rows);
            var changes = rows.Where(r => r.LinkGroupId != (wanted.TryGetValue(r.Id, out var group) ? group : null)).ToList();
            if (changes.Count == 0 && missing.Count == 0)
            {
                return 0;
            }

            // A scoped pass knows only the groups it looked at: the others aren't its to add or remove.
            List<Guid> known;
            if (scoped)
            {
                var looked = wanted.Values.Concat(rows.Select(r => r.LinkGroupId).OfType<Guid>()).Distinct().ToList();
                known = [];
                foreach (var chunk in looked.Chunk(IdsPerStatement))
                {
                    known.AddRange(await db.TitleGroups.Where(g => chunk.Contains(g.Id)).Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false));
                }
            }
            else
            {
                known = await db.TitleGroups.Select(g => g.Id).ToListAsync(ct).ConfigureAwait(false);
            }

            var added = wanted.Values.Distinct().Except(known).Select(id => new TitleGroup { Id = id }).ToList();
            db.TitleGroups.AddRange(added);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            foreach (var group in added)
            {
                db.Entry(group).State = EntityState.Detached;
            }

            foreach (var chunk in missing.Chunk(IdsPerStatement))
            {
                await db.MediaItems.IgnoreQueryFilters().Where(i => chunk.Contains(i.Id))
                    .ExecuteUpdateAsync(u => u.SetProperty(i => i.LinkGroupId, (Guid?)null), ct).ConfigureAwait(false);
            }

            // A few groups at a time, each with its shared watch state, so nothing else waits long to write. If this stops
            // part way, the next run carries on: what's done stays done, and a group's link and state always go together.
            foreach (var batch in Batches(changes, wanted))
            {
                await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                foreach (var change in batch)
                {
                    foreach (var chunk in change.Select(c => c.Id).Chunk(IdsPerStatement))
                    {
                        await db.MediaItems.IgnoreQueryFilters().Where(i => chunk.Contains(i.Id))
                            .ExecuteUpdateAsync(u => u.SetProperty(i => i.LinkGroupId, change.Key), ct).ConfigureAwait(false);
                    }
                }

                var joined = batch.SelectMany(c => c).Where(c => c.Kind is MediaKind.Movie or MediaKind.Episode && wanted.ContainsKey(c.Id)).Select(c => wanted[c.Id]).ToHashSet();
                var members = rows.Where(r => wanted.TryGetValue(r.Id, out var group) && joined.Contains(group)).ToLookup(r => wanted[r.Id], r => r.Id);
                await ShareWatchStateAsync(db, members, ct).ConfigureAwait(false);
                await AgreeKidsOverridesAsync(db, batch, rows, wanted, ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }

            var used = wanted.Values.ToHashSet();
            foreach (var chunk in known.Where(id => !used.Contains(id)).Chunk(IdsPerStatement))
            {
                // In a scoped pass, never one something it didn't look at still has: deleting it would unlink that.
                await db.TitleGroups.Where(g => chunk.Contains(g.Id) && (!scoped || !db.MediaItems.IgnoreQueryFilters().Any(i => i.LinkGroupId == g.Id)))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            }

            CatalogVersion.Bump();
            DataVersion.Bump();
            return changes.Count + missing.Count;
        }
        finally
        {
            OneAtATime.Release();
        }
    }

    /// <summary>
    /// Every film and show, and only the seasons and episodes that could have copies or already have a link: a library
    /// with no second copies never reads its episodes.
    /// </summary>
    /// <param name="db">The database.</param>
    /// <param name="titles">The films and shows to look at, and only their seasons and episodes; null for all of them.</param>
    /// <param name="ct">Stops it.</param>
    private static async Task<List<Row>> RowsAsync(FlexarrDbContext db, List<Row>? titles, CancellationToken ct)
    {
        var items = db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.MissingSince == null);
        var rows = titles ?? await items.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series).Select(AsRow).ToListAsync(ct).ConfigureAwait(false);

        var shows = Groups(rows).Keys.Intersect(rows.Where(r => r.Kind == MediaKind.Series).Select(r => r.Id)).ToList();
        rows.AddRange(await ChildrenAsync(items, MediaKind.Season, shows, ct).ConfigureAwait(false));
        var seasons = Groups(rows).Keys.Intersect(rows.Where(r => r.Kind == MediaKind.Season).Select(r => r.Id)).ToList();
        rows.AddRange(await ChildrenAsync(items, MediaKind.Episode, seasons, ct).ConfigureAwait(false));

        // Whatever else is linked now, which may need unlinking.
        if (titles is null)
        {
            rows.AddRange(await items.Where(i => i.LinkGroupId != null && (i.Kind == MediaKind.Season || i.Kind == MediaKind.Episode)).Select(AsRow).ToListAsync(ct).ConfigureAwait(false));
        }
        else
        {
            foreach (var chunk in titles.Where(r => r.Kind == MediaKind.Series).Select(r => r.Id).ToList().Chunk(IdsPerStatement))
            {
                rows.AddRange(await items.Where(i => i.LinkGroupId != null && i.ParentId != null
                        && ((i.Kind == MediaKind.Season && chunk.Contains(i.ParentId.Value)) || (i.Kind == MediaKind.Episode && i.Parent!.ParentId != null && chunk.Contains(i.Parent.ParentId.Value))))
                    .Select(AsRow).ToListAsync(ct).ConfigureAwait(false));
            }
        }

        return rows.DistinctBy(r => r.Id).ToList();
    }

    /// <summary>
    /// The rows a scoped pass looks at: the titles of <paramref name="touched"/>, those of any copy left on its own (its
    /// other went without a scan of its folder), and, round by round, every title sharing an id or a link with one
    /// already in, so each title's copies are all there to be grouped as a whole pass would. Null when that keeps
    /// growing: then a whole pass it is.
    /// </summary>
    private static async Task<List<Row>?> ScopedRowsAsync(FlexarrDbContext db, IReadOnlyCollection<Guid> touched, CancellationToken ct)
    {
        var all = db.MediaItems.IgnoreQueryFilters().AsNoTracking();
        var items = all.Where(i => i.MissingSince == null);
        var titles = items.Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series);

        // Each touched item's title, gone or not, and the links it and its title had.
        var next = new HashSet<Guid>();
        var groups = new HashSet<Guid>();
        foreach (var chunk in touched.Distinct().Chunk(IdsPerStatement))
        {
            foreach (var seed in await all.Where(i => chunk.Contains(i.Id))
                .Select(i => new { Title = i.Parent!.ParentId ?? i.ParentId ?? i.Id, i.LinkGroupId, TitleGroup = i.Parent!.Parent!.LinkGroupId ?? i.Parent!.LinkGroupId })
                .ToListAsync(ct).ConfigureAwait(false))
            {
                next.Add(seed.Title);
                groups.UnionWith(new[] { seed.LinkGroupId, seed.TitleGroup }.OfType<Guid>());
            }
        }

        // Links down to one item: what's left of a title, season or episode whose other copy has gone.
        var alone = await items.Where(i => i.LinkGroupId != null).GroupBy(i => i.LinkGroupId!.Value).Where(g => g.Count() < 2).Select(g => g.Key).ToListAsync(ct).ConfigureAwait(false);
        foreach (var chunk in alone.Chunk(IdsPerStatement))
        {
            next.UnionWith(await items.Where(i => i.LinkGroupId != null && chunk.Contains(i.LinkGroupId.Value))
                .Select(i => i.Parent!.ParentId ?? i.ParentId ?? i.Id).ToListAsync(ct).ConfigureAwait(false));
        }

        var rows = new Dictionary<Guid, Row>();
        var asked = new HashSet<Guid>();
        for (var round = 0; next.Count > 0 || groups.Count > 0; round++)
        {
            if (round == MostRounds)
            {
                return null;
            }

            var found = new List<Row>();
            foreach (var chunk in next.Chunk(IdsPerStatement))
            {
                found.AddRange(await titles.Where(i => chunk.Contains(i.Id)).Select(AsRow).ToListAsync(ct).ConfigureAwait(false));
            }

            asked.UnionWith(groups);
            foreach (var chunk in groups.Chunk(IdsPerStatement))
            {
                found.AddRange(await titles.Where(i => i.LinkGroupId != null && chunk.Contains(i.LinkGroupId.Value)).Select(AsRow).ToListAsync(ct).ConfigureAwait(false));
            }

            var fresh = found.Where(r => rows.TryAdd(r.Id, r)).ToList();
            next = [];
            async Task SharingAsync(Func<Row, string?> key, Func<string[], IQueryable<MediaItem>> sharing)
            {
                foreach (var chunk in fresh.Select(key).Where(id => !string.IsNullOrEmpty(id)).Select(id => id!).Distinct().Chunk(IdsPerStatement))
                {
                    next.UnionWith((await sharing(chunk).Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false)).Where(id => !rows.ContainsKey(id)));
                }
            }

            await SharingAsync(r => r.TmdbId, chunk => titles.Where(i => i.TmdbId != null && chunk.Contains(i.TmdbId))).ConfigureAwait(false);
            await SharingAsync(r => r.TvdbId, chunk => titles.Where(i => i.TvdbId != null && chunk.Contains(i.TvdbId))).ConfigureAwait(false);
            await SharingAsync(r => r.TvMazeId, chunk => titles.Where(i => i.TvMazeId != null && chunk.Contains(i.TvMazeId))).ConfigureAwait(false);
            await SharingAsync(r => r.ImdbId, chunk => titles.Where(i => i.ImdbId != null && chunk.Contains(i.ImdbId))).ConfigureAwait(false);
            groups = [.. fresh.Select(r => r.LinkGroupId).OfType<Guid>().Where(g => !asked.Contains(g))];
        }

        return await RowsAsync(db, [.. rows.Values], ct).ConfigureAwait(false);
    }

    private static async Task<List<Row>> ChildrenAsync(IQueryable<MediaItem> items, MediaKind kind, List<Guid> parents, CancellationToken ct)
    {
        var rows = new List<Row>();
        foreach (var chunk in parents.Chunk(IdsPerStatement))
        {
            rows.AddRange(await items.Where(i => i.Kind == kind && i.ParentId != null && chunk.Contains(i.ParentId.Value)).Select(AsRow).ToListAsync(ct).ConfigureAwait(false));
        }

        return rows;
    }

    /// <summary>The changes by the group they link to (null to unlink), in batches of about <see cref="ItemsPerBatch"/>. A group is never split.</summary>
    private static IEnumerable<List<IGrouping<Guid?, Row>>> Batches(List<Row> changes, Dictionary<Guid, Guid> wanted)
    {
        var batch = new List<IGrouping<Guid?, Row>>();
        var size = 0;
        foreach (var change in changes.GroupBy(c => wanted.TryGetValue(c.Id, out var group) ? group : (Guid?)null))
        {
            batch.Add(change);
            size += change.Count();
            if (size >= ItemsPerBatch)
            {
                yield return batch;
                batch = [];
                size = 0;
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
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

        var bySeason = rows.Where(r => r.Kind == MediaKind.Episode && r.ParentId is { } season && groups.ContainsKey(season) && (r.EpisodeNumber is not null || r.AirDate is not null))
            .GroupBy(r => groups[r.ParentId!.Value]).ToList();
        foreach (var season in bySeason)
        {
            foreach (var episode in EpisodeCopies(season).Where(e => e.Count > 1))
            {
                AddGroup(groups, episode);
            }
        }

        return groups;
    }

    /// <summary>
    /// A linked season's episodes, as the copies of each: by number when they have one, and an episode named only by its
    /// date (a daily show's file, in a library that numbers it in the other) joins the numbered one that aired that day,
    /// when only one did.
    /// </summary>
    private static List<List<Guid>> EpisodeCopies(IEnumerable<Row> season)
    {
        var numbered = season.Where(r => r.EpisodeNumber is not null).GroupBy(r => (r.EpisodeNumber, End: r.EpisodeNumberEnd ?? r.EpisodeNumber))
            .Select(g => (Ids: g.Select(r => r.Id).ToList(), Dates: g.Select(r => r.AirDate).OfType<DateOnly>().ToHashSet())).ToList();
        var copies = numbered.Select(n => n.Ids).ToList();
        foreach (var dated in season.Where(r => r.EpisodeNumber is null).GroupBy(r => r.AirDate!.Value))
        {
            var aired = numbered.Where(n => n.Dates.Contains(dated.Key)).ToList();
            if (aired.Count == 1)
            {
                aired[0].Ids.AddRange(dated.Select(r => r.Id));
            }
            else
            {
                copies.Add([.. dated.Select(r => r.Id)]);
            }
        }

        return copies;
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
    /// Films and shows that have just linked agree a parent's say for profiles with an age limit, so it holds for the
    /// whole title, also for a copy arriving later: Block if any copy has it, otherwise Allow if any has that.
    /// </summary>
    private static async Task AgreeKidsOverridesAsync(FlexarrDbContext db, List<IGrouping<Guid?, Row>> batch, List<Row> rows, Dictionary<Guid, Guid> wanted, CancellationToken ct)
    {
        var joined = batch.SelectMany(c => c).Where(c => c.Kind is MediaKind.Movie or MediaKind.Series && wanted.ContainsKey(c.Id)).Select(c => wanted[c.Id]).ToHashSet();
        foreach (var title in rows.Where(r => r.Kind is MediaKind.Movie or MediaKind.Series && wanted.TryGetValue(r.Id, out var group) && joined.Contains(group)).GroupBy(r => wanted[r.Id]))
        {
            var says = title.Any(r => r.KidsOverride == KidsOverride.Block) ? KidsOverride.Block
                : title.Any(r => r.KidsOverride == KidsOverride.Allow) ? KidsOverride.Allow : KidsOverride.None;
            var differ = title.Where(r => r.KidsOverride != says).Select(r => r.Id).ToList();
            if (differ.Count == 0)
            {
                continue;
            }

            await db.MediaItems.IgnoreQueryFilters().Where(i => differ.Contains(i.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.KidsOverride, says), ct).ConfigureAwait(false);
            await db.PassDownRatingsAsync(differ, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Copies that have just linked agree what's been watched, for each profile: watched if any copy was (and it wasn't
    /// since marked unwatched), and the place
    /// and details of whichever was played last.
    /// </summary>
    private static async Task ShareWatchStateAsync(FlexarrDbContext db, ILookup<Guid, Guid> members, CancellationToken ct)
    {
        var ids = members.SelectMany(m => m).ToHashSet();
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
            // Unless the latest thing done to it was marking it unwatched: a copy coming back mustn't undo that.
            var watched = group.Any(s => s.Watched) && newest is not { Watched: false, PositionSeconds: 0 };
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

        foreach (var entry in db.ChangeTracker.Entries<PlaybackState>().Where(e => ids.Contains(e.Entity.MediaItemId)).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }

    private sealed record Row(Guid Id, Guid? ParentId, MediaKind Kind, string? TmdbId, string? TvdbId, string? TvMazeId, string? ImdbId,
        int? SeasonNumber, int? EpisodeNumber, int? EpisodeNumberEnd, DateOnly? AirDate, Guid? LinkGroupId, KidsOverride KidsOverride);
}
