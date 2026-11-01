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

        var shelves = catalogues.Select(c => new ReadarrShelf(c.Instance.Name, c.Books, c.Files, c.Authors)).ToList();
        int fromArr = 0, lookedUp = 0, unmatched = 0, local = 0;
        foreach (var book in books)
        {
            var details = book.Book ??= new BookDetails { MediaItemId = book.Id };
            var wanted = new BookToMatch(book.Path, book.Title,
                [.. book.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name)],
                details.Asin, details.Isbn, details.ArrBookId, details.ArrInstance);
            // The app it came from is set up but didn't answer: its link stands rather than one guessed in another app.
            var unanswered = details.ArrInstance is { } from
                && settings.ArrInstances.Any(a => a.Kind == ArrKind.Readarr && SameInstance(a.Name, from))
                && !catalogues.Any(c => SameInstance(c.Instance.Name, from));
            var hit = unanswered ? null : ReadarrMatcher.Match(wanted, shelves, settings.PathMappings);
            if (hit is { } matched)
            {
                await ApplyReadarrAsync(book, details, matched.Book, catalogues[matched.Shelf], ct).ConfigureAwait(false);
                fromArr++;
            }
            else if (!unanswered && details.ArrBookId is not null)
            {
                // Its app no longer has it (or is gone): the old id would only reach some other book there.
                details.ArrBookId = null;
                details.ArrAuthorId = null;
                details.ArrInstance = null;
            }

            // Online: by its ids, else (unless it was matched by hand) by title and author.
            var online = false;
            if (bookProviders is not null)
            {
                try
                {
                    var found = await bookProviders.LookupAsync(details.Asin, details.Isbn, ct).ConfigureAwait(false)
                        ?? (book.MatchLocked ? null : await bookProviders.FindAsync(book.Title, wanted.Authors, ct).ConfigureAwait(false));
                    if (found is not null)
                    {
                        await ApplyOnlineAsync(book, details, found, overwrite: false, ct).ConfigureAwait(false);
                        online = true;
                    }
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
                {
                    LogItemFailed(ex, book.Title);
                }
            }

            if (hit is null)
            {
                if (online)
                {
                    lookedUp++;
                }
                else if (catalogues.Count > 0 || bookProviders is not null)
                {
                    unmatched++;
                }
                else
                {
                    local++;
                }
            }

            book.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + lookedUp + unmatched + local, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, lookedUp, unmatched, local);
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
        details.ArrInstance = catalogue.Instance.Name;
        details.Asin ??= edition?.Asin is { Length: > 0 } asin ? asin.ToUpperInvariant() : null;
        details.Isbn ??= edition?.Isbn13;
        details.Publisher ??= edition?.Publisher;
        details.Language ??= edition?.Language;
        book.Overview ??= Plain(edition?.Overview) ?? Plain(match.Overview);
        book.ReleaseDate ??= match.ReleaseDate is { } released ? DateOnly.FromDateTime(released) : null;
        book.Year ??= book.ReleaseDate?.Year;
        book.Studio ??= details.Publisher;
        book.PosterUrl ??= Cover(match.Images) ?? Cover(edition?.Images ?? []);
        // Where its details came from, as shown; deleting it goes through ArrInstance.
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

    /// <summary>
    /// What an online provider says about a book: only its gaps, unless <paramref name="overwrite"/> (a match chosen by
    /// hand, whose details are what was chosen). Its authors' photos and biographies and, for a guessed set of chapters,
    /// Audible's.
    /// </summary>
    public async Task ApplyOnlineAsync(MediaItem book, BookDetails details, Books.BookMatch found, bool overwrite, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentNullException.ThrowIfNull(details);
        ArgumentNullException.ThrowIfNull(found);
        T? Pick<T>(T? current, T? online) => overwrite ? online ?? current : current ?? online;

        if (overwrite)
        {
            book.Title = found.Title;
            book.SortTitle = SortTitle.For(found.Title);
        }

        details.Subtitle = Pick(details.Subtitle, found.Subtitle);
        details.Publisher = Pick(details.Publisher, found.Publisher);
        details.Language = Pick(details.Language, found.Language);
        details.Isbn = Pick(details.Isbn, found.Isbn);
        details.Asin = Pick(details.Asin, found.Asin);
        details.Abridged = found.Abridged is { } abridged && (overwrite || !details.Abridged) ? abridged : details.Abridged;
        book.Overview = Pick(book.Overview, found.Description);
        book.ReleaseDate = book.ReleaseDate is not null && !overwrite ? book.ReleaseDate : found.ReleaseDate ?? book.ReleaseDate;
        book.Year = book.Year is not null && !overwrite ? book.Year : found.ReleaseDate?.Year ?? book.Year;
        book.Studio ??= details.Publisher;
        book.PosterUrl = Pick(book.PosterUrl, found.Cover);
        book.MetadataSource ??= found.Provider;

        if ((overwrite || book.Genres.Count == 0) && found.Genres.Count > 0)
        {
            book.Genres.Clear();
            foreach (var name in found.Genres.Distinct(StringComparer.OrdinalIgnoreCase).Take(5))
            {
                book.Genres.Add(await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct).ConfigureAwait(false)
                    ?? db.Genres.Local.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                    ?? db.Genres.Add(new Genre { Name = name }).Entity);
            }
        }

        await CreditsAsync(book, CreditRole.Author, found.Authors, overwrite, ct).ConfigureAwait(false);
        await CreditsAsync(book, CreditRole.Narrator, found.Narrators, overwrite, ct).ConfigureAwait(false);

        // A series the files named without a number takes the provider's, for the same series or when each has one.
        var unnumbered = await db.BookSeriesEntries.Include(e => e.Series).Where(e => e.BookId == book.Id).ToListAsync(ct).ConfigureAwait(false);
        if (!overwrite && found.Series.Count > 0 && unnumbered.Count > 0)
        {
            foreach (var entry in unnumbered.Where(e => e.Sequence is null))
            {
                var part = found.Series.FirstOrDefault(p => p.Sequence is not null && BookInfo.SameSeries(p.Name, entry.Series!.Name))
                    ?? (unnumbered.Count == 1 && found.Series.Count == 1 ? found.Series[0] : null);
                if (part?.Sequence is { } sequence)
                {
                    entry.Sequence = sequence;
                    entry.SortOrder = BookAssembler.SequenceOrder(sequence);
                }
            }
        }

        if (found.Series.Count > 0 && (overwrite || unnumbered.Count == 0))
        {
            await db.BookSeriesEntries.Where(e => e.BookId == book.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            foreach (var part in found.Series)
            {
                var stored = await db.BookSeries.FirstOrDefaultAsync(s => s.LibraryId == book.LibraryId && s.Name == part.Name, ct).ConfigureAwait(false)
                    ?? db.BookSeries.Local.FirstOrDefault(s => s.LibraryId == book.LibraryId && s.Name == part.Name)
                    ?? db.BookSeries.Add(new BookSeries { LibraryId = book.LibraryId, Name = part.Name, SortName = SortTitle.For(part.Name) }).Entity;
                db.BookSeriesEntries.Add(new BookSeriesEntry { SeriesId = stored.Id, BookId = book.Id, Sequence = part.Sequence, SortOrder = BookAssembler.SequenceOrder(part.Sequence) });
            }
        }

        // The first author's photo and biography, from Audible.
        if (bookProviders is not null && found.AuthorAsins is { Count: > 0 } asins
            && book.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person).FirstOrDefault() is { } person
            && (person.Biography is null || person.ImageUrl is null)
            && await bookProviders.AuthorAsync(asins[0], ct).ConfigureAwait(false) is { } author)
        {
            person.Asin ??= asins[0];
            person.Biography ??= author.Biography;
            person.ImageUrl ??= author.Image;
        }

        // Audible's chapters, in place of guessed ones, when Audible says they're accurate and the length agrees.
        if (bookProviders is not null && details.Asin is { } asin && details.ChapterSource is ChapterSource.Even or ChapterSource.None
            or ChapterSource.Files && await db.MediaChapters.CountAsync(c => c.MediaItemId == book.Id, ct).ConfigureAwait(false) <= 1)
        {
            await UseAudibleChaptersAsync(book.Id, details, asin, shift: 0, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Replaces a book's chapters with Audible's for <paramref name="asin"/>, moved by <paramref name="shift"/> seconds; false if they don't fit.</summary>
    public async Task<bool> UseAudibleChaptersAsync(Guid bookId, BookDetails details, string asin, double shift, CancellationToken ct, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (bookProviders is null || await bookProviders.ChaptersAsync(asin, ct).ConfigureAwait(false) is not { Chapters.Count: > 0 } audible)
        {
            return false;
        }

        var length = details.DurationSeconds;
        if (!force && (!audible.Accurate || length <= 0 || Math.Abs(audible.RuntimeSeconds - length) > Math.Max(30, length * 0.01)))
        {
            return false;
        }

        await db.MediaChapters.Where(c => c.MediaItemId == bookId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        var chapters = audible.Chapters.Select(c => c with { StartSeconds = Math.Max(0, c.StartSeconds + shift), EndSeconds = Math.Max(0, c.EndSeconds + shift) })
            .Where(c => length <= 0 || c.StartSeconds < length).ToList();
        db.MediaChapters.AddRange(chapters.Select((c, i) => new MediaChapter
        {
            MediaItemId = bookId, Index = i, Title = c.Title, StartSeconds = i == 0 ? 0 : c.StartSeconds,
            EndSeconds = i == chapters.Count - 1 && length > 0 ? length : c.EndSeconds,
        }));
        details.ChapterSource = ChapterSource.Audible;
        return true;
    }

    /// <summary>Credits in a role from a provider: when the book has none in it, or a chosen match says so.</summary>
    private async Task CreditsAsync(MediaItem book, CreditRole role, IReadOnlyList<string> names, bool overwrite, CancellationToken ct)
    {
        var current = book.Credits.Where(c => c.Role == role).ToList();
        if (names.Count == 0 || (current.Count > 0 && !overwrite))
        {
            return;
        }

        foreach (var credit in current)
        {
            book.Credits.Remove(credit);
            db.Credits.Remove(credit);
        }

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            var person = await db.People.FirstOrDefaultAsync(p => p.Name == name, ct).ConfigureAwait(false)
                ?? db.People.Local.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? db.People.Add(new Person { Name = name }).Entity;
            book.Credits.Add(new Credit { MediaItemId = book.Id, Person = person, PersonId = person.Id, Role = role, Order = i });
        }
    }

    /// <summary>A cover the app points at on the web; its own /MediaCover paths need its key, so they're left for other sources.</summary>
    private static string? Cover(IReadOnlyList<ArrImage> images) =>
        images.Where(i => i.CoverType is "cover" or "poster").Select(i => i.RemoteUrl)
            .FirstOrDefault(u => u is not null && u.StartsWith("http", StringComparison.OrdinalIgnoreCase));

    private static bool SameInstance(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string? Plain(string? html) => string.IsNullOrWhiteSpace(html) ? null : BookSidecars.PlainText(html);
}
