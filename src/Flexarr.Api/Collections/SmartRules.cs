using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Api.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Collections;

/// <summary>
/// What a smart collection holds, worked out whenever it's opened (so it keeps itself up to date). Every rule is
/// optional; the ones given all apply.
/// </summary>
/// <param name="Type">"movie", "series", or null for both.</param>
/// <param name="ThisYear">Films released this year, and shows with episodes out this year.</param>
/// <param name="Watched">"unwatched" or "watched", for whoever is looking; null for either. A show counts as watched once every episode is.</param>
/// <param name="AddedWithinDays">Added recently (for a show: a new episode).</param>
/// <param name="Contains">Text in the title or description, e.g. "Christmas".</param>
/// <param name="Sort">"rating", "added", "year", "title" or "popularity".</param>
public sealed record SmartRules(
    IReadOnlyList<Guid>? LibraryIds = null,
    string? Type = null,
    IReadOnlyList<string>? Genres = null,
    int? YearFrom = null,
    int? YearTo = null,
    bool ThisYear = false,
    double? MinRating = null,
    string? Watched = null,
    int? AddedWithinDays = null,
    Guid? PersonId = null,
    string? Contains = null,
    string Sort = "rating",
    int Limit = 50)
{
    public const int MaxLimit = 200;

    /// <summary>The titles matching, best first, at most <see cref="Limit"/>.</summary>
    public IQueryable<MediaItem> Query(FlexarrDbContext db, string profileId, DateTime now)
    {
        var q = db.MediaItems.Where(i => i.ParentId == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series));

        if (LibraryIds is { Count: > 0 } libraries)
        {
            q = q.Where(i => libraries.Contains(i.LibraryId));
        }

        q = Type switch
        {
            "movie" => q.Where(i => i.Kind == MediaKind.Movie),
            "series" => q.Where(i => i.Kind == MediaKind.Series),
            _ => q,
        };

        if (Genres is { Count: > 0 } genres)
        {
            q = q.Where(i => i.Genres.Any(g => genres.Contains(g.Name)));
        }

        if (YearFrom is { } from)
        {
            q = q.Where(i => i.Year >= from);
        }

        if (YearTo is { } to)
        {
            q = q.Where(i => i.Year <= to);
        }

        if (ThisYear)
        {
            var first = new DateOnly(now.Year, 1, 1);
            var last = new DateOnly(now.Year, 12, 31);
            q = q.Where(i => i.Kind == MediaKind.Movie
                ? i.Year == now.Year
                : i.Children.SelectMany(s => s.Children).Any(e => (e.ReleaseDate ?? e.AirDate) >= first && (e.ReleaseDate ?? e.AirDate) <= last));
        }

        if (MinRating is { } rating)
        {
            q = q.Where(i => i.CommunityRating >= rating);
        }

        q = Watched switch
        {
            "unwatched" => q.Where(i => i.Kind == MediaKind.Movie
                ? !i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)
                : i.Children.SelectMany(s => s.Children).Any(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))),
            "watched" => q.Where(i => i.Kind == MediaKind.Movie
                ? i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)
                : i.Children.SelectMany(s => s.Children).Any() && i.Children.SelectMany(s => s.Children).All(e => e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))),
            _ => q,
        };

        if (AddedWithinDays is { } days)
        {
            var since = now.AddDays(-days);
            q = q.Where(i => i.AddedAt >= since || i.Children.SelectMany(s => s.Children).Any(e => e.AddedAt >= since));
        }

        if (PersonId is { } person)
        {
            q = q.Where(i => db.Credits.Any(c => c.MediaItemId == i.Id && c.PersonId == person));
        }

        if (!string.IsNullOrWhiteSpace(Contains))
        {
            var like = $"%{Contains.Trim()}%";
            q = q.Where(i => EF.Functions.Like(i.Title, like) || (i.Overview != null && EF.Functions.Like(i.Overview, like)));
        }

        IOrderedQueryable<MediaItem> ordered = Sort switch
        {
            "added" => q.OrderByDescending(i => i.Children.SelectMany(s => s.Children).Max(e => (DateTime?)e.AddedAt) ?? i.AddedAt),
            "year" => q.OrderByDescending(i => i.Year),
            "title" => q.OrderBy(i => i.SortTitle),
            "popularity" => q.OrderByDescending(i => i.Popularity),
            _ => q.OrderByDescending(i => i.CommunityRating + (i.CriticRating == null ? 0 : (i.CriticRating.Value - 70) / 50.0)),
        };

        return ordered.ThenBy(i => i.SortTitle).Take(Math.Clamp(Limit, 1, MaxLimit));
    }
}

public sealed record CollectionSuggestionDto(string Name, string Description, SmartRules Rules, int Count, IReadOnlyList<string> Posters);

