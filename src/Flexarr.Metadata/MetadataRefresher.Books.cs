using Flexarr.Core.Books;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Metadata;

/// <summary>Audiobooks: what Readarr, Bookshelf or Chaptarr know about a book fills what its files don't say.</summary>
public sealed partial class MetadataRefresher
{
    /// <summary>What one app knows, fetched once per refresh.</summary>
    /// <param name="Complete">Every author's books, not just those of the authors of the books due.</param>
    private sealed record ReadarrCatalogue(ArrInstance Instance, IReadOnlyList<ReadarrAuthor> Authors, IReadOnlyList<ReadarrBook> Books,
        IReadOnlyList<ReadarrBookFile> Files, IReadOnlyDictionary<string, List<(ReadarrSeries Series, string? Position)>> SeriesByBook, bool Complete);

    /// <summary>A book due a refresh, as working out its author in an app needs it.</summary>
    private sealed record DueBook(string? Path, string? ArrAuthorId, string? ArrInstance, List<string> Authors);

    /// <summary>Calls made at once to one app, for each author's files and series.</summary>
    private const int ArrConcurrency = 4;

    /// <summary>At most this many books due (a webhook's) are fetched by their authors rather than the whole catalogue.</summary>
    private const int FewBooks = 10;

    private enum BookOutcome { FromArr, LookedUp, Unmatched, Local }

    private async Task<MetadataRefreshResult> RefreshBooksAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var result = await RefreshDueBooksAsync(libraryId, force, within, ct).ConfigureAwait(false);

        // A photo put in (or taken from) an author's folder shows after the scan that saw it, not when one of their
        // books is next due.
        await Authors.FolderPhotosAsync(libraryId, within, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        return result;
    }

    private async Task<MetadataRefreshResult> RefreshDueBooksAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var also = await WithinAsync(libraryId, MediaKind.Audiobook, within, ct).ConfigureAwait(false);
        var ids = await Stale(libraryId, MediaKind.Audiobook, force, also).OrderBy(i => i.SortTitle).Select(i => i.Id).ToListAsync(ct).ConfigureAwait(false);
        if (ids.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        // Just a webhook's books due: only their authors' books are asked for, not every author's.
        List<DueBook>? only = null;
        if (within.Count > 0 && ids.Count <= FewBooks && ids.All(also.Contains))
        {
            only = await db.MediaItems.Where(i => ids.Contains(i.Id))
                .Select(i => new DueBook(i.Path, i.Book!.ArrAuthorId, i.Book.ArrInstance,
                    i.Credits.Where(c => c.Role == CreditRole.Author).Select(c => c.Person!.Name).ToList()))
                .ToListAsync(ct).ConfigureAwait(false);
        }

        var catalogues = new List<ReadarrCatalogue>();
        foreach (var readarr in settings.ArrInstances.Where(a => a.Kind == ArrKind.Readarr))
        {
            if (await CatalogueAsync(readarr, only, ct).ConfigureAwait(false) is { } catalogue)
            {
                catalogues.Add(catalogue);
            }
        }

        var shelves = catalogues.Select(c => new ReadarrShelf(c.Instance.Name, c.Books, c.Files, c.Authors)).ToList();
        var readarrAuthors = new ReadarrAuthorIndex(catalogues.SelectMany(c => c.Authors)); // once, not once a book
        int fromArr = 0, lookedUp = 0, unmatched = 0, local = 0;
        // A few books at a time, each saved as it's done: one that fails is logged and the rest go on.
        for (var next = 0; next < ids.Count;)
        {
            var chunk = ids.Skip(next).Take(25).ToList();
            var books = await db.MediaItems.Where(i => chunk.Contains(i.Id))
                .Include(i => i.Book).Include(i => i.Genres).Include(i => i.Files).Include(i => i.Credits).ThenInclude(c => c.Person)
                .AsSplitQuery().ToListAsync(ct).ConfigureAwait(false);
            var failed = false;
            foreach (var id in chunk)
            {
                next++;
                if (books.FirstOrDefault(b => b.Id == id) is not { } book)
                {
                    continue; // gone since
                }

                try
                {
                    var outcome = await RefreshBookAsync(book, catalogues, shelves, readarrAuthors, ct).ConfigureAwait(false);
                    book.MetadataUpdatedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    _ = outcome switch
                    {
                        BookOutcome.FromArr => fromArr++,
                        BookOutcome.LookedUp => lookedUp++,
                        BookOutcome.Unmatched => unmatched++,
                        _ => local++,
                    };
                }
                catch (Exception ex) when (ex is InvalidOperationException or DbUpdateException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
                {
                    // What it left half done is dropped; it's tried again when it's next due, not on every refresh.
                    LogItemFailed(ex, book.Title);
                    db.ChangeTracker.Clear();
                    _series.Clear();
                    var now = DateTime.UtcNow;
                    await db.MediaItems.Where(i => i.Id == id).ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, now), ct).ConfigureAwait(false);
                    unmatched++;
                    failed = true;
                    break; // the rest of the chunk is loaded again, clean
                }
            }

            if (!failed)
            {
                db.ChangeTracker.Clear(); // done with these; keeps each save from looking over every book so far
                _series.Clear();
            }
        }

