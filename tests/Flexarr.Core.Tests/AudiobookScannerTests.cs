using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>Audiobook libraries: files grouped into books, put in order, and described from their path, tags and sidecars.</summary>
public sealed class AudiobookScannerTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-books-").FullName;
    private readonly BookProbe _probe = new();
    private FlexarrDbContext _db = null!;

    private string Books => Path.Combine(_dir, "books");

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task AFolderOfFilesIsOneBook_InPlayingOrder()
    {
        Touch("Alan Watts/Out of Your Mind/10-01 Introduction.mp3",
            "Alan Watts/Out of Your Mind/1-02 Our Image Of The World.mp3",
            "Alan Watts/Out of Your Mind/1-01 Introduction.mp3",
            "Alan Watts/Out of Your Mind/2-01 Introduction.mp3",
            "Alan Watts/Out of Your Mind/Folder.jpg");

        var result = await ScanAsync();

        Assert.Equal(4, result.Added);
        var book = await _db.MediaItems.Include(i => i.Files).Include(i => i.Book).SingleAsync();
        Assert.Equal(MediaKind.Audiobook, book.Kind);
        Assert.Equal("Out of Your Mind", book.Title);
        Assert.Equal(["1-01 Introduction.mp3", "1-02 Our Image Of The World.mp3", "2-01 Introduction.mp3", "10-01 Introduction.mp3"],
            book.Files.OrderBy(f => f.BookOrder).Select(f => Path.GetFileName(f.Path)));
        Assert.Equal(4 * BookProbe.Length, book.Book!.DurationSeconds);
        Assert.Equal(["Alan Watts"], await AuthorsAsync(book.Id));
    }

    [Fact]
    public async Task DiscFoldersAndTrackTags_PutFilesInOrder()
    {
        Touch("A/Book/CD2/01.mp3", "A/Book/CD1/02.mp3", "A/Book/CD1/01.mp3");
        _probe.Tags["CD1/01.mp3"] = new() { ["track"] = "2" };
        _probe.Tags["CD1/02.mp3"] = new() { ["track"] = "1/9" };

        await ScanAsync();

        var files = await _db.MediaFiles.OrderBy(f => f.BookOrder).Select(f => f.Path).ToListAsync();
        Assert.Equal(["CD1/02.mp3", "CD1/01.mp3", "CD2/01.mp3"], files.Select(f => Path.GetRelativePath(Path.Combine(Books, "A/Book"), f)));
    }

    [Fact]
    public async Task SingleFileBooksSideBySide_AreBooksOfTheirOwn()
    {
        Touch("Kurt Vonnegut/Cat's Cradle.m4b", "Kurt Vonnegut/Timequake.m4b", "Andy Weir - Project Hail Mary.m4b");

        await ScanAsync();

        var books = await _db.MediaItems.OrderBy(i => i.SortTitle).Select(i => i.Title).ToListAsync();
        Assert.Equal(["Cat's Cradle", "Project Hail Mary", "Timequake"], books);
    }

    [Fact]
    public async Task SeriesFromThePath_WithItsNumber()
    {
        Touch("Richard Osman/Thursday Murder Club/1 - The Thursday Murder Club/a.m4b",
            "Richard Osman/Thursday Murder Club/2 - The Man Who Died Twice/a.m4b");

        await ScanAsync();

        var series = await _db.BookSeries.Include(s => s.Entries).ThenInclude(e => e.Book).SingleAsync();
        Assert.Equal("Thursday Murder Club", series.Name);
        Assert.Equal([("The Thursday Murder Club", "1"), ("The Man Who Died Twice", "2")],
            series.Entries.OrderBy(e => e.SortOrder).Select(e => (e.Book!.Title, e.Sequence!)));
    }

    [Fact]
    public async Task AnAuthorIsOnePersonAcrossBooks()
    {
        Touch("Kurt Vonnegut/Cat's Cradle/a.mp3", "Kurt Vonnegut/Timequake/a.mp3");

        await ScanAsync();

        Assert.Equal(1, await _db.People.CountAsync());
        Assert.Equal(2, await _db.Credits.CountAsync(c => c.Role == CreditRole.Author));
    }

    [Fact]
    public async Task ARescanOfUnchangedFiles_LeavesBooksAlone()
    {
        Touch("A/Book/01.mp3", "A/Book/02.mp3");
        await ScanAsync();
        var calls = _probe.BookCalls;

        var result = await ScanAsync();

        Assert.Equal(2, result.Unchanged);
        Assert.Equal(calls, _probe.BookCalls);
    }

    [Fact]
    public async Task AFileAddedLater_JoinsItsBook()
    {
        Touch("A/Book/01.mp3");
        await ScanAsync();
        Touch("A/Book/02.mp3");

        await ScanAsync();

        var book = await _db.MediaItems.Include(i => i.Files).Include(i => i.Book).SingleAsync();
        Assert.Equal([0, 1], book.Files.OrderBy(f => f.Path, StringComparer.Ordinal).Select(f => f.BookOrder ?? -1));
        Assert.Equal(2 * BookProbe.Length, book.Book!.DurationSeconds);
    }

    [Fact]
    public async Task TagsDescribeTheBook_OverItsPath()
    {
        Touch("Unsorted/rip 4/Richard Osman - TMC1 The Thursday Murder Club.m4b");
        _probe.Tags["Richard Osman - TMC1 The Thursday Murder Club.m4b"] = new()
        {
            ["album"] = "The Thursday Murder Club (Unabridged)", ["title"] = "Chapter 1", ["artist"] = "Richard Osman", ["composer"] = "Lesley Manville",
            ["mvnm"] = "Thursday Murder Club", ["mvin"] = "1", ["asin"] = "b07ql7c4nl", ["date"] = "2020-09-03", ["publisher"] = "Penguin Audio",
            ["genre"] = "Mystery; Crime",
        };

        await ScanAsync();

        var book = await _db.MediaItems.Include(i => i.Book).Include(i => i.Genres).SingleAsync();
        Assert.Equal("The Thursday Murder Club", book.Title);
        Assert.Equal(2020, book.Year);
        Assert.Equal(new DateOnly(2020, 9, 3), book.ReleaseDate);
        Assert.Equal("B07QL7C4NL", book.Book!.Asin);
        Assert.Equal("Penguin Audio", book.Book.Publisher);
        Assert.Equal(["Crime", "Mystery"], book.Genres.Select(g => g.Name).Order());
        Assert.Equal(["Richard Osman"], await AuthorsAsync(book.Id));
        Assert.Equal(["Lesley Manville"], await NarratorsAsync(book.Id));
        var entry = await _db.BookSeriesEntries.Include(e => e.Series).SingleAsync();
        Assert.Equal(("Thursday Murder Club", "1"), (entry.Series!.Name, entry.Sequence!));
    }

    [Fact]
    public async Task InABookOfManyFiles_TheTitleTagIsAChapter_NotTheBook()
    {
        Touch("Alan Watts/Out of Your Mind/1-01 Introduction.mp3", "Alan Watts/Out of Your Mind/1-02 Our Image.mp3");
        _probe.Tags["1-01 Introduction.mp3"] = new() { ["title"] = "Introduction", ["artist"] = "Alan Watts" };

        await ScanAsync();

        Assert.Equal("Out of Your Mind", (await _db.MediaItems.SingleAsync()).Title);
    }

    [Fact]
    public async Task AnOpfFile_WinsOverTags()
    {
        Touch("Terry Pratchett/Going Postal/01.mp3", "Terry Pratchett/Going Postal/02.mp3");
        _probe.Tags["01.mp3"] = new() { ["album"] = "going postal (tagged)", ["artist"] = "Pratchett" };
        File.WriteAllText(Path.Combine(Books, "Terry Pratchett/Going Postal/metadata.opf"), """
            <?xml version="1.0" encoding="utf-8"?>
            <package xmlns="http://www.idpf.org/2007/opf" version="2.0">
              <metadata xmlns:dc="http://purl.org/dc/elements/1.1/" xmlns:opf="http://www.idpf.org/2007/opf">
                <dc:title>Going Postal</dc:title>
                <dc:creator opf:role="aut">Terry Pratchett</dc:creator>
                <dc:creator opf:role="nrt">Stephen Briggs</dc:creator>
                <dc:description>&lt;p&gt;Moist von Lipwig was a con artist.&lt;/p&gt;&lt;p&gt;Then he ran the post office.&lt;/p&gt;</dc:description>
                <dc:publisher>Isis</dc:publisher>
                <dc:date>2004-09-25T00:00:00+00:00</dc:date>
                <dc:language>eng</dc:language>
                <dc:identifier opf:scheme="ISBN">9780753122624</dc:identifier>
                <dc:identifier opf:scheme="ASIN">B002V0QK4C</dc:identifier>
                <dc:subject>Fantasy</dc:subject>
                <meta name="calibre:series" content="Discworld"/>
                <meta name="calibre:series_index" content="33.0"/>
              </metadata>
            </package>
            """);

        await ScanAsync();

        var book = await _db.MediaItems.Include(i => i.Book).SingleAsync();
        Assert.Equal("Going Postal", book.Title);
        Assert.Equal("Moist von Lipwig was a con artist.\n\nThen he ran the post office.", book.Overview);
        Assert.Equal(("9780753122624", "B002V0QK4C", "eng"), (book.Book!.Isbn, book.Book.Asin, book.Book.Language));
        Assert.Equal(["Terry Pratchett"], await AuthorsAsync(book.Id));
        Assert.Equal(["Stephen Briggs"], await NarratorsAsync(book.Id));
        Assert.Equal("33", (await _db.BookSeriesEntries.SingleAsync()).Sequence);
    }

    [Fact]
    public async Task AudiobookshelfMetadataJson_DescTxtAndReaderTxt()
    {
        Touch("Andy Weir/Project Hail Mary/a.m4b");
        var folder = Path.Combine(Books, "Andy Weir/Project Hail Mary");
        File.WriteAllText(Path.Combine(folder, "metadata.json"), """
            {"title":"Project Hail Mary","subtitle":"A Novel","authors":["Andy Weir"],"narrators":[],"series":["Hail Mary #1"],
             "genres":["Science Fiction"],"publishedYear":"2021","publisher":"Audible Studios","description":null,"asin":"B08G9PRS1K",
             "language":"English","explicit":false,"abridged":false}
            """);
        File.WriteAllText(Path.Combine(folder, "desc.txt"), "Ryland Grace is the sole survivor.\n");
        File.WriteAllText(Path.Combine(folder, "reader.txt"), "Ray Porter\n");

        await ScanAsync();

        var book = await _db.MediaItems.Include(i => i.Book).SingleAsync();
        Assert.Equal(("Project Hail Mary", "A Novel", 2021), (book.Title, book.Book!.Subtitle, book.Year));
        Assert.Equal("Ryland Grace is the sole survivor.", book.Overview);
        Assert.Equal(["Ray Porter"], await NarratorsAsync(book.Id));
        Assert.Equal("1", (await _db.BookSeriesEntries.SingleAsync()).Sequence);
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("Folder.jpg")]
    [InlineData("Can't Hurt Me by David Goggins - Cover.jpg")]
    [InlineData("anything.png")]
    public async Task ACoverImageInTheFolder(string name)
    {
        Touch("A/Book/01.mp3", $"A/Book/{name}");

        await ScanAsync();

        Assert.Equal($"file://{Path.Combine(Books, "A/Book", name)}", (await _db.MediaItems.SingleAsync()).PosterUrl);
    }

    [Fact]
    public async Task EmbeddedArtwork_WhenTheFolderHasNoCover()
    {
        Touch("A/Book/01.m4b");
        _probe.CoverArt = true;

        await ScanAsync();

        Assert.Equal($"embedded://{Path.Combine(Books, "A/Book/01.m4b")}", (await _db.MediaItems.SingleAsync()).PosterUrl);
    }

    private async Task<List<string>> NarratorsAsync(Guid bookId) =>
        await _db.Credits.Where(c => c.MediaItemId == bookId && c.Role == CreditRole.Narrator).OrderBy(c => c.Order).Select(c => c.Person!.Name).ToListAsync();

    private async Task<List<string>> AuthorsAsync(Guid bookId) =>
        await _db.Credits.Where(c => c.MediaItemId == bookId && c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name).ToListAsync();

    private async Task<ScanResult> ScanAsync()
    {
        var library = await _db.Libraries.Include(l => l.Folders).FirstOrDefaultAsync();
        if (library is null)
        {
            library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = Books }] };
            _db.Libraries.Add(library);
            await _db.SaveChangesAsync();
        }

        return await new LibraryScanner(_db, _probe, new ScanProgress(), NullLogger<LibraryScanner>.Instance).ScanAsync(library, null, default);
    }

    private void Touch(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(Books, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }
    }

    /// <summary>A probe that reports every file as <see cref="Length"/> seconds of sound, with tags and chapters set by test.</summary>
    private sealed class BookProbe : IMediaProbe
    {
        public const double Length = 600;
        public int BookCalls;
        public bool CoverArt;

        /// <summary>Tags by the end of the file's path ("CD1/01.mp3" or "01.mp3").</summary>
        public Dictionary<string, Dictionary<string, string>> Tags { get; } = [];

        /// <summary>Chapters by the end of the file's path.</summary>
        public Dictionary<string, List<ProbedChapter>> Chapters { get; } = [];

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new ProbeResult("mp3", Length, 64_000, VideoRange.Sdr,
                [new MediaStream { Index = 0, Type = StreamType.Audio, Codec = "mp3", Channels = 2, IsDefault = true }])
            { Tags = TagsFor(path) });

        public Task<BookFileProbe> ProbeBookFileAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref BookCalls);
            var chapters = Chapters.FirstOrDefault(c => path.EndsWith("/" + c.Key, StringComparison.Ordinal)).Value ?? [];
            return Task.FromResult(new BookFileProbe(Length, TagsFor(path), chapters, CoverArt));
        }

        private Dictionary<string, string> TagsFor(string path)
        {
            var match = Tags.Where(t => path.EndsWith("/" + t.Key, StringComparison.Ordinal)).OrderByDescending(t => t.Key.Length).FirstOrDefault().Value;
            return match is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(match, StringComparer.OrdinalIgnoreCase);
        }
    }
}
