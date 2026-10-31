using Flexarr.Core.Books;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Metadata;

/// <summary>Audiobooks: what Readarr, Bookshelf or Chaptarr know about a book fills what its files don't say.</summary>
public sealed partial class MetadataRefresher
{
    /// <summary>What one app knows, fetched once per refresh.</summary>
    private sealed record ReadarrCatalogue(ArrInstance Instance, IReadOnlyList<ReadarrAuthor> Authors, IReadOnlyList<ReadarrBook> Books,
        IReadOnlyList<ReadarrBookFile> Files, IReadOnlyDictionary<string, List<(ReadarrSeries Series, string? Position)>> SeriesByBook);

    private async Task<MetadataRefreshResult> RefreshBooksAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var also = await WithinAsync(libraryId, MediaKind.Audiobook, within, ct).ConfigureAwait(false);
        var books = await Stale(libraryId, MediaKind.Audiobook, force, also)
            .Include(i => i.Book).Include(i => i.Genres).Include(i => i.Credits).ThenInclude(c => c.Person)
            .AsSplitQuery().ToListAsync(ct).ConfigureAwait(false);
        if (books.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var catalogues = new List<ReadarrCatalogue>();
        foreach (var readarr in settings.ArrInstances.Where(a => a.Kind == ArrKind.Readarr))
        {
            if (await CatalogueAsync(readarr, ct).ConfigureAwait(false) is { } catalogue)
            {
                catalogues.Add(catalogue);
            }
        }

        int fromArr = 0, unmatched = 0, local = 0;
        foreach (var book in books)
        {
            var details = book.Book ??= new BookDetails { MediaItemId = book.Id };
            var wanted = new BookToMatch(book.Path, book.Title,
                [.. book.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name)],
                details.Asin, details.Isbn, details.ArrBookId);
            var hit = catalogues.Select(c => (Catalogue: c, Book: ReadarrMatcher.Match(wanted, c.Books, c.Files, c.Authors, settings.PathMappings)))
                .FirstOrDefault(h => h.Book is not null);
            if (hit.Book is { } match)
            {
                await ApplyReadarrAsync(book, details, match, hit.Catalogue, ct).ConfigureAwait(false);
                fromArr++;
            }
            else if (catalogues.Count > 0)
            {
                unmatched++;
            }
            else
            {
                local++;
            }

            book.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + unmatched + local, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, 0, unmatched, local);
    }

    /// <summary>An app's authors, books, files and series; null if it can't be asked right now.</summary>
    private async Task<ReadarrCatalogue?> CatalogueAsync(ArrInstance readarr, CancellationToken ct)
    {
        try
        {
            var authors = await arr.GetAuthorsAsync(readarr, ct).ConfigureAwait(false);
            var books = await arr.GetBooksAsync(readarr, ct).ConfigureAwait(false);
            var files = new List<ReadarrBookFile>();
            var series = new Dictionary<string, List<(ReadarrSeries, string?)>>(StringComparer.Ordinal);
            foreach (var author in authors.Where(a => a.Id is not null))
            {
                files.AddRange(await arr.GetBookFilesAsync(readarr, author.Id!, ct).ConfigureAwait(false));
                foreach (var s in await arr.GetBookSeriesAsync(readarr, author.Id!, ct).ConfigureAwait(false))
                {
                    foreach (var link in s.Links.Where(l => l.BookId is not null))
                    {
                        (series.TryGetValue(link.BookId!, out var list) ? list : series[link.BookId!] = []).Add((s, link.Position));
                    }
                }
            }

            return new ReadarrCatalogue(readarr, authors, books, files, series);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
        {
            LogArrUnavailable(readarr.Name, ex.Message);
            return null;
        }
    }

    /// <summary>Fills the book's gaps from the app: never over what the files said.</summary>
    private async Task ApplyReadarrAsync(MediaItem book, BookDetails details, ReadarrBook match, ReadarrCatalogue catalogue, CancellationToken ct)
    {
        var edition = match.Edition;
        details.ArrBookId = match.Id;
        details.ArrAuthorId = match.AuthorId;
        details.Asin ??= edition?.Asin is { Length: > 0 } asin ? asin.ToUpperInvariant() : null;
        details.Isbn ??= edition?.Isbn13;
        details.Publisher ??= edition?.Publisher;
        details.Language ??= edition?.Language;
        book.Overview ??= Plain(edition?.Overview) ?? Plain(match.Overview);
        book.ReleaseDate ??= match.ReleaseDate is { } released ? DateOnly.FromDateTime(released) : null;
        book.Year ??= book.ReleaseDate?.Year;
        book.Studio ??= details.Publisher;
        book.PosterUrl ??= Cover(match.Images) ?? Cover(edition?.Images ?? []);
        // Which app has it: deleting it goes through that one.
        book.MetadataSource = catalogue.Instance.Name;

        if (book.Genres.Count == 0 && match.Genres.Count > 0)
        {
            foreach (var name in match.Genres.Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
            {
                book.Genres.Add(await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct).ConfigureAwait(false)
                    ?? db.Genres.Local.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? db.Genres.Add(new Genre { Name = name }).Entity);
            }
        }

        // The author's photo and biography, on whoever the files named first.
        var author = catalogue.Authors.FirstOrDefault(a => a.Id == match.AuthorId);
        if (author is not null && book.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person).FirstOrDefault() is { } person)
        {
            person.Biography ??= Plain(author.Overview);
            person.ImageUrl ??= Cover(author.Images);
        }

        // Its series and place in them, when the files didn't say.
        if (match.Id is { } id && catalogue.SeriesByBook.TryGetValue(id, out var places)
            && !await db.BookSeriesEntries.AnyAsync(e => e.BookId == book.Id, ct).ConfigureAwait(false))
        {
            foreach (var (series, position) in places.Where(p => p.Series.Title is { Length: > 0 }))
            {
                var stored = await db.BookSeries.FirstOrDefaultAsync(s => s.LibraryId == book.LibraryId && s.Name == series.Title, ct).ConfigureAwait(false)
                    ?? db.BookSeries.Local.FirstOrDefault(s => s.LibraryId == book.LibraryId && s.Name == series.Title)
                    ?? db.BookSeries.Add(new BookSeries { LibraryId = book.LibraryId, Name = series.Title!, SortName = SortTitle.For(series.Title!), Description = Plain(series.Description), ExternalId = series.Id }).Entity;
                db.BookSeriesEntries.Add(new BookSeriesEntry { SeriesId = stored.Id, BookId = book.Id, Sequence = position, SortOrder = BookAssembler.SequenceOrder(position) });
            }
        }
    }

    /// <summary>A cover the app points at on the web; its own /MediaCover paths need its key, so they're left for other sources.</summary>
    private static string? Cover(IReadOnlyList<ArrImage> images) =>
        images.Where(i => i.CoverType is "cover" or "poster").Select(i => i.RemoteUrl)
            .FirstOrDefault(u => u is not null && u.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    private static string? Plain(string? html) => string.IsNullOrWhiteSpace(html) ? null : BookSidecars.PlainText(html);
}
