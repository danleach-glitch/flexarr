using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Metadata;

/// <summary>A book of ours, as the matcher needs to know it.</summary>
/// <param name="Path">Its folder (or file, for a book that's one file beside others).</param>
/// <param name="Authors">Its authors' names.</param>
public sealed record BookToMatch(string? Path, string Title, IReadOnlyList<string> Authors, string? Asin, string? Isbn, string? ArrBookId);

/// <summary>
/// Pairs the books the scanner found with what a Readarr-compatible app manages. The app knows exactly where each of
/// its book's files lives, so the files decide first; then the ids the files carry (ASIN, ISBN), then title and
/// author. A book once matched keeps its id, as long as the app still has it.
/// </summary>
public static class ReadarrMatcher
{
    /// <param name="book">Ours.</param>
    /// <param name="books">The app's books (each with its editions).</param>
    /// <param name="files">The app's book files.</param>
    /// <param name="authors">The app's authors, for their names.</param>
    /// <param name="mappings">Path mappings from the app's paths to Flexarr's.</param>
    public static ReadarrBook? Match(BookToMatch book, IReadOnlyList<ReadarrBook> books, IReadOnlyList<ReadarrBookFile> files,
        IReadOnlyList<ReadarrAuthor> authors, IReadOnlyList<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(books);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(authors);
        ArgumentNullException.ThrowIfNull(mappings);

        if (book.ArrBookId is { } known && books.FirstOrDefault(b => b.Id == known) is { } stillThere)
        {
            return stillThere;
        }

        // Its files: one of the app's files is the book's file, or inside the book's folder.
        if (book.Path is { Length: > 0 } path)
        {
            var hit = files.Where(f => f.Path is { Length: > 0 } && f.BookId is not null)
                .FirstOrDefault(f => PathComparison.IsUnder(PathMapper.Map(f.Path!, mappings), path));
            if (hit is not null && books.FirstOrDefault(b => b.Id == hit.BookId) is { } byFile)
            {
                return byFile;
            }
        }

        if (book.Asin is { Length: > 0 } asin
            && books.FirstOrDefault(b => b.Editions.Any(e => string.Equals(e.Asin, asin, StringComparison.OrdinalIgnoreCase))) is { } byAsin)
        {
            return byAsin;
        }

        if (book.Isbn is { Length: > 0 } isbn
            && books.FirstOrDefault(b => b.Editions.Any(e => e.Isbn13 is { } i && i.Replace("-", "", StringComparison.Ordinal) == isbn)) is { } byIsbn)
        {
            return byIsbn;
        }

        var title = Fold(book.Title);
        var names = book.Authors.Select(Fold).ToHashSet(StringComparer.Ordinal);
        var authorNames = authors.Where(a => a.Id is not null).ToDictionary(a => a.Id!, a => Fold(a.AuthorName ?? ""), StringComparer.Ordinal);
        return books.Where(b => b.Title is { } t && Fold(t) == title)
            .FirstOrDefault(b => names.Count == 0
                || (b.AuthorId is { } id && authorNames.TryGetValue(id, out var name) && names.Contains(name))
                || (b.AuthorTitle is { } at && names.Any(n => Fold(at).Contains(n, StringComparison.Ordinal))));
    }

    /// <summary>Lower case, letters and digits only: "The Man Who Died Twice" and "the man who died twice!" are one.</summary>
    private static string Fold(string value) =>
        new([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
