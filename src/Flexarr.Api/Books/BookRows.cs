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
    /// <param name="continueRow">Whether to work out Continue listening: not when Home's Continue row holds the books instead.</param>
    public static async Task<List<HomeRowDto>> HomeAsync(FlexarrDbContext db, IReadOnlyList<Guid> libraryIds, string profileId, int size, bool continueRow,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(libraryIds);
        var books = Playable(db, libraryIds);
        var link = libraryIds.Count == 1 ? $"/library/{libraryIds[0]}" : null;
        var rows = new List<HomeRowDto>
        {
            new(ContinueRowId, "Continue listening", "poster", !continueRow ? [] : await BookQueries.InProgress(books, profileId)
                .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.LastPlayedAt).FirstOrDefault())
                .Take(size).Summaries(profileId).ToListAsync(ct), link),
            new(NextRowId, "Next in series", "poster", await BookQueries.NextInSeriesAsync(db, books, profileId, size, ct), link),
            new(NewRowId, "New audiobooks", "poster", await books.OrderByDescending(i => i.AddedAt).Take(size).Summaries(profileId).ToListAsync(ct),
                link is null ? null : $"{link}?view=all&sort=added"),
        };
        return [.. rows.Where(r => r.Items.Count > 0)];
    }

    /// <summary>The books in <paramref name="libraryIds"/> that can be played: there, with a file that read properly.</summary>
    public static IQueryable<MediaItem> Playable(FlexarrDbContext db, IReadOnlyList<Guid> libraryIds)
    {
        ArgumentNullException.ThrowIfNull(db);
        return db.MediaItems.Where(i => libraryIds.Contains(i.LibraryId) && i.Kind == MediaKind.Audiobook && i.MissingSince == null
            && i.Files.Any(f => f.ProbeError == null && f.MissingSince == null));
    }
}
