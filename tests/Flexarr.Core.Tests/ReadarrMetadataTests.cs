using System.Net;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>What Readarr, Bookshelf or Chaptarr know about a book fills the gaps its files leave, and never more.</summary>
public sealed class ReadarrMetadataTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-readarr-").FullName;
    private readonly Bookshelf _arr = new();
    private FlexarrDbContext _db = null!;

    private string Books => Path.Combine(_dir, "media", "audiobooks");

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
    public async Task ABookMatchedByItsFile_GetsWhatItsFilesDidntSay()
    {
        var library = await SeedAsync(("The Thursday Murder Club", "Richard Osman", "Richard Osman/The Thursday Murder Club", "Local description"));

        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(1, result.FromArr);
        var book = await _db.MediaItems.Include(i => i.Book).SingleAsync();
        Assert.Equal("Local description", book.Overview); // the files' word stands
        Assert.Equal(("12", "3", "B07QL7C4NL", "Viking"), (book.Book!.ArrBookId, book.Book.ArrAuthorId, book.Book.Asin, book.Book.Publisher));
        Assert.Equal(new DateOnly(2020, 9, 3), book.ReleaseDate);
        Assert.Equal("https://covers.example/tmc.jpg", book.PosterUrl);
        var entry = await _db.BookSeriesEntries.Include(e => e.Series).SingleAsync();
        Assert.Equal(("Thursday Murder Club", "1", 1.0), (entry.Series!.Name, entry.Sequence!, entry.SortOrder!.Value));
        var osman = await _db.People.SingleAsync();
        Assert.Equal(("Television presenter and novelist.", "https://covers.example/osman.jpg"), (osman.Biography, osman.ImageUrl));
    }

    [Fact]
    public async Task WithoutAFileMatch_TitleAndAuthorWill()
    {
        var library = await SeedAsync(("The Man Who Died Twice!", "Richard Osman", "Elsewhere/TMC2", null));

        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(1, result.FromArr);
        Assert.Equal("Elizabeth gets a letter from an old colleague.", (await _db.MediaItems.SingleAsync()).Overview);
    }

    [Fact]
    public async Task ABookTheAppDoesntHave_IsUnmatched_AndKeepsItsOwnDetails()
    {
        var library = await SeedAsync(("Project Hail Mary", "Andy Weir", "Andy Weir/Project Hail Mary", "Ryland Grace wakes up."));

        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal((0, 1), (result.FromArr, result.Unmatched));
        Assert.Equal("Ryland Grace wakes up.", (await _db.MediaItems.SingleAsync()).Overview);
    }

    [Fact]
    public async Task AnAppThatDoesntAnswer_LeavesBooksAsTheyAre()
    {
        var library = await SeedAsync(("The Thursday Murder Club", "Richard Osman", "Richard Osman/The Thursday Murder Club", null));
        _arr.Down = true;

        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal((0, 0), (result.FromArr, result.Unmatched));
        Assert.Null((await _db.MediaItems.SingleAsync()).Overview);
    }

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir,
            [new ArrInstance { Name = "Bookshelf", Kind = ArrKind.Readarr, BaseUrl = new Uri("http://bookshelf.test"), ApiKey = "k" }],
            [new PathMapping { From = "/data/audiobooks", To = Books }]);
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(_arr)), new TvMazeClient(new HttpClient(_arr)),
            new ImageCache(new HttpClient(_arr), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance);
    }

    private async Task<Library> SeedAsync(params (string Title, string Author, string Folder, string? Overview)[] books)
    {
        var library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = Books }] };
        _db.Libraries.Add(library);
        foreach (var (title, author, folder, overview) in books)
        {
            var path = Path.Combine(Books, folder);
            _db.MediaItems.Add(new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = SortTitle.For(title), Path = path, Overview = overview,
                Book = new BookDetails(),
                Files = [new MediaFile { Path = Path.Combine(path, "book.m4b"), BookOrder = 0 }],
                Credits = [new Credit { Person = new Person { Name = author }, Role = CreditRole.Author }],
            });
        }

        await _db.SaveChangesAsync();
        return library;
    }

    private sealed class Settings(string cache, IReadOnlyList<ArrInstance> instances, IReadOnlyList<PathMapping> mappings) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances => instances;
        public IReadOnlyList<PathMapping> PathMappings => mappings;
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>Bookshelf with Richard Osman's first two Thursday Murder Club books; only the first has a file.</summary>
    private sealed class Bookshelf : HttpMessageHandler
    {
        public bool Down;

        private static readonly Dictionary<string, string> Responses = new()
        {
            ["/api/v1/author"] = """
                [{"id": 3, "authorName": "Richard Osman", "foreignAuthorId": "6914939", "path": "/data/audiobooks/Richard Osman",
                  "overview": "<p>Television presenter and novelist.</p>", "images": [{"coverType": "poster", "remoteUrl": "https://covers.example/osman.jpg"}]}]
                """,
            ["/api/v1/book"] = """
                [{"id": 12, "authorId": 3, "title": "The Thursday Murder Club", "foreignBookId": "46000520", "releaseDate": "2020-09-03T00:00:00Z",
                  "overview": "Four friends meet weekly.", "genres": ["Mystery"],
                  "images": [{"coverType": "cover", "remoteUrl": "https://covers.example/tmc.jpg"}, {"coverType": "cover", "url": "/MediaCover/Books/12/cover.jpg"}],
                  "editions": [{"foreignEditionId": "54825869", "asin": "b07ql7c4nl", "publisher": "Viking", "monitored": true}]},
                 {"id": 13, "authorId": 3, "title": "The Man Who Died Twice", "foreignBookId": "57536720", "overview": "Elizabeth gets a letter from an old colleague.", "editions": []}]
                """,
            ["/api/v1/bookfile?authorId=3"] = """
                [{"id": 40, "authorId": 3, "bookId": 12, "path": "/data/audiobooks/Richard Osman/The Thursday Murder Club/book.m4b", "size": 1}]
                """,
            ["/api/v1/series?authorId=3"] = """
                [{"id": 5, "title": "Thursday Murder Club", "links": [{"position": "1", "seriesPosition": 1, "bookId": 12}, {"position": "2", "bookId": 13}]}]
                """,
        };

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Down
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(Responses.GetValueOrDefault(request.RequestUri!.PathAndQuery, "[]"), System.Text.Encoding.UTF8, "application/json"),
                });
    }
}
