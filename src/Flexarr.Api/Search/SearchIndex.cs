using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Search;

/// <summary>
/// The names search forgives typos in (top-level titles, albums, people and genres), normalized and held in memory,
/// since the database can only match them exactly. Rebuilt when they change. It holds every library: what a profile
/// may see is left to the queries that turn its matches into results.
/// </summary>
public sealed class SearchIndex : IDisposable
{
    public enum Group { Title, Album, Person, Genre }

    /// <param name="Rank">Breaks ties between equally close matches: popularity for titles, credits for people.</param>
    private sealed record Entry(Group Group, Guid Id, string Name, SearchText.Candidate[] Texts, double Rank);

    private sealed record Snapshot((int Items, DateTime? Added, DateTime? Updated, int People, int Genres) Stamp, IReadOnlyList<Entry> Entries);

    private readonly SemaphoreSlim gate = new(1, 1);
    private Snapshot? snapshot;

    /// <summary>A match: the item's or person's id (empty for a genre), and the genre's name.</summary>
    public sealed record Match(Guid Id, string Name);

    /// <summary>
    /// The closest matches in <paramref name="group"/>, best first, leaving out <paramref name="except"/> (what the
    /// database already found). Asks for more than needed: some may be in libraries hidden from the profile.
    /// </summary>
    public async Task<IReadOnlyList<Match>> FindAsync(FlexarrDbContext db, string text, Group group, int take, IReadOnlySet<Guid> except, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(except);
        var query = SearchText.Query.Parse(text);
        if (query.Compact.Length == 0 || take <= 0)
        {
            return [];
        }

        var entries = (await CurrentAsync(db, ct)).Entries;
        return entries
            .Where(e => e.Group == group && !except.Contains(e.Id))
            .Select(e => (Entry: e, Cost: e.Texts.Select(t => SearchText.Cost(query, t)).Min()))
            .Where(m => m.Cost is not null)
            .OrderBy(m => m.Cost).ThenByDescending(m => m.Entry.Rank)
            .Take(take)
            .Select(m => new Match(m.Entry.Id, m.Entry.Name))
            .ToList();
    }

    /// <summary>
    /// Up to <paramref name="wanted"/> items close to <paramref name="text"/> that the database didn't find
    /// (<paramref name="found"/>), as this profile sees them.
    /// </summary>
    public async Task<List<ItemSummaryDto>> CloseItemsAsync(FlexarrDbContext db, string profileId, string text, Group group, int wanted,
        IEnumerable<ItemSummaryDto> found, CancellationToken ct)
    {
        if (wanted <= 0)
        {
            return [];
        }

        // Twice as many as wanted: some may be in libraries this profile doesn't see, or gone from disk.
        var close = await FindAsync(db, text, group, wanted * 2, found.Select(i => i.Id).ToHashSet(), ct);
        var ids = close.Select(m => m.Id).ToList();
        var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profileId).ToListAsync(ct);
        return [.. items.OrderBy(i => ids.IndexOf(i.Id)).Take(wanted)];
    }

    public void Dispose() => gate.Dispose();

    private async Task<Snapshot> CurrentAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var stamp = await StampAsync(db, ct);
        if (snapshot is { } current && current.Stamp == stamp)
        {
            return current;
        }

        await gate.WaitAsync(ct);
        try
        {
            if (snapshot is { } built && built.Stamp == stamp)
            {
                return built;
            }

            snapshot = new Snapshot(stamp, await BuildAsync(db, ct));
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    private static IQueryable<MediaItem> Indexed(FlexarrDbContext db) =>
        db.MediaItems.IgnoreQueryFilters().Where(i => (i.ParentId == null && i.Kind != MediaKind.Podcast) || i.Kind == MediaKind.Album);

    /// <summary>Changes whenever something indexed is added, removed or renamed.</summary>
    private static async Task<(int, DateTime?, DateTime?, int, int)> StampAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var items = await Indexed(db).GroupBy(_ => 1)
            .Select(g => new { Count = g.Count(), Added = g.Max(i => (DateTime?)i.AddedAt), Updated = g.Max(i => i.MetadataUpdatedAt) })
            .FirstOrDefaultAsync(ct);
        var people = await db.People.CountAsync(ct);
        var genres = await db.Genres.CountAsync(ct);
        return (items?.Count ?? 0, items?.Added, items?.Updated, people, genres);
    }

    private static async Task<IReadOnlyList<Entry>> BuildAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var items = await Indexed(db).AsNoTracking()
            .Select(i => new { i.Id, i.Kind, i.Title, i.OriginalTitle, Rank = i.Popularity ?? i.CommunityRating ?? 0 })
            .ToListAsync(ct);
        var people = await db.People.AsNoTracking().Where(p => p.Credits.Any())
            .Select(p => new { p.Id, p.Name, Credits = p.Credits.Count })
            .ToListAsync(ct);
        var genres = await db.Genres.AsNoTracking().Select(g => g.Name).ToListAsync(ct);

        return
        [
            .. items.Select(i => new Entry(i.Kind == MediaKind.Album ? Group.Album : Group.Title, i.Id, i.Title,
                i.OriginalTitle is { } original && original != i.Title ? [SearchText.Candidate.Of(i.Title), SearchText.Candidate.Of(original)] : [SearchText.Candidate.Of(i.Title)],
                i.Rank)),
            .. people.Select(p => new Entry(Group.Person, p.Id, p.Name, [SearchText.Candidate.Of(p.Name)], p.Credits)),
            .. genres.Select(g => new Entry(Group.Genre, Guid.Empty, g, [SearchText.Candidate.Of(g)], 0)),
        ];
    }
}
