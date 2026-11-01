using Flexarr.Api.Endpoints;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>How a book list is narrowed and ordered. Every field is optional.</summary>
/// <param name="Progress">"notstarted", "inprogress" or "finished".</param>
/// <param name="Sort">"title" (the default), "author", "series", "added", "released", "length" or "listened".</param>
/// <param name="Descending">Reverse the order; left out, newest and longest come first, titles A to Z.</param>
public sealed record BookFilter(
    string? Search = null, string? Progress = null, Guid? Author = null, Guid? Narrator = null, string? Genre = null, string? Language = null,
    bool? Abridged = null, double? MinHours = null, double? MaxHours = null, string? Sort = null, bool? Descending = null);

public static class BookQueries
{
    /// <summary>The books of <paramref name="libraryId"/> on disk that something can play.</summary>
    public static IQueryable<MediaItem> Books(FlexarrDbContext db, Guid libraryId) =>
        db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == MediaKind.Audiobook && i.MissingSince == null
            && i.Files.Any(f => f.ProbeError == null && f.MissingSince == null));

    /// <summary>Started and not finished: what Continue listening shows.</summary>
    public static IQueryable<MediaItem> InProgress(IQueryable<MediaItem> books, string profileId) =>
        books.Where(i => i.PlaybackStates.Any(p => p.UserId == profileId && !p.Watched && p.PositionSeconds > 0));

    public static IQueryable<MediaItem> Filter(IQueryable<MediaItem> books, BookFilter filter, string profileId)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (filter.Search is { Length: > 0 } search)
        {
            var like = $"%{search.Trim()}%";
            books = books.Where(i => EF.Functions.Like(i.Title, like)
                || i.Credits.Any(c => (c.Role == CreditRole.Author || c.Role == CreditRole.Narrator) && EF.Functions.Like(c.Person!.Name, like))
                || i.SeriesEntries.Any(e => EF.Functions.Like(e.Series!.Name, like)));
        }

        books = filter.Progress switch
        {
            "notstarted" => books.Where(i => !i.PlaybackStates.Any(p => p.UserId == profileId && (p.Watched || p.PositionSeconds > 0 || p.FinishedAt != null))),
            "inprogress" => InProgress(books, profileId),
            "finished" => books.Where(i => i.PlaybackStates.Any(p => p.UserId == profileId && (p.Watched || p.FinishedAt != null))),
            _ => books,
        };

        if (filter.Author is { } author)
        {
            books = books.Where(i => i.Credits.Any(c => c.Role == CreditRole.Author && c.PersonId == author));
        }

        if (filter.Narrator is { } narrator)
        {
            books = books.Where(i => i.Credits.Any(c => c.Role == CreditRole.Narrator && c.PersonId == narrator));
        }

        if (filter.Genre is { Length: > 0 } genre)
        {
            books = books.Where(i => i.Genres.Any(g => g.Name == genre));
        }

        if (filter.Language is { Length: > 0 } language)
        {
            // "en", "eng" and "English" are one language: books tagged with any of them, in any case, match. SQL's
            // lower(), which ToLower becomes, has no culture to pass and folds only A to Z, so "Русский" or "Čeština"
            // is matched as it's written too: the tag as it was offered, and the names in their usual cases.
            var names = LanguageNames(language);
            var written = LanguageSpellings(language, names);
#pragma warning disable CA1304, CA1311
            books = books.Where(i => names.Contains(i.Book!.Language!.ToLower()) || written.Contains(i.Book!.Language!));
#pragma warning restore CA1304, CA1311
        }

        if (filter.Abridged is { } abridged)
        {
            books = books.Where(i => i.Book!.Abridged == abridged);
        }

        if (filter.MinHours is { } min)
        {
            books = books.Where(i => i.Book!.DurationSeconds >= min * 3600);
        }

        if (filter.MaxHours is { } max)
        {
            books = books.Where(i => i.Book!.DurationSeconds <= max * 3600);
        }

        return Sort(books, filter.Sort, filter.Descending, profileId);
    }

    private static IQueryable<MediaItem> Sort(IQueryable<MediaItem> books, string? sort, bool? descending, string profileId)
    {
        // Newest, longest and most recently heard first unless asked otherwise; names A to Z.
        var reverse = descending ?? sort is "added" or "released" or "length" or "listened";
        return sort switch
        {
            "author" => Order(books, i => i.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name).FirstOrDefault(), reverse),
            "series" => reverse
                ? books.OrderByDescending(i => i.SeriesEntries.Select(e => e.Series!.SortName).FirstOrDefault()).ThenByDescending(i => i.SeriesEntries.Select(e => e.SortOrder).FirstOrDefault())
                : books.OrderBy(i => i.SeriesEntries.Select(e => e.Series!.SortName).FirstOrDefault() == null)
                    .ThenBy(i => i.SeriesEntries.Select(e => e.Series!.SortName).FirstOrDefault()).ThenBy(i => i.SeriesEntries.Select(e => e.SortOrder).FirstOrDefault()),
            "added" => Order(books, i => i.AddedAt, reverse),
            "released" => Order(books, i => i.ReleaseDate, reverse),
            "length" => Order(books, i => i.Book!.DurationSeconds, reverse),
            "listened" => Order(books, i => i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => (DateTime?)p.LastPlayedAt).FirstOrDefault(), reverse),
            _ => Order(books, i => i.SortTitle, reverse),
        };
    }

    private static IOrderedQueryable<MediaItem> Order<T>(IQueryable<MediaItem> books, System.Linq.Expressions.Expression<Func<MediaItem, T>> key, bool reverse) =>
        reverse ? books.OrderByDescending(key).ThenBy(i => i.SortTitle) : books.OrderBy(key).ThenBy(i => i.SortTitle);

    /// <summary>A series' books in reading order (by number, then release), with this profile's finished ones.</summary>
    private sealed record SeriesBook(Guid SeriesId, Guid BookId, string? Sequence, double? SortOrder, DateOnly? Released, string SortTitle, bool Finished, bool Started, DateTime? FinishedAt);

    private static async Task<List<SeriesBook>> SeriesBooksAsync(FlexarrDbContext db, IQueryable<MediaItem> books, IQueryable<BookSeries> series, string profileId, CancellationToken ct) =>
        await db.BookSeriesEntries.Where(e => series.Any(s => s.Id == e.SeriesId) && books.Any(b => b.Id == e.BookId))
            .Select(e => new SeriesBook(e.SeriesId, e.BookId, e.Sequence, e.SortOrder, e.Book!.ReleaseDate, e.Book.SortTitle,
                e.Book.PlaybackStates.Any(p => p.UserId == profileId && (p.Watched || p.FinishedAt != null)),
                e.Book.PlaybackStates.Any(p => p.UserId == profileId && !p.Watched && p.PositionSeconds > 0),
                e.Book.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.FinishedAt).FirstOrDefault()))
            .ToListAsync(ct);

    private static IEnumerable<SeriesBook> InReadingOrder(IEnumerable<SeriesBook> books) =>
        books.OrderBy(b => b.SortOrder is null).ThenBy(b => b.SortOrder).ThenBy(b => b.Released).ThenBy(b => b.SortTitle, StringComparer.Ordinal);

    /// <summary>The first book in reading order this profile hasn't finished.</summary>
    private static SeriesBook? NextOf(IEnumerable<SeriesBook> books) => InReadingOrder(books).FirstOrDefault(b => !b.Finished);

    public static async Task<PagedDto<BookSeriesSummaryDto>> SeriesListAsync(FlexarrDbContext db, Guid libraryId, string? search, int skip, int take, string profileId, CancellationToken ct)
    {
        var books = Books(db, libraryId);
        var series = db.BookSeries.Where(s => s.LibraryId == libraryId && s.Entries.Any(e => books.Any(b => b.Id == e.BookId)));
        if (search is { Length: > 0 })
        {
            series = series.Where(s => EF.Functions.Like(s.Name, $"%{search.Trim()}%"));
        }

        var total = await series.CountAsync(ct);
        var page = await series.OrderBy(s => s.SortName).Skip(skip).Take(take).Select(s => new { s.Id, s.Name }).ToListAsync(ct);
        var ids = page.Select(p => p.Id).ToList();
        var entries = await SeriesBooksAsync(db, books, db.BookSeries.Where(s => ids.Contains(s.Id)), profileId, ct);
        return new PagedDto<BookSeriesSummaryDto>(total, await SummariesAsync(db, page.Select(p => (p.Id, p.Name)).ToList(), entries, profileId, ct));
    }

    private static async Task<List<BookSeriesSummaryDto>> SummariesAsync(FlexarrDbContext db, List<(Guid Id, string Name)> series, List<SeriesBook> entries, string profileId, CancellationToken ct)
    {
        var bySeries = entries.ToLookup(e => e.SeriesId);
        var wanted = series.SelectMany(s => InReadingOrder(bySeries[s.Id]).Take(3).Select(b => b.BookId).Append(NextOf(bySeries[s.Id])?.BookId ?? Guid.Empty)).Distinct().ToList();
        var summaries = await db.MediaItems.Where(i => wanted.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return [.. series.Select(s =>
        {
            var books = bySeries[s.Id].ToList();
            var next = NextOf(books);
            return new BookSeriesSummaryDto(s.Id, s.Name, books.Count, books.Count(b => b.Finished),
                [.. InReadingOrder(books).Take(3).Select(b => summaries.GetValueOrDefault(b.BookId)?.Artwork).OfType<ArtworkDto>()],
                next is null ? null : summaries.GetValueOrDefault(next.BookId));
        })];
    }

    public static async Task<BookSeriesDto?> SeriesAsync(FlexarrDbContext db, Guid seriesId, string profileId, CancellationToken ct)
    {
        var series = await db.BookSeries.AsNoTracking().FirstOrDefaultAsync(s => s.Id == seriesId, ct);
        if (series is null)
        {
            return null;
        }

        var books = Books(db, series.LibraryId);
        var entries = InReadingOrder(await SeriesBooksAsync(db, books, db.BookSeries.Where(s => s.Id == seriesId), profileId, ct)).ToList();
        var ids = entries.Select(e => e.BookId).ToList();
        var summaries = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return new BookSeriesDto(series.Id, series.LibraryId, series.Name, series.Description,
            [.. entries.Where(e => summaries.ContainsKey(e.BookId)).Select(e => new BookSeriesEntryDto(summaries[e.BookId], e.Sequence))],
            NextOf(entries)?.BookId);
    }

    public static async Task<PagedDto<BookPersonDto>> PeopleAsync(FlexarrDbContext db, Guid libraryId, CreditRole role, string? search, int skip, int take, CancellationToken ct)
    {
        // From the library's book credits, grouped by person, rather than from everyone the server knows.
        var books = Books(db, libraryId).Select(b => b.Id);
        var counts = db.Credits.Where(c => c.Role == role && books.Contains(c.MediaItemId))
            .GroupBy(c => c.PersonId)
            .Select(g => new { PersonId = g.Key, Count = g.Count() });
        var people = from c in counts
                     join p in db.People on c.PersonId equals p.Id
                     select new { p.Id, p.Name, HasImage = p.ImageUrl != null, p.ImageChangedAt, c.Count };
        if (search is { Length: > 0 })
        {
            people = people.Where(p => EF.Functions.Like(p.Name, $"%{search.Trim()}%"));
        }

        var total = await people.CountAsync(ct);
        var page = await people.OrderBy(p => p.Name).Skip(skip).Take(take).ToListAsync(ct);
        return new PagedDto<BookPersonDto>(total, [.. page.Select(p => new BookPersonDto(p.Id, p.Name, ArtworkUrls.Person(p.Id, p.HasImage, p.ImageChangedAt), p.Count))]);
    }

    public static async Task<BookShelvesDto> ShelvesAsync(FlexarrDbContext db, Guid libraryId, string profileId, CancellationToken ct)
    {
        const int Shelf = 20;
        var books = Books(db, libraryId);
        var continuing = await InProgress(books, profileId)
            .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.LastPlayedAt).FirstOrDefault())
            .Take(Shelf).Summaries(profileId).ToListAsync(ct);
        var recent = await books.OrderByDescending(i => i.AddedAt).Take(Shelf).Summaries(profileId).ToListAsync(ct);
        var finished = await books.Where(i => i.PlaybackStates.Any(p => p.UserId == profileId && p.FinishedAt != null))
            .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.FinishedAt).FirstOrDefault())
            .Take(Shelf).Summaries(profileId).ToListAsync(ct);

        var next = await NextInSeriesAsync(db, books, profileId, Shelf, ct);
        var series = await SeriesListAsync(db, libraryId, null, 0, Shelf, profileId, ct);
        var authors = await PeopleAsync(db, libraryId, CreditRole.Author, null, 0, Shelf, ct);
        var narrators = await PeopleAsync(db, libraryId, CreditRole.Narrator, null, 0, Shelf, ct);
        var genres = await db.Genres.Select(g => new { g.Name, Count = g.Items.Count(i => books.Any(b => b.Id == i.Id)) })
            .Where(g => g.Count > 0).OrderByDescending(g => g.Count).ThenBy(g => g.Name).Take(200).ToListAsync(ct);

        var languages = await books.Select(i => i.Book!.Language).Where(l => l != null && l != "").Distinct().OrderBy(l => l).ToListAsync(ct);

        return new BookShelvesDto(continuing, next, recent, series.Items, authors.Items, narrators.Items,
            [.. genres.Select(g => new GenreCountDto(g.Name, g.Count))], finished, [.. languages.OfType<string>()]);
    }

    /// <summary>
    /// For each series this profile has finished a book of, the next one it hasn't started: most recently finished
    /// series first. Books already under way are in Continue listening instead.
    /// </summary>
    public static async Task<List<ItemSummaryDto>> NextInSeriesAsync(FlexarrDbContext db, IQueryable<MediaItem> books, string profileId, int take, CancellationToken ct)
    {
        var series = db.BookSeries.Where(s => s.Entries.Any(e => e.Book!.PlaybackStates.Any(p => p.UserId == profileId && p.FinishedAt != null)));
        var entries = await SeriesBooksAsync(db, books, series, profileId, ct);
        var picks = entries.GroupBy(e => e.SeriesId)
            .Select(g => (Next: NextOf(g), LastFinished: g.Max(b => b.FinishedAt)))
            .Where(p => p.Next is { Started: false })
            .OrderByDescending(p => p.LastFinished)
            .Select(p => p.Next!.BookId).Distinct().Take(take).ToList();
        var summaries = await db.MediaItems.Where(i => picks.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return [.. picks.Where(summaries.ContainsKey).Select(id => summaries[id])];
    }

    /// <summary>The book after <paramref name="bookId"/> in its series (the first series it's numbered in), if the library has it.</summary>
    public static async Task<ItemSummaryDto?> NextInSeriesAfterAsync(FlexarrDbContext db, MediaItem book, string profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        var mine = await db.BookSeriesEntries.Where(e => e.BookId == book.Id).OrderBy(e => e.SortOrder == null).Select(e => new { e.SeriesId, e.SortOrder }).ToListAsync(ct);
        var books = Books(db, book.LibraryId);
        foreach (var entry in mine.Where(m => m.SortOrder is not null))
        {
            var next = await db.BookSeriesEntries.Where(e => e.SeriesId == entry.SeriesId && e.SortOrder > entry.SortOrder && books.Any(b => b.Id == e.BookId))
                .OrderBy(e => e.SortOrder).Select(e => e.BookId).FirstOrDefaultAsync(ct);
            if (next != Guid.Empty)
            {
                return await db.MediaItems.Where(i => i.Id == next).Summaries(profileId).FirstOrDefaultAsync(ct);
            }
        }

        return null;
    }

    public static IQueryable<BookChapterDto> Chapters(FlexarrDbContext db, Guid bookId) =>
        db.MediaChapters.Where(c => c.MediaItemId == bookId).OrderBy(c => c.Index).Select(c => new BookChapterDto(c.Index, c.Title, c.StartSeconds, c.EndSeconds));

    public static IQueryable<BookmarkDto> Bookmarks(FlexarrDbContext db, Guid bookId, string profileId) =>
        db.Bookmarks.Where(b => b.MediaItemId == bookId && b.ProfileId == profileId).OrderBy(b => b.PositionSeconds)
            .Select(b => new BookmarkDto(b.Id, b.PositionSeconds, b.Note, b.CreatedAt));

    /// <summary>A book's own page: edition, series, chapters, bookmarks and the rows of more to listen to.</summary>
    public static async Task<BookDetailDto> DetailAsync(FlexarrDbContext db, MediaItem book, string profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(book);
        const int Row = 20;
        var details = await db.BookDetails.AsNoTracking().FirstOrDefaultAsync(d => d.MediaItemId == book.Id, ct) ?? new BookDetails();
        var books = Books(db, book.LibraryId).Where(i => i.Id != book.Id);
        var series = await db.BookSeriesEntries.Where(e => e.BookId == book.Id)
            .Select(e => new BookSeriesRefDto(e.SeriesId, e.Series!.Name, e.Sequence, e.Series.Entries.Count(x => books.Any(b => b.Id == x.BookId)) + 1))
            .ToListAsync(ct);
        var seriesIds = series.Select(s => s.Id).ToList();
        var credited = (await db.Credits.Where(c => c.MediaItemId == book.Id && c.Role == CreditRole.Author).OrderBy(c => c.Order)
                .Select(c => new { c.PersonId, c.Person!.Name, HasImage = c.Person.ImageUrl != null, c.Person.ImageChangedAt, c.Person.Biography }).ToListAsync(ct))
            .DistinctBy(c => c.PersonId).ToList();
        var authors = credited.Select(c => c.PersonId).ToList();
        var narrators = await db.Credits.Where(c => c.MediaItemId == book.Id && c.Role == CreditRole.Narrator).Select(c => c.PersonId).ToListAsync(ct);
        var state = await db.PlaybackStates.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == profileId && p.MediaItemId == book.Id, ct);
        var speed = await db.ShowPlaybackSpeeds.Where(s => s.ProfileId == profileId && s.SeriesId == book.Id).Select(s => (double?)s.Speed).FirstOrDefaultAsync(ct);

        var inSeries = (await db.BookSeriesEntries.Where(e => seriesIds.Contains(e.SeriesId) && books.Any(b => b.Id == e.BookId))
            .Select(e => new { e.BookId, e.SortOrder }).ToListAsync(ct))
            .OrderBy(e => e.SortOrder is null).ThenBy(e => e.SortOrder).Select(e => e.BookId).Distinct().Take(Row).ToList();
        var moreInSeries = await db.MediaItems.Where(i => inSeries.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);

        return new BookDetailDto(
            details.Subtitle, details.Asin, details.Isbn, details.Publisher, details.Language, details.Abridged, details.Explicit, details.DurationSeconds,
            details.ChapterSource, state?.FinishedAt, speed, series,
            await Chapters(db, book.Id).ToListAsync(ct),
            await Bookmarks(db, book.Id, profileId).ToListAsync(ct),
            [.. inSeries.Where(moreInSeries.ContainsKey).Select(id => moreInSeries[id])],
            await books.Where(i => !inSeries.Contains(i.Id) && i.Credits.Any(c => c.Role == CreditRole.Author && authors.Contains(c.PersonId)))
                .OrderByDescending(i => i.ReleaseDate).Take(Row).Summaries(profileId).ToListAsync(ct),
            await books.Where(i => i.Credits.Any(c => c.Role == CreditRole.Narrator && narrators.Contains(c.PersonId))
                    && !i.Credits.Any(c => c.Role == CreditRole.Author && authors.Contains(c.PersonId)))
                .OrderByDescending(i => i.AddedAt).Take(Row).Summaries(profileId).ToListAsync(ct),
            [.. credited.Select(c => new BookAuthorDto(c.PersonId, c.Name, ArtworkUrls.Person(c.PersonId, c.HasImage, c.ImageChangedAt), c.Biography))]);
    }

    /// <summary>
    /// The ways one language is written in tags, lower case: <paramref name="language"/> itself, and when it names a
    /// known language, its two- and three-letter codes (the older bibliographic one too, "ger" beside "deu") and its
    /// English and own names.
    /// </summary>
    internal static List<string> LanguageNames(string language)
    {
        language = language.Trim();
        var names = new HashSet<string>(StringComparer.Ordinal) { language.ToLowerInvariant() };
        if (Terminological.TryGetValue(language, out var terminological))
        {
            language = terminological;
        }

        var culture = System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.NeutralCultures)
            .FirstOrDefault(c => c.Name.Length > 0 && (string.Equals(c.TwoLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.ThreeLetterISOLanguageName, language, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.EnglishName, language, StringComparison.OrdinalIgnoreCase)
                || string.Equals(c.NativeName, language, StringComparison.OrdinalIgnoreCase)));
        if (culture is not null)
        {
            foreach (var name in new[] { culture.TwoLetterISOLanguageName, culture.ThreeLetterISOLanguageName, culture.EnglishName, culture.NativeName })
            {
                names.Add(name.ToLowerInvariant());
            }

            if (Bibliographic.TryGetValue(culture.ThreeLetterISOLanguageName, out var bibliographic))
            {
                names.Add(bibliographic);
            }
        }

        return [.. names];
    }

    /// <summary>
    /// The ways <paramref name="names"/> (from <see cref="LanguageNames"/>) are written beyond lower case, for tags SQL's
    /// lower() can't fold: <paramref name="language"/> as asked, and each name capitalised and in capitals.
    /// </summary>
    internal static List<string> LanguageSpellings(string language, IEnumerable<string> names)
    {
        var written = new HashSet<string>(StringComparer.Ordinal) { language.Trim() };
        foreach (var name in names)
        {
            written.Add(name);
            written.Add(name.ToUpperInvariant());
            if (name.Length > 0)
            {
                written.Add(string.Concat(char.ToUpperInvariant(name[0]).ToString(), name[1..]));
            }
        }

        return [.. written];
    }

    /// <summary>
    /// ISO 639-2's bibliographic codes, by the terminological code .NET knows each language by: older tags (and
    /// libraries' catalogues) say "ger" and "fre" where .NET says "deu" and "fra".
    /// </summary>
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["deu"] = "ger", ["fra"] = "fre", ["zho"] = "chi", ["nld"] = "dut", ["ces"] = "cze", ["ell"] = "gre", ["fas"] = "per",
        ["ron"] = "rum", ["slk"] = "slo", ["cym"] = "wel", ["hye"] = "arm", ["eus"] = "baq", ["mya"] = "bur", ["kat"] = "geo",
        ["isl"] = "ice", ["mkd"] = "mac", ["mri"] = "mao", ["msa"] = "may", ["sqi"] = "alb", ["bod"] = "tib",
    };

    /// <summary><see cref="Bibliographic"/> the other way round.</summary>
    private static readonly Dictionary<string, string> Terminological =
        Bibliographic.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
}
