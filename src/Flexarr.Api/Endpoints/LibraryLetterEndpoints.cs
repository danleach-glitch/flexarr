using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

/// <param name="Letter">"A" to "Z", or "#" for titles that start with anything else.</param>
/// <param name="Offset">Where the run starts in the A–Z order: the <c>skip</c> that loads its first title.</param>
/// <param name="Count">How many titles the run has.</param>
public sealed record LetterOffsetDto(string Letter, int Offset, int Count);

public static class LibraryLetterEndpoints
{
    /// <summary>
    /// GET /libraries/{id}/items/letters (audit perf-14; new in the release after 0.62.1, absent before): where each
    /// letter starts in <c>/libraries/{id}/items</c> sorted A–Z (no <c>sort</c>), for the same <c>search</c> and
    /// <c>genre</c>, so a letter jump can load just the page at its offset. Runs come in list order, only letters that
    /// have titles. A letter can come more than once ("#" before A for digits and punctuation, and after Z for accented
    /// letters and the like): the first is where a jump to it goes.
    /// A scan between this and the page can shift titles by a few.
    /// </summary>
    public static RouteGroupBuilder MapLibraryLetterEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/libraries/{id:guid}/items/letters", async (Guid id, string? search, string? genre, FlexarrDbContext db, CancellationToken ct) =>
        {
            // The same titles as the items list.
            var query = db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                var pattern = $"%{search.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
                query = query.Where(i => EF.Functions.Like(i.Title, pattern));
            }

            if (!string.IsNullOrWhiteSpace(genre))
            {
                query = query.Where(i => i.Genres.Any(g => g.Name == genre));
            }

            // Grouped by the sort title's first character, in the database's order: the order the list sorts in.
            var firsts = await query.GroupBy(i => i.SortTitle.Substring(0, 1)).Select(g => new { First = g.Key, Count = g.Count() })
                .OrderBy(g => g.First).ToListAsync(ct);
            return Runs(firsts.Select(f => (f.First, f.Count)));
        }).WithTags("Items");

        return api;
    }

    /// <summary>First characters in list order, merged into letter runs with their offsets.</summary>
    internal static List<LetterOffsetDto> Runs(IEnumerable<(string First, int Count)> firsts)
    {
        var runs = new List<LetterOffsetDto>();
        var offset = 0;
        foreach (var (first, count) in firsts)
        {
            var letter = first is [var c] && char.IsAsciiLetter(c) ? char.ToUpperInvariant(c).ToString() : "#";
            if (runs.Count > 0 && runs[^1].Letter == letter)
            {
                runs[^1] = runs[^1] with { Count = runs[^1].Count + count };
            }
            else
            {
                runs.Add(new LetterOffsetDto(letter, offset, count));
            }

            offset += count;
        }

        return runs;
    }
}
