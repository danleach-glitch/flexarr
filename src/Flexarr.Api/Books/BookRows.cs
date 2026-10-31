using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>Audiobooks' rows on Home.</summary>
public static class BookRows
{
    public const string ContinueRowId = "books-continue";
    public const string NextRowId = "books-next";
    public const string NewRowId = "books-new";

    /// <summary>The rows for <paramref name="libraryIds"/>, the audiobooks libraries this profile can see; empty rows are left out.</summary>
    public static async Task<List<HomeRowDto>> HomeAsync(FlexarrDbContext db, IReadOnlyList<Guid> libraryIds, string profileId, int size, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var books = db.MediaItems.Where(i => libraryIds.Contains(i.LibraryId) && i.Kind == MediaKind.Audiobook && i.MissingSince == null
            && i.Files.Any(f => f.ProbeError == null && f.MissingSince == null));
        var link = libraryIds.Count == 1 ? $"/library/{libraryIds[0]}" : null;
        var rows = new List<HomeRowDto>
        {
            new(ContinueRowId, "Continue listening", "poster", await BookQueries.InProgress(books, profileId)
                .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.LastPlayedAt).FirstOrDefault())
                .Take(size).Summaries(profileId).ToListAsync(ct), link),
            new(NextRowId, "Next in series", "poster", await BookQueries.NextInSeriesAsync(db, books, profileId, size, ct), link),
            new(NewRowId, "New audiobooks", "poster", await books.OrderByDescending(i => i.AddedAt).Take(size).Summaries(profileId).ToListAsync(ct),
                link is null ? null : $"{link}?view=all&sort=added"),
        };
        return [.. rows.Where(r => r.Items.Count > 0)];
    }
}