        return new MetadataRefreshResult(fromArr, lookedUp, unmatched, local);
    }

    /// <summary>One book: matched in a Readarr-compatible app, then looked up online.</summary>
    private async Task<BookOutcome> RefreshBookAsync(MediaItem book, List<ReadarrCatalogue> catalogues, List<ReadarrShelf> shelves, ReadarrAuthorIndex readarrAuthors,
        CancellationToken ct)
    {
        var details = book.Book ??= new BookDetails { MediaItemId = book.Id };
        var wanted = new BookToMatch(book.Path, book.Title,
            [.. book.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name)],
            details.Asin, details.Isbn, details.ArrBookId, details.ArrInstance, [.. book.Files.Select(f => f.Path)]);
        // The app it came from is set up but didn't answer: its link stands rather than one guessed in another app.
        var unanswered = details.ArrInstance is { } from
            && settings.ArrInstances.Any(a => a.Kind == ArrKind.Readarr && SameInstance(a.Name, from))
            && !catalogues.Any(c => SameInstance(c.Instance.Name, from));
        var hit = unanswered ? null : ReadarrMatcher.Match(wanted, shelves, settings.PathMappings);
        if (hit is { } matched)
        {
            await ApplyReadarrAsync(book, details, matched.Book, catalogues[matched.Shelf], ct).ConfigureAwait(false);
        }
        else if (!unanswered && details.ArrBookId is not null && catalogues.All(c => c.Complete))
        {
            // Its app no longer has it (or is gone): the old id would only reach some other book there.
            details.ArrBookId = null;
            details.ArrAuthorId = null;
            details.ArrInstance = null;
        }

        // Online: by its ids, else (unless it was matched by hand) by title and author.
        var online = false;
        IReadOnlyList<string> authorAsins = [];
        if (bookProviders is not null)
        {
            try
            {
                var found = await bookProviders.LookupAsync(details.Asin, details.Isbn, ct).ConfigureAwait(false)
                    ?? (book.MatchLocked ? null : await bookProviders.FindAsync(book.Title, wanted.Authors, ct).ConfigureAwait(false));
                if (found is not null)
                {
                    // Locked: only its authors' photos and biographies come from it (below), not the book's details.
                    if (!book.MatchLocked)
                    {
                        await ApplyOnlineAsync(book, details, found, overwrite: false, ct).ConfigureAwait(false);
                    }

                    authorAsins = found.AuthorAsins ?? [];
                    online = true;
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, book.Title);
            }
        }

        // Every author's photo and biography.
        var matchedAuthor = hit is { } h ? catalogues[h.Shelf].Authors.FirstOrDefault(a => a.Id == h.Book.AuthorId) : null;
        await Authors.FillBookAsync(book, readarrAuthors, matchedAuthor, authorAsins, ct).ConfigureAwait(false);

        return hit is not null ? BookOutcome.FromArr
            : online ? BookOutcome.LookedUp
            : catalogues.Count > 0 || bookProviders is not null ? BookOutcome.Unmatched
            : BookOutcome.Local;
    }

    /// <summary>
    /// An app's authors, books, files and series; null if it can't be asked right now. With <paramref name="only"/>
    /// (a webhook's few books), just their authors' books, files and series. Files and series are asked for author by
    /// author, a few at a time.
    /// </summary>
    private async Task<ReadarrCatalogue?> CatalogueAsync(ArrInstance readarr, IReadOnlyList<DueBook>? only, CancellationToken ct)
    {
        try
        {
            var authors = await arr.GetAuthorsAsync(readarr, ct).ConfigureAwait(false);
            var wanted = only is null ? null : AuthorsOf(readarr, authors, only);
            var authorIds = (wanted ?? authors.Where(a => a.Id is not null).Select(a => a.Id!)).Distinct(StringComparer.Ordinal).ToList();
            var perAuthor = new (IReadOnlyList<ReadarrBook> Books, IReadOnlyList<ReadarrBookFile> Files, IReadOnlyList<ReadarrSeries> Series)[authorIds.Count];
            await Parallel.ForEachAsync(Enumerable.Range(0, authorIds.Count), new ParallelOptions { MaxDegreeOfParallelism = ArrConcurrency, CancellationToken = ct },
                async (i, token) => perAuthor[i] = (
                    wanted is null ? [] : await arr.GetAuthorBooksAsync(readarr, authorIds[i], token).ConfigureAwait(false),
                    await arr.GetBookFilesAsync(readarr, authorIds[i], token).ConfigureAwait(false),
                    await arr.GetBookSeriesAsync(readarr, authorIds[i], token).ConfigureAwait(false))).ConfigureAwait(false);

            var books = wanted is null ? await arr.GetBooksAsync(readarr, ct).ConfigureAwait(false) : [.. perAuthor.SelectMany(a => a.Books)];
            var series = new Dictionary<string, List<(ReadarrSeries, string?)>>(StringComparer.Ordinal);
            foreach (var s in perAuthor.SelectMany(a => a.Series))
            {
                foreach (var link in s.Links.Where(l => l.BookId is not null))
                {
                    (series.TryGetValue(link.BookId!, out var list) ? list : series[link.BookId!] = []).Add((s, link.Position));
                }
            }

            return new ReadarrCatalogue(readarr, authors, books, [.. perAuthor.SelectMany(a => a.Files)], series, Complete: wanted is null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
        {
            LogArrUnavailable(readarr.Name, ex.Message);
            return null;
        }
    }

    /// <summary>
    /// The app's ids for the authors of <paramref name="due"/>: the one each was matched to before, else the author
    /// whose folder holds the book, else one of the same name. A book none of them fits isn't this app's.
    /// </summary>
    private List<string> AuthorsOf(ArrInstance readarr, IReadOnlyList<ReadarrAuthor> authors, IReadOnlyList<DueBook> due)
    {
        var ids = new List<string>();
        foreach (var book in due)
        {
            if (book.ArrAuthorId is { } known && book.ArrInstance is { } from && SameInstance(from, readarr.Name))
            {
                ids.Add(known);
                continue;
            }

            var names = book.Authors.Select(FoldName).ToHashSet(StringComparer.Ordinal);
            ids.AddRange(authors.Where(a => a.Id is not null
                    && ((book.Path is { } path && a.Path is { Length: > 0 } folder && PathComparison.IsUnder(path, PathMapper.Map(folder, settings.PathMappings)))
                        || (a.AuthorName is { } name && names.Contains(FoldName(name)))))
                .Select(a => a.Id!));
        }

        return ids;

        static string FoldName(string value) => new([.. value.Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);
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
        // Locked (edited or matched by hand): the links above, but nothing of the book's own, not even its gaps, so a
        // field emptied on purpose stays empty.
        if (book.MatchLocked)
        {
            return;
        }

        details.Publisher ??= edition?.Publisher;
        details.Language ??= edition?.Language;
        book.Overview ??= Plain(edition?.Overview) ?? Plain(match.Overview);
        book.ReleaseDate ??= match.ReleaseDate is { } released ? DateOnly.FromDateTime(released) : null;
        book.Year ??= book.ReleaseDate?.Year;
        book.Studio ??= details.Publisher;
        book.PosterUrl ??= Cover(match.Images) ?? Cover(edition?.Images ?? []);
        // Where its details came from, as shown; deleting it goes through ArrInstance.
        book.MetadataSource = catalogue.Instance.Name;

        if (book.Genres.Count == 0 && BookGenres.Clean(match.Genres) is { Count: > 0 } readarrGenres)
        {
            foreach (var name in readarrGenres.Take(5))
            {
                book.Genres.Add(await BookGenres.FindAsync(db, name, ct).ConfigureAwait(false));
            }
        }

        // Its series and place in them, when the files didn't say.
        if (match.Id is { } id && catalogue.SeriesByBook.TryGetValue(id, out var places)
            && (await SeriesEntriesAsync(book.Id, ct).ConfigureAwait(false)).Count == 0)
        {
            var added = new List<BookSeriesEntry>();
            foreach (var (series, position) in places.Where(p => p.Series.Title is { Length: > 0 }))
            {
                var stored = await SeriesAsync(book.LibraryId, series.Title!, ct, Plain(series.Description), series.Id).ConfigureAwait(false);
                if (added.All(e => e.SeriesId != stored.Id))
                {
                    added.Add(db.BookSeriesEntries.Add(new BookSeriesEntry { SeriesId = stored.Id, Series = stored, BookId = book.Id, Sequence = position, SortOrder = BookAssembler.SequenceOrder(position) }).Entity);
                }
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

        if ((overwrite || book.Genres.Count == 0) && BookGenres.Clean(found.Genres) is { Count: > 0 } foundGenres)
        {
            book.Genres.Clear();
            foreach (var name in foundGenres.Take(5))
            {
                book.Genres.Add(await BookGenres.FindAsync(db, name, ct).ConfigureAwait(false));
            }
        }

        await CreditsAsync(book, CreditRole.Author, found.Authors, overwrite, ct).ConfigureAwait(false);
        await CreditsAsync(book, CreditRole.Narrator, found.Narrators, overwrite, ct).ConfigureAwait(false);

        // Its series so far: saved, or just given by Readarr and not saved yet. A series the files (or Readarr) named
        // without a number takes the provider's, for the same series or when each has one.
        var entries = await SeriesEntriesAsync(book.Id, ct).ConfigureAwait(false);
        if (!overwrite && found.Series.Count > 0 && entries.Count > 0)
        {
            foreach (var entry in entries.Where(e => e.Sequence is null))
            {
                var part = found.Series.FirstOrDefault(p => p.Sequence is not null && BookInfo.SameSeries(p.Name, entry.Series!.Name))
                    ?? (entries.Count == 1 && found.Series.Count == 1 ? found.Series[0] : null);
                if (part?.Sequence is { } sequence)
                {
                    entry.Sequence = sequence;
                    entry.SortOrder = BookAssembler.SequenceOrder(sequence);
                }
            }
        }

        // The provider's series in place of the book's: when it had none, or this is the match chosen by hand. An
        // entry for a series it's already in is updated where it is (deleting and adding it again would clash).
        if (found.Series.Count > 0 && (overwrite || entries.Count == 0))
        {
            var kept = new List<BookSeriesEntry>();
            foreach (var part in found.Series)
            {
                var stored = await SeriesAsync(book.LibraryId, part.Name, ct).ConfigureAwait(false);
                if (kept.Any(e => e.SeriesId == stored.Id))
                {
                    continue; // the same series twice (Audible's primary and secondary)
                }

                var entry = entries.FirstOrDefault(e => e.SeriesId == stored.Id)
                    ?? db.BookSeriesEntries.Add(new BookSeriesEntry { SeriesId = stored.Id, Series = stored, BookId = book.Id }).Entity;
                entry.Sequence = part.Sequence;
                entry.SortOrder = BookAssembler.SequenceOrder(part.Sequence);
                kept.Add(entry);
            }

            db.BookSeriesEntries.RemoveRange(entries.Where(e => !kept.Contains(e)));
        }

        // Audible's ids for its authors, on those of the same name (their photos and biographies come after, AuthorInfo).
        if (found.AuthorAsins is { Count: > 0 } asins && found.Authors.Count == asins.Count)
        {
            foreach (var credit in book.Credits.Where(c => c.Role == CreditRole.Author && c.Person is { Asin: null }))
            {
                var at = found.Authors.ToList().FindIndex(a => AuthorInfo.Fold(a) == AuthorInfo.Fold(credit.Person!.Name));
                credit.Person!.Asin = at >= 0 ? asins[at] : null;
            }
        }

        // Audible's chapters, in place of guessed ones, when Audible says they're accurate and the length agrees.
        if (bookProviders is not null && details.Asin is { } asin && details.ChapterSource is ChapterSource.Even or ChapterSource.None
            or ChapterSource.Files && await db.MediaChapters.CountAsync(c => c.MediaItemId == book.Id, ct).ConfigureAwait(false) <= 1)
        {
            await UseAudibleChaptersAsync(book.Id, details, asin, shift: 0, ct).ConfigureAwait(false);
        }
    }

    private AuthorInfo? _authors;

    /// <summary>Authors' photos and biographies (see <see cref="AuthorInfo"/>).</summary>
    private AuthorInfo Authors => _authors ??= new AuthorInfo(db, arr, settings, bookProviders, logger);

    /// <summary>
    /// The photos and biographies of a book's authors, after its match was fixed by hand. The caller saves.
    /// </summary>
    public Task FillAuthorsAsync(MediaItem book, IReadOnlyCollection<string> knownAsins, CancellationToken ct) =>
        Authors.FillBookAsync(book, ReadarrAuthorIndex.Empty, null, knownAsins, ct);

    /// <summary>A book's series entries, saved or only added so far, each with its series.</summary>
    private async Task<List<BookSeriesEntry>> SeriesEntriesAsync(Guid bookId, CancellationToken ct)
    {
        var saved = (await db.BookSeriesEntries.Include(e => e.Series).Where(e => e.BookId == bookId).ToListAsync(ct).ConfigureAwait(false))
            .Where(e => db.Entry(e).State != EntityState.Deleted);
        var entries = saved.Concat(db.BookSeriesEntries.Local.Where(e => e.BookId == bookId)).Distinct().ToList();
        foreach (var entry in entries.Where(e => e.Series is null))
        {
            entry.Series = await db.BookSeries.FindAsync([entry.SeriesId], ct).ConfigureAwait(false);
        }

        return entries;
    }

    private readonly Dictionary<Guid, List<BookSeries>> _series = [];

    /// <summary>
    /// The library's series called <paramref name="name"/> ("The Thursday Murder Club" is "Thursday Murder Club"), or
    /// a new one. The library's series are read once a refresh, not once a book.
    /// </summary>
    private async Task<BookSeries> SeriesAsync(Guid libraryId, string name, CancellationToken ct, string? description = null, string? externalId = null)
    {
        if (!_series.TryGetValue(libraryId, out var all))
        {
            all = await db.BookSeries.Where(s => s.LibraryId == libraryId).ToListAsync(ct).ConfigureAwait(false);
            all.AddRange(db.BookSeries.Local.Where(s => s.LibraryId == libraryId && !all.Contains(s)));
            _series[libraryId] = all;
        }

        if (all.FirstOrDefault(s => BookInfo.SameSeries(s.Name, name)) is { } known)
        {
            return known;
        }

        var series = db.BookSeries.Add(new BookSeries { LibraryId = libraryId, Name = name.Trim(), SortName = SortTitle.For(name), Description = description, ExternalId = externalId }).Entity;
        all.Add(series);
        return series;
    }

    /// <summary>Replaces a book's chapters with Audible's for <paramref name="asin"/>, moved by <paramref name="shift"/> seconds; false if they don't fit.</summary>
    public async Task<bool> UseAudibleChaptersAsync(Guid bookId, BookDetails details, string asin, double shift, CancellationToken ct, bool force = false)
    {
        if (await AudibleChaptersAsync(details, asin, shift, ct, force).ConfigureAwait(false) is not { } chapters)
        {
            return false;
        }

        await db.MediaChapters.Where(c => c.MediaItemId == bookId).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.MediaChapters.AddRange(chapters.Select((c, i) => new MediaChapter
        {
            MediaItemId = bookId, Index = i, Title = c.Title, StartSeconds = c.StartSeconds, EndSeconds = c.EndSeconds,
        }));
        details.ChapterSource = ChapterSource.Audible;
        return true;
    }

    /// <summary>
    /// Audible's chapters for <paramref name="asin"/>, moved by <paramref name="shift"/> seconds and fitted to the
    /// book (the first at the start, the last to the end), without saving them; null if there are none (none left once
    /// shifted, too), or (unless <paramref name="force"/>) they don't fit the book's length.
    /// </summary>
    public async Task<List<BookChapter>?> AudibleChaptersAsync(BookDetails details, string asin, double shift, CancellationToken ct, bool force = false)
    {
        ArgumentNullException.ThrowIfNull(details);
        if (bookProviders is null || await bookProviders.ChaptersAsync(asin, ct).ConfigureAwait(false) is not { Chapters.Count: > 0 } audible)
        {
            return null;
        }

        var length = details.DurationSeconds;
        if (!force && (!audible.Accurate || length <= 0 || Math.Abs(audible.RuntimeSeconds - length) > Math.Max(30, length * 0.01)))
        {
            return null;
        }

        var chapters = audible.Chapters.Select(c => c with { StartSeconds = Math.Max(0, c.StartSeconds + shift), EndSeconds = Math.Max(0, c.EndSeconds + shift) })
            .Where(c => length <= 0 || c.StartSeconds < length).ToList();
        if (chapters.Count == 0)
        {
            return null; // Shifted past the end: saving none would only delete the book's own.
        }

        return chapters.Select((c, i) => c with
        {
            StartSeconds = i == 0 ? 0 : c.StartSeconds,
            EndSeconds = i == chapters.Count - 1 && length > 0 ? length : c.EndSeconds,
        }).ToList();
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
