using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Metadata;

/// <summary>A book of ours, as the matcher needs to know it.</summary>
/// <param name="Path">Its folder (or file, for a book that's one file beside others).</param>
/// <param name="Authors">Its authors' names.</param>
/// <param name="ArrBookId">Its id in the app it was matched in before.</param>
/// <param name="ArrInstance">The app <paramref name="ArrBookId"/> belongs to: an id means nothing in any other.</param>
/// <param name="Files">
/// Its files. When known, an app's file matches only if it's one of them: a folder that holds one book's file beside
/// other books' folders (Author/One.m4b, Author/Two/…) is "the book's folder" to the scanner, and every file below it
/// isn't this book's.
/// </param>
public sealed record BookToMatch(string? Path, string Title, IReadOnlyList<string> Authors, string? Asin, string? Isbn, string? ArrBookId, string? ArrInstance = null,
    IReadOnlyList<string>? Files = null);

/// <summary>What one Readarr-compatible app has: its books (each with its editions), their files and its authors.</summary>
/// <param name="Instance">The app's name in Flexarr's settings.</param>
public sealed record ReadarrShelf(string Instance, IReadOnlyList<ReadarrBook> Books, IReadOnlyList<ReadarrBookFile> Files, IReadOnlyList<ReadarrAuthor> Authors);

/// <summary>
/// Pairs the books the scanner found with what Readarr-compatible apps manage. A household may run several (one for
/// ebooks, one for audiobooks), and their ids overlap, so a book once matched keeps its id only in the app it came
/// from. Otherwise an app's files decide first, in every app (they say exactly where each book lives); then the ids the
/// files carry (ASIN, ISBN); then title and author, never title alone.
/// </summary>
public static class ReadarrMatcher
{
    /// <param name="book">Ours.</param>
    /// <param name="shelves">Each app's books, files and authors, in the order the apps are set up.</param>
    /// <param name="mappings">Path mappings from the apps' paths to Flexarr's.</param>
    /// <returns>The app (its index in <paramref name="shelves"/>) and its book; null if none has it.</returns>
    public static (int Shelf, ReadarrBook Book)? Match(BookToMatch book, IReadOnlyList<ReadarrShelf> shelves, IReadOnlyList<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(shelves);
        ArgumentNullException.ThrowIfNull(mappings);

        // The id it had, in the app it had it from (and only there: another app's book 12 is another book).
        if (book.ArrBookId is { } known && book.ArrInstance is { } instance)
        {
            for (var i = 0; i < shelves.Count; i++)
            {
                if (string.Equals(shelves[i].Instance, instance, StringComparison.OrdinalIgnoreCase)
                    && shelves[i].Books.FirstOrDefault(b => b.Id == known) is { } stillThere)
                {
                    return (i, stillThere);
                }
            }
        }

        // Its files: one of an app's files is one of the book's files (or, when they aren't known, inside the book's
        // folder). Every app is asked before anything less certain is tried in any of them.
        var files = book.Files is { Count: > 0 } ours ? ours.Select(PathComparison.Key).ToHashSet(PathComparison.FileSystemKeyComparer) : null;
        if (book.Path is { Length: > 0 } path)
        {
            for (var i = 0; i < shelves.Count; i++)
            {
                var hit = shelves[i].Files.Where(f => f.Path is { Length: > 0 } && f.BookId is not null)
                    .FirstOrDefault(f => PathMapper.Map(f.Path!, mappings) is var mapped
                        && (files is not null ? files.Contains(PathComparison.Key(mapped)) : PathComparison.IsUnder(mapped, path, PathComparison.FileSystemComparison)));
                if (hit is not null && shelves[i].Books.FirstOrDefault(b => b.Id == hit.BookId) is { } byFile)
                {
                    return (i, byFile);
                }
            }
        }

        if (book.Asin is { Length: > 0 } asin
            && First(shelves, b => b.Editions.Any(e => string.Equals(e.Asin, asin, StringComparison.OrdinalIgnoreCase))) is { } byAsin)
        {
            return byAsin;
        }

        if (book.Isbn is { Length: > 0 } isbn
            && First(shelves, b => b.Editions.Any(e => e.Isbn13 is { } i && i.Replace("-", "", StringComparison.Ordinal) == isbn)) is { } byIsbn)
        {
            return byIsbn;
        }

        // Title and author. A book with no author has nothing to tell one "Dune" from another.
        if (book.Authors.Count == 0)
        {
            return null;
        }

        var title = Fold(book.Title);
        var names = book.Authors.Select(Fold).Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
        if (title.Length == 0 || names.Count == 0)
        {
            return null;
        }

        for (var i = 0; i < shelves.Count; i++)
        {
            var authorNames = shelves[i].Authors.Where(a => a.Id is not null).GroupBy(a => a.Id!, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => Fold(g.First().AuthorName ?? ""), StringComparer.Ordinal);
            var byTitle = shelves[i].Books.Where(b => b.Title is { } t && Fold(t) == title)
                .FirstOrDefault(b => (b.AuthorId is { } id && authorNames.TryGetValue(id, out var name) && names.Contains(name))
                    || (b.AuthorTitle is { } at && names.Any(n => Fold(at).Contains(n, StringComparison.Ordinal))));
            if (byTitle is not null)
            {
                return (i, byTitle);
            }
        }

        return null;
    }

    private static (int Shelf, ReadarrBook Book)? First(IReadOnlyList<ReadarrShelf> shelves, Func<ReadarrBook, bool> predicate)
    {
        for (var i = 0; i < shelves.Count; i++)
        {
            if (shelves[i].Books.FirstOrDefault(predicate) is { } found)
            {
                return (i, found);
            }
        }

        return null;
    }

    /// <summary>Lower case, letters and digits only: "The Man Who Died Twice" and "the man who died twice!" are one.</summary>
    private static string Fold(string value) =>
        new([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
}
