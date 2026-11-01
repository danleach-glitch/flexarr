using System.Net;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>Books looked up online (Audible through Audnexus, Audible's catalogue, Open Library, Google Books), from recorded answers.</summary>
public sealed class BookProviderTests : IAsyncLifetime
{
    private const string Tmc = "0241988292";
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-bookmeta-").FullName;
    private readonly Recorded _web = new();
    private FlexarrDbContext _db = null!;
    private BookMetadataSettings _books = new("uk", ["audible", "openlibrary"]);

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task ByAsin_AudibleFillsTheGaps_WithItsChaptersAndTheAuthor()
    {
        var library = await SeedAsync("The Thursday Murder Club", "Richard Osman", asin: Tmc, duration: 44718, chapters: ChapterSource.Even);

        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(1, result.FromLookup);
        var book = await _db.MediaItems.Include(i => i.Book).Include(i => i.Genres).SingleAsync();
        Assert.StartsWith("THE FIRST NOVEL", book.Overview, StringComparison.Ordinal);
        Assert.Equal(("Penguin", "English", "9780241988299"), (book.Book!.Publisher, book.Book.Language, book.Book.Isbn));
        Assert.Equal("https://m.media-amazon.com/images/I/71BSZeMNc-L.jpg", book.PosterUrl);
        Assert.Contains(book.Genres, g => g.Name == "Mystery, Thriller & Suspense");
        Assert.Equal(["Lesley Manville", "Richard Osman", "Marian Keyes"], await NamesAsync(book.Id, CreditRole.Narrator));
        Assert.Equal(("The Thursday Murder Club", "1"), await _db.BookSeriesEntries.Select(e => ValueTuple.Create(e.Series!.Name, e.Sequence!)).SingleAsync());
        var osman = await _db.People.SingleAsync(p => p.Name == "Richard Osman");
        Assert.StartsWith("Richard Osman is an author", osman.Biography, StringComparison.Ordinal);
        Assert.Equal("B074KL81KM", osman.Asin);
        Assert.Equal(ChapterSource.Audible, book.Book.ChapterSource);
        var chapters = await _db.MediaChapters.OrderBy(c => c.Index).Select(c => c.Title).Take(2).ToListAsync();
        Assert.Equal(["Opening Credits", "Prologue"], chapters);
    }

    [Fact]
    public async Task AudiblesChapters_AreLeftAlone_WhenTheLengthDisagrees()
    {
        var library = await SeedAsync("The Thursday Murder Club", "Richard Osman", asin: Tmc, duration: 30000, chapters: ChapterSource.Even);

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(ChapterSource.Even, (await _db.BookDetails.SingleAsync()).ChapterSource);
    }

    [Fact]
    public async Task WithoutIds_TheCatalogueIsSearchedByTitleAndAuthor()
    {
        var library = await SeedAsync("The Thursday Murder Club", "Richard Osman");

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var book = await _db.BookDetails.SingleAsync();
        Assert.Equal(Tmc, book.Asin);
        Assert.Equal(["Lesley Manville", "Richard Osman", "Marian Keyes"], await NamesAsync(book.MediaItemId, CreditRole.Narrator));
    }

    [Fact]
    public async Task OpenLibrary_ByIsbn()
    {
        _books = new("us", ["openlibrary"]);
        var library = await SeedAsync("Project Hail Mary", "Andy Weir", isbn: "9780593135204");

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var book = await _db.MediaItems.Include(i => i.Book).SingleAsync();
        Assert.Equal(("Ballantine Books", new DateOnly(2021, 5, 4)), (book.Book!.Publisher, book.ReleaseDate!.Value));
        Assert.Equal("https://covers.openlibrary.org/b/id/12455001-L.jpg", book.PosterUrl);
    }

    [Fact]
    public async Task GoogleBooks_NeedsAKey_AndReadsItsVolumes()
    {
        _books = new("us", ["google"]);
        var providers = new BookProviders(new HttpClient(_web), () => new Settings(_dir, _books));

        Assert.Empty(await providers.SearchAsync("Project Hail Mary", "Weir", default));

        _books = new("us", ["google"], GoogleBooksKey: "key");
        var found = Assert.Single(await providers.SearchAsync("Project Hail Mary", "Weir", default));
        Assert.Equal(("Project Hail Mary", "A Novel", "9780593135204"), (found.Title, found.Subtitle, found.Isbn));
        Assert.Equal("Ryland Grace is the sole survivor on a desperate, last-chance mission.", found.Description);
    }

    [Fact]
    public async Task AProviderThatsDown_ChangesNothing_AndIsAskedAgainNextTime()
    {
        var library = await SeedAsync("The Thursday Murder Club", "Richard Osman", asin: Tmc);
        _web.Down = true;

        var down = await Refresher().RefreshLibraryAsync(library, force: true, default);
        _web.Down = false;
        var back = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(0, down.FromLookup);
        Assert.Equal(1, back.FromLookup);
    }

    [Fact]
    public async Task Answers_AreKept_SoTheNextRefreshDoesntAskAgain()
    {
        var library = await SeedAsync("The Thursday Murder Club", "Richard Osman", asin: Tmc);
        await Refresher().RefreshLibraryAsync(library, force: true, default);
        var asked = _web.Requests.Count;

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(asked, _web.Requests.Count);
    }

    [Fact]
    public async Task FindingABook_StopsAtTheFirstProviderThatHasIt()
    {
        var providers = new BookProviders(new HttpClient(_web), () => new Settings(_dir, _books), new BookProviderThrottle());

        var found = await providers.FindAsync("The Thursday Murder Club", ["Richard Osman"], default);

        Assert.Equal("audible", found?.Provider);
        Assert.DoesNotContain(_web.Requests, r => r.Contains("openlibrary.org", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProviderThatSaysTooMany_IsLeftAloneForAWhile()
    {
        _web.TooMany = "api.audible.co.uk";
        var providers = new BookProviders(new HttpClient(_web), () => new Settings(_dir, _books), new BookProviderThrottle());

        var first = await providers.SearchAsync("The Thursday Murder Club", "Richard Osman", default);
        var asked = _web.Requests.Count(r => r.Contains("api.audible.co.uk", StringComparison.Ordinal));
        var again = await providers.SearchAsync("The Thursday Murder Club", "Osman", default);

        Assert.Equal(1, asked);
        Assert.Equal(asked, _web.Requests.Count(r => r.Contains("api.audible.co.uk", StringComparison.Ordinal)));
        Assert.DoesNotContain(first.Concat(again), m => m.Provider == "audible");
        Assert.Contains(again, m => m.Provider == "openlibrary"); // the others are still asked
    }

    private async Task<List<string>> NamesAsync(Guid book, CreditRole role) =>
        await _db.Credits.Where(c => c.MediaItemId == book && c.Role == role).OrderBy(c => c.Order).Select(c => c.Person!.Name).ToListAsync();

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir, _books);
        var http = new HttpClient(_web);
        return new MetadataRefresher(_db, new ArrClient(http), new TvMazeClient(http), new ImageCache(http, settings, NullLogger<ImageCache>.Instance),
            settings, NullLogger<MetadataRefresher>.Instance, bookProviders: new BookProviders(http, () => settings));
    }

    private async Task<Library> SeedAsync(string title, string author, string? asin = null, string? isbn = null, double duration = 3600, ChapterSource chapters = ChapterSource.Files)
    {
        var library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks };
        _db.Libraries.Add(library);
        _db.MediaItems.Add(new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = SortTitle.For(title), Path = $"/books/{title}",
            Book = new BookDetails { Asin = asin, Isbn = isbn, DurationSeconds = duration, ChapterSource = chapters },
            Credits = [new Credit { Person = new Person { Name = author }, Role = CreditRole.Author }],
        });
        await _db.SaveChangesAsync();
        return library;
    }

    private sealed class Settings(string cache, BookMetadataSettings books) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = [];
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
        public BookMetadataSettings Books => books;
    }

    /// <summary>The providers, answering with what they said when recorded; anything else is a 404.</summary>
    private sealed class Recorded : HttpMessageHandler
    {
        public bool Down;

        /// <summary>A host that answers 429, asking to be left alone for two minutes.</summary>
        public string? TooMany;
        public List<string> Requests { get; } = [];

        private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Books", name));

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            Requests.Add(url.ToString());
            if (Down)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
            }

            if (url.Host == TooMany)
            {
                var tooMany = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                tooMany.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(2));
                return Task.FromResult(tooMany);
            }

            var body = (url.Host, url.AbsolutePath) switch
            {
                ("api.audnex.us", $"/books/{Tmc}") => Fixture("audnexus-book.json"),
                ("api.audnex.us", $"/books/{Tmc}/chapters") => Fixture("audnexus-chapters.json"),
                ("api.audnex.us", "/authors/B074KL81KM") => Fixture("audnexus-author.json"),
                ("api.audible.co.uk", "/1.0/catalog/products") => Fixture("audible-search.json"),
                ("openlibrary.org", "/isbn/9780593135204.json") => Fixture("openlibrary-isbn.json"),
                ("openlibrary.org", "/search.json") => Fixture("openlibrary-search.json"),
                ("www.googleapis.com", "/books/v1/volumes") when url.Query.Contains("key=key", StringComparison.Ordinal) => Fixture("google-search.json"),
                _ => null,
            };
            return Task.FromResult(body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
