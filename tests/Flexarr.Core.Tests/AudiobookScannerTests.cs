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
            return Task.FromResult(new BookFileProbe(Length, TagsFor(path), chapters, false));
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