/// <summary>Collections the library would make good ones of: its directors and actors, genre-and-decade runs, the season, and the year's best.</summary>
public static class CollectionSuggestions
{
    public static async Task<List<CollectionSuggestionDto>> ForAsync(FlexarrDbContext db, string profileId, IReadOnlySet<string> taken, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var ideas = new List<(string Name, string Description, SmartRules Rules)>
        {
            ("Top 10 shows this year", "The best-rated shows with episodes out this year.", new SmartRules(Type: "series", ThisYear: true, Limit: 10)),
            ("Top 10 films this year", "The best-rated films released this year.", new SmartRules(Type: "movie", ThisYear: true, Limit: 10)),
            ("Highly rated, never watched", "Films rated 7.5 or more that you haven't seen.", new SmartRules(Type: "movie", MinRating: 7.5, Watched: "unwatched")),
            ("New this month", "Everything added in the last 30 days.", new SmartRules(AddedWithinDays: 30, Sort: "added")),
        };

        // Seasonal, and only near the season, so suggestions change through the year.
        if (now.Month is 11 or 12)
        {
            ideas.Add(("Christmas", "Films and specials with Christmas in them.", new SmartRules(Contains: "Christmas", Sort: "year")));
        }

        if (now.Month is 9 or 10)
        {
            ideas.Add(("Halloween", "Horror for the season, best first.", new SmartRules(Genres: ["Horror"])));
        }

        // Directors with four or more titles here, and leading actors with six or more.
        var directors = await db.Credits.Where(c => c.Role == CreditRole.Director)
            .GroupBy(c => c.PersonId).Select(g => new { g.Key, Count = g.Count() }).Where(g => g.Count >= 4)
            .OrderByDescending(g => g.Count).Take(6).ToListAsync(ct);
        var actors = await db.Credits.Where(c => c.Role == CreditRole.Actor && c.Order < 3)
            .GroupBy(c => c.PersonId).Select(g => new { g.Key, Count = g.Count() }).Where(g => g.Count >= 6)
            .OrderByDescending(g => g.Count).Take(6).ToListAsync(ct);
        var ids = directors.Select(d => d.Key).Concat(actors.Select(a => a.Key)).ToList();
        var names = await db.People.Where(p => ids.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        ideas.AddRange(directors.Where(d => names.ContainsKey(d.Key))
            .Select(d => ($"Directed by {names[d.Key]}", $"{d.Count} of their films and shows in your library.", new SmartRules(PersonId: d.Key, Sort: "year"))));
        ideas.AddRange(actors.Where(a => names.ContainsKey(a.Key))
            .Select(a => (names[a.Key], $"Starring {names[a.Key]}: {a.Count} titles.", new SmartRules(PersonId: a.Key, Sort: "year"))));

        // Genre-and-decade runs with plenty of films.
        // (Grouped here rather than in SQL, which SQLite can't do for a collection navigation; a library has
        // hundreds of films, not millions.)
        var years = await db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.Year != null)
            .Select(i => new { Year = i.Year!.Value, Genres = i.Genres.Select(g => g.Name).ToList() }).ToListAsync(ct);
        var films = years.SelectMany(i => i.Genres.Select(g => new { Name = g, Decade = i.Year / 10 * 10 }))
            .GroupBy(x => new { x.Name, x.Decade }).Select(g => new { g.Key.Name, g.Key.Decade, Count = g.Count() })
            .Where(g => g.Count >= 15).OrderByDescending(g => g.Count)
            // At most two per decade, so they span the library rather than all coming from its biggest decade.
            .GroupBy(g => g.Decade).SelectMany(d => d.Take(2)).OrderByDescending(g => g.Count).Take(6).ToList();
        ideas.AddRange(films.Select(g => ($"'{g.Decade % 100:00}s {g.Name}", $"{g.Count} {g.Name.ToLowerInvariant()} films from the {g.Decade}s, best first.",
            new SmartRules(Type: "movie", Genres: [g.Name], YearFrom: g.Decade, YearTo: g.Decade + 9))));

        var suggestions = new List<CollectionSuggestionDto>();
        foreach (var (name, description, rules) in ideas.Where(i => !taken.Contains(i.Name)))
        {
            var items = await rules.Query(db, profileId, now).Take(4).Summaries(profileId).ToListAsync(ct);
            if (items.Count < 3)
            {
                continue; // too few to be worth a collection (or nothing this profile can see)
            }

            var count = await rules.Query(db, profileId, now).CountAsync(ct);
            suggestions.Add(new CollectionSuggestionDto(name, description, rules, count, items.Select(i => i.Artwork.Poster).OfType<string>().ToList()));
        }

        return suggestions;
    }
}
