using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Books;

/// <summary>
/// Keeps a book's progress with it when its files come back somewhere new: a folder renamed, a book re-encoded into
/// one m4b under a new name, a series folder added above it. A missing book hands its progress, bookmarks, speed,
/// collections and hand-edited chapters to the book on disk that matches it by ASIN, then ISBN, then title and author.
/// Each missing book is handed over once, like films and shows (see <see cref="Scanning.WatchHistoryCarryOver"/>).
/// </summary>
public static class BookCarryOver
{
    public static async Task<int> CarryOverAsync(FlexarrDbContext db, Guid libraryId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var books = db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == libraryId && i.Kind == MediaKind.Audiobook);
        var missing = await books.Where(i => i.MissingSince != null && (i.HandedOverAt == null || i.HandedOverAt < i.MissingSince))
            .Select(Identity()).ToListAsync(ct).ConfigureAwait(false);
        if (missing.Count == 0)
        {
            return 0;
        }

        var present = await books.Where(i => i.MissingSince == null).Select(Identity()).ToListAsync(ct).ConfigureAwait(false);
        var carried = 0;
        var handedOver = new List<Guid>();
        foreach (var from in missing)
        {
            var to = present.FirstOrDefault(p => Same(p.Asin, from.Asin))
                ?? present.FirstOrDefault(p => Same(p.Isbn, from.Isbn))
                ?? present.FirstOrDefault(p => from.Author is not null && Fold(p.Title) == Fold(from.Title) && Same(Fold(p.Author), Fold(from.Author)));
            if (to is null)
            {
                continue;
            }

            carried += await HandOverAsync(db, from.Id, to.Id, ct).ConfigureAwait(false);
            handedOver.Add(from.Id);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (handedOver.Count > 0)
        {
            var now = DateTime.UtcNow;
            await db.MediaItems.IgnoreQueryFilters().Where(i => handedOver.Contains(i.Id))
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.HandedOverAt, now), ct).ConfigureAwait(false);
        }

        return carried;
    }

    private sealed record BookIdentity(Guid Id, string Title, string? Asin, string? Isbn, string? Author);

    private static System.Linq.Expressions.Expression<Func<MediaItem, BookIdentity>> Identity() => i => new BookIdentity(
        i.Id, i.Title, i.Book!.Asin, i.Book.Isbn,
        i.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name).FirstOrDefault());

    private static bool Same(string? a, string? b) => a is not null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Fold(string? value) =>
        value is null ? null : new string([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    /// <summary>
    /// Gives <paramref name="to"/> what <paramref name="from"/> had, where it has nothing of its own: a profile that
    /// already has progress on the new book keeps it.
    /// </summary>
    private static async Task<int> HandOverAsync(FlexarrDbContext db, Guid from, Guid to, CancellationToken ct)
    {
        var carried = 0;
        var had = (await db.PlaybackStates.Where(p => p.MediaItemId == to).Select(p => p.UserId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        foreach (var state in await db.PlaybackStates.AsNoTracking().Where(p => p.MediaItemId == from).ToListAsync(ct).ConfigureAwait(false))
        {
            if (had.Contains(state.UserId))
            {
                continue;
            }

            state.MediaItemId = to;
            state.MediaItem = null;
            db.PlaybackStates.Add(state);
            carried++;
        }

        await db.Bookmarks.Where(b => b.MediaItemId == from).ExecuteUpdateAsync(u => u.SetProperty(b => b.MediaItemId, to), ct).ConfigureAwait(false);

        var speeds = (await db.ShowPlaybackSpeeds.Where(s => s.SeriesId == to).Select(s => s.ProfileId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
        foreach (var speed in await db.ShowPlaybackSpeeds.AsNoTracking().Where(s => s.SeriesId == from).ToListAsync(ct).ConfigureAwait(false))
        {
            if (!speeds.Contains(speed.ProfileId))
            {
                db.ShowPlaybackSpeeds.Add(new ShowPlaybackSpeed { ProfileId = speed.ProfileId, SeriesId = to, Speed = speed.Speed });
            }
        }

        var collections = (await db.CollectionItems.Where(c => c.MediaItemId == to).Select(c => c.CollectionId).ToListAsync(ct).ConfigureAwait(false)).ToHashSet();
        foreach (var item in await db.CollectionItems.AsNoTracking().Where(c => c.MediaItemId == from).ToListAsync(ct).ConfigureAwait(false))
        {
            if (collections.Add(item.CollectionId))
            {
                db.CollectionItems.Add(new CollectionItem { CollectionId = item.CollectionId, MediaItemId = to, Position = item.Position });
            }
        }

        // Chapters someone fixed by hand go with the book, unless the new copy has had its own fixed.
        var fromDetails = await db.BookDetails.AsNoTracking().FirstOrDefaultAsync(d => d.MediaItemId == from, ct).ConfigureAwait(false);
        var toDetails = await db.BookDetails.FirstOrDefaultAsync(d => d.MediaItemId == to, ct).ConfigureAwait(false);
        if (fromDetails?.ChapterSource is ChapterSource.Edited or ChapterSource.Audible && toDetails is not null
            && toDetails.ChapterSource is not (ChapterSource.Edited or ChapterSource.Audible)
            && Math.Abs(fromDetails.DurationSeconds - toDetails.DurationSeconds) < 5)
        {
            var chapters = await db.MediaChapters.AsNoTracking().Where(c => c.MediaItemId == from).OrderBy(c => c.Index).ToListAsync(ct).ConfigureAwait(false);
            await db.MediaChapters.Where(c => c.MediaItemId == to).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.MediaChapters.AddRange(chapters.Select(c => new MediaChapter { MediaItemId = to, Index = c.Index, Title = c.Title, StartSeconds = c.StartSeconds, EndSeconds = c.EndSeconds }));
            toDetails.ChapterSource = fromDetails.ChapterSource;
        }

        return carried;
    }
}
