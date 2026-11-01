using System.Globalization;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Core.Books;

/// <summary>
/// Puts books together once a scan has found their files: the files in order on one timeline, the book's details,
/// authors, narrators and series from what the files say (sidecars, then tags, then the path), and its chapters. Runs
/// only for books whose files changed, from what the scanner read of each file when it last changed
/// (<see cref="MediaFile.BookProbe"/>): only files with no such reading (scanned before it was kept) are read here, a
/// few at a time. Local always wins: what's found here replaces what's stored, and what isn't found is left alone for
/// the *arr and online providers to fill.
/// </summary>
public sealed partial class BookAssembler(FlexarrDbContext db, IMediaProbe probe, ILogger logger)
{
    private static readonly StringComparer Natural = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.NumericOrdering | CompareOptions.IgnoreCase);
    private const int ProbeConcurrency = 4;

    public async Task AssembleAsync(IReadOnlyCollection<Guid> bookIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bookIds);
        foreach (var chunk in bookIds.Chunk(50))
        {
            var books = await db.MediaItems.IgnoreQueryFilters()
                .Where(i => chunk.Contains(i.Id) && i.Kind == MediaKind.Audiobook)
                .Include(i => i.Files).Include(i => i.Book).Include(i => i.Credits).ThenInclude(c => c.Person).Include(i => i.Genres)
                .AsSplitQuery()
                .ToListAsync(ct).ConfigureAwait(false);
            var libraryIds = books.Select(b => b.LibraryId).Distinct().ToList();
            var roots = await db.LibraryFolders.Where(f => libraryIds.Contains(f.LibraryId)).Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct).ConfigureAwait(false);

            foreach (var book in books)
            {
                var root = roots.Where(r => r.LibraryId == book.LibraryId && book.Path is not null && PathComparison.IsUnder(book.Path, r.Path))
                    .Select(r => r.Path).OrderByDescending(p => p.Length).FirstOrDefault();
                if (root is null)
                {
                    continue;
                }

                await AssembleAsync(book, root, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
    }

    private async Task AssembleAsync(MediaItem book, string root, CancellationToken ct)
    {
        // Protected files are listed in library health, but have no place on the timeline.
        foreach (var file in book.Files.Where(f => Scanning.MediaFileEnumerator.ProtectedBookExtensions.Contains(Path.GetExtension(f.Path))))
        {
            file.BookOrder = null;
        }

        var files = book.Files.Where(f => f.MissingSince is null && !Scanning.MediaFileEnumerator.ProtectedBookExtensions.Contains(Path.GetExtension(f.Path))).ToList();
        if (files.Count == 0)
        {
            return; // Held as missing by the scanner; its order and chapters stay as they were.
        }

        var probes = new Dictionary<Guid, BookFileProbe?>();
        var unread = new List<MediaFile>();
        foreach (var file in files)
        {
            if (BookFileProbe.FromJson(file.BookProbe) is { } kept)
            {
                probes[file.Id] = kept;
            }
            else
            {
                unread.Add(file);
            }
        }

        // Files scanned before their readings were kept: read now, and kept.
        var read = new System.Collections.Concurrent.ConcurrentDictionary<Guid, BookFileProbe?>();
        await Parallel.ForEachAsync(unread, new ParallelOptions { MaxDegreeOfParallelism = ProbeConcurrency, CancellationToken = ct }, async (file, token) =>
        {
            try
            {
                read[file.Id] = await probe.ProbeBookFileAsync(file.Path, token).ConfigureAwait(false);
            }
            catch (MediaProbeException ex)
            {
                read[file.Id] = null;
                LogProbeFailed(PathComparison.Printable(file.Path), PathComparison.Printable(ex.Message));
            }
        }).ConfigureAwait(false);
        foreach (var file in unread)
        {
            probes[file.Id] = read[file.Id];
            file.BookProbe = read[file.Id]?.ToJson();
        }

        var key = book.Path!;
        var ordered = Order(files, f => BookLayout.DiscOf(key, f.Path), f => probes[f.Id]?.Tags);
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].BookOrder = i;
        }

        var details = book.Book ??= new BookDetails { MediaItemId = book.Id };
        details.DurationSeconds = ordered.Sum(f => f.DurationSeconds ?? probes[f.Id]?.DurationSeconds ?? 0);

        // Sidecars, then the first file's tags, then the path: each fills only what the one above leaves out.
        var isFile = files.Count == 1 && PathComparison.Key(files[0].Path) == PathComparison.Key(key);
        var first = probes[ordered[0].Id];
        var tags = first is null ? BookInfo.Empty : BookTags.Read(first.Tags, singleFile: ordered.Count == 1);
        // Files that disagree on their album (each disc of a set named apart) don't name the book: its folder does.
        var albums = ordered.Select(f => probes[f.Id]?.Tags is { } t && t.TryGetValue("album", out var a) ? a.Trim().ToLowerInvariant() : null).OfType<string>().Distinct().Count();
        if (albums > 1)
        {
            tags = tags with { Title = null };
        }

        if (first?.HasCoverArt == true)
        {
            tags = tags with { Cover = $"embedded://{ordered[0].Path}" };
        }

        var sidecars = isFile ? BookInfo.Empty : BookSidecars.Read(key);
        var info = sidecars.Over(tags).Over(FromPath(BookLayout.Describe(root, key, isFile)));
        if (info.Title is { } named && BookTags.SplitSeriesNumber(named) is { Sequence: { } number } split && split.Title.Length > 0)
        {
            // "TMC 03 The Bullet That Missed": the number is its place in the series, when the series has none.
            info = info with
            {
                Title = split.Title,
                Series = [.. info.Series.Select(s => s.Sequence is null ? s with { Sequence = number } : s)],
            };
        }

        if (!book.MatchLocked)
        {
            await ApplyAsync(book, details, info, ct).ConfigureAwait(false); // matched by hand: what was chosen stands
        }

        // Chapters someone edited, or took from Audible, are kept: the files can't say anything better.
        if (details.ChapterSource is not (ChapterSource.Edited or ChapterSource.Audible))
        {
            var sources = ordered.Select(f => new ChapterSourceFile(f.Path, f.DurationSeconds ?? probes[f.Id]?.DurationSeconds ?? 0, probes[f.Id]?.Tags, probes[f.Id]?.Chapters ?? [])).ToList();
            var (chapters, source) = BookChapters.Build(sources, isFile ? null : CueSheet(key), book.Title);
            await ReplaceChaptersAsync(book.Id, chapters, ct).ConfigureAwait(false);
            details.ChapterSource = source;
        }
    }

    /// <summary>The text of the only .cue sheet in a book's folder.</summary>
    private static string? CueSheet(string folder)
    {
        try
        {
            var sheets = Directory.EnumerateFiles(folder, "*.cue").Take(2).ToList();
            return sheets.Count == 1 ? File.ReadAllText(sheets[0]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Puts <paramref name="chapters"/> in place of the book's stored ones.</summary>
    public async Task ReplaceChaptersAsync(Guid bookId, IReadOnlyList<BookChapter> chapters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(chapters);
        var stored = await db.MediaChapters.Where(c => c.MediaItemId == bookId).ToListAsync(ct).ConfigureAwait(false);
        db.MediaChapters.RemoveRange(stored);
        if (stored.Count > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // the unique (book, index) would clash with the new rows
        }

        db.MediaChapters.AddRange(chapters.Select((c, i) => new MediaChapter
        {
            MediaItemId = bookId, Index = i, Title = c.Title.Length > 500 ? c.Title[..500] : c.Title, StartSeconds = c.StartSeconds, EndSeconds = c.EndSeconds,
        }));
    }

    /// <summary>
    /// A book's files in playing order: by disc (folder, tag or "1-01" name), then track tag, then name with numbers
    /// read as numbers ("Part 2" before "Part 10").
    /// </summary>
    public static List<MediaFile> Order(IEnumerable<MediaFile> files, Func<MediaFile, int?> disc, Func<MediaFile, IReadOnlyDictionary<string, string>?> tags)
    {
        ArgumentNullException.ThrowIfNull(disc);
        ArgumentNullException.ThrowIfNull(tags);
        return [.. files
            .OrderBy(f => disc(f) ?? TagNumber(tags(f), "disc") ?? 0)
            .ThenBy(f => TagNumber(tags(f), "track") ?? int.MaxValue)
            .ThenBy(f => Path.GetFileName(f.Path), Natural)];
    }

    /// <summary>The number in a tag like "3" or "3/12".</summary>
    internal static int? TagNumber(IReadOnlyDictionary<string, string>? tags, string name)
    {
        if (tags is null || !tags.TryGetValue(name, out var value))
        {
            return null;
        }

        var head = value.Split('/')[0].Trim();
        return int.TryParse(head, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private static BookInfo FromPath(BookPlace place) => new()
    {
        Title = place.Title,
        Authors = place.Authors,
        Narrators = place.Narrator is { } n ? [n] : [],
        Series = place.Series is { } s ? [new SeriesPart(s, place.Sequence)] : [],
        Year = place.Year,
        Asin = place.Asin,
    };

    /// <summary>Stores what <paramref name="info"/> says; what it doesn't say is left as it was.</summary>
    private async Task ApplyAsync(MediaItem book, BookDetails details, BookInfo info, CancellationToken ct)
    {
        if (info.Title is { Length: > 0 } title && !book.MatchLocked)
        {
            book.Title = title;
            book.SortTitle = SortTitle.For(title);
        }

        book.Year = info.Year ?? info.ReleaseDate?.Year ?? book.Year;
        book.ReleaseDate = info.ReleaseDate ?? book.ReleaseDate;
        book.Overview = info.Description ?? book.Overview;
        book.Studio = info.Publisher ?? book.Studio;
        if (info.Cover is { } cover)
        {
            book.PosterUrl = cover.StartsWith("embedded://", StringComparison.Ordinal) ? cover : $"file://{cover}";
        }

        details.Subtitle = info.Subtitle ?? details.Subtitle;
        details.Publisher = info.Publisher ?? details.Publisher;
        details.Language = info.Language is { } language && !BookTags.Undetermined(language) ? language : details.Language is { } kept && BookTags.Undetermined(kept) ? null : details.Language;
        details.Asin = info.Asin ?? details.Asin;
        details.Isbn = info.Isbn ?? details.Isbn;
        details.Abridged = info.Abridged ?? details.Abridged;
        details.Explicit = info.Explicit ?? details.Explicit;

        await SetCreditsAsync(book, CreditRole.Author, info.Authors, ct).ConfigureAwait(false);
        await SetCreditsAsync(book, CreditRole.Narrator, info.Narrators, ct).ConfigureAwait(false);
        await SetCreditsAsync(book, CreditRole.Translator, info.Translators, ct).ConfigureAwait(false);
        await SetSeriesAsync(book, info.Series, ct).ConfigureAwait(false);
        await SetGenresAsync(book, info.Genres, ct).ConfigureAwait(false);
    }

    private readonly Dictionary<string, Person> _people = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the book's credits in <paramref name="role"/> when <paramref name="names"/> has any.</summary>
    private async Task SetCreditsAsync(MediaItem book, CreditRole role, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            return;
        }

        var current = book.Credits.Where(c => c.Role == role).OrderBy(c => c.Order).ToList();
        if (current.Select(c => c.Person?.Name).SequenceEqual(names, StringComparer.OrdinalIgnoreCase))
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
            var person = await PersonAsync(names[i], ct).ConfigureAwait(false);
            book.Credits.Add(new Credit { MediaItemId = book.Id, Person = person, PersonId = person.Id, Role = role, Order = i });
        }
    }

    /// <summary>
    /// The person called <paramref name="name"/>: someone already credited on a book first (an author or narrator),
    /// then anyone of that name, then someone new.
    /// </summary>
    private async Task<Person> PersonAsync(string name, CancellationToken ct)
    {
        name = name.Trim();
        if (_people.TryGetValue(name, out var known))
        {
            return known;
        }

        var bookRoles = new[] { CreditRole.Author, CreditRole.Narrator, CreditRole.Translator };
        var person = await db.People.Where(p => p.Name == name && p.Credits.Any(c => bookRoles.Contains(c.Role))).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? await db.People.Where(p => p.Name == name).FirstOrDefaultAsync(ct).ConfigureAwait(false)
            ?? db.People.Local.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (person is null)
        {
            person = new Person { Name = name };
            db.People.Add(person);
        }

        _people[name] = person;
        return person;
    }

    private readonly Dictionary<(Guid Library, string Name), BookSeries> _series = [];

    /// <summary>Puts the book in <paramref name="parts"/>' series (and only those) when there are any.</summary>
    private async Task SetSeriesAsync(MediaItem book, IReadOnlyList<SeriesPart> parts, CancellationToken ct)
    {
        if (parts.Count == 0)
        {
            return;
        }

        var entries = await db.BookSeriesEntries.Include(e => e.Series).Where(e => e.BookId == book.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var entry in entries.Where(e => !parts.Any(p => BookInfo.SameSeries(p.Name, e.Series!.Name))))
        {
            db.BookSeriesEntries.Remove(entry);
        }

        foreach (var part in parts)
        {
            var series = await SeriesAsync(book.LibraryId, part.Name, ct).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => e.SeriesId == series.Id);
            if (entry is null)
            {
                entry = new BookSeriesEntry { SeriesId = series.Id, BookId = book.Id };
                db.BookSeriesEntries.Add(entry);
            }

            entry.Sequence = part.Sequence ?? entry.Sequence;
            entry.SortOrder = SequenceOrder(entry.Sequence);
        }
    }

    private async Task<BookSeries> SeriesAsync(Guid libraryId, string name, CancellationToken ct)
    {
        var key = (libraryId, name.Trim().ToLowerInvariant());
        if (_series.TryGetValue(key, out var known))
        {
            return known;
        }

        var all = await db.BookSeries.Where(s => s.LibraryId == libraryId).ToListAsync(ct).ConfigureAwait(false);
        var series = all.Concat(db.BookSeries.Local.Where(s => s.LibraryId == libraryId)).FirstOrDefault(s => BookInfo.SameSeries(s.Name, name));
        if (series is null)
        {
            series = new BookSeries { LibraryId = libraryId, Name = name.Trim(), SortName = SortTitle.For(name) };
            db.BookSeries.Add(series);
        }

        _series[key] = series;
        return series;
    }

    /// <summary>"1.5" → 1.5, "1-3" → 1, "0" → 0; null for anything else.</summary>
    public static double? SequenceOrder(string? sequence)
    {
        if (sequence is null)
        {
            return null;
        }

        var head = sequence.Split('-')[0].Trim().Replace(',', '.');
        return double.TryParse(head, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
    }

    private async Task SetGenresAsync(MediaItem book, IReadOnlyList<string> names, CancellationToken ct)
    {
        if (names.Count == 0 || book.Genres.Select(g => g.Name).Order().SequenceEqual(names.Order(), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        book.Genres.Clear();
        foreach (var name in names.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var genre = await db.Genres.FirstOrDefaultAsync(g => g.Name == name, ct).ConfigureAwait(false)
                ?? db.Genres.Local.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase))
                ?? db.Genres.Add(new Genre { Name = name }).Entity;
            book.Genres.Add(genre);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not read the book file {Path}: {Error}")]
    private partial void LogProbeFailed(string path, string error);
}
