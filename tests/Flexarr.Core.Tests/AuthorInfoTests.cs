using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Flexarr.Metadata.Books;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>Every author's photo and biography, from their folder, Readarr, Audible and Open Library, as a book refresh finds them.</summary>
public sealed class AuthorInfoTests : IAsyncLifetime
{
    private const string Osman = "Richard Osman", Keyes = "Marian Keyes", Title = "The Thursday Murder Club";
    private const string AudiblePhoto = "https://m.media-amazon.com/images/I/osman.jpg";
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-authorinfo-").FullName;
    private readonly AuthorProviderTests.Canned _web = new();
    private FlexarrDbContext _db = null!;
    private bool _readarr;

    private string Books => Path.Combine(_dir, "media", "audiobooks");

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
        _web.Answers[("api.audnex.us", "/authors")] = """[{"asin": "B074KL81KM", "name": "Richard Osman"}]""";
        _web.Answers[("api.audnex.us", "/authors/B074KL81KM")] = $$"""{"asin": "B074KL81KM", "name": "Richard Osman", "description": "From Audible.", "image": "{{AudiblePhoto}}"}""";
        _web.Answers[("openlibrary.org", "/search/authors.json")] = $$"""{"docs": [{"key": "OL5A", "name": "Marian Keyes", "top_work": "{{Title}}"}]}""";
        _web.Answers[("openlibrary.org", "/authors/OL5A.json")] = """{"name": "Marian Keyes", "bio": "From Open Library."}""";
        // Readarr knows Richard Osman, with a biography but no photo, and none of his books.
        _web.Answers[("readarr.test", "/api/v1/author")] = """[{"id": 3, "authorName": "Richard Osman", "overview": "<p>From Readarr.</p>", "images": []}]""";
        foreach (var path in new[] { "/api/v1/book", "/api/v1/bookfile", "/api/v1/series" })
        {
            _web.Answers[("readarr.test", path)] = "[]";
        }
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Readarr_ThenAudible_EachFillingOnlyWhatsMissing()
    {
        _readarr = true;
        var library = await SeedAsync(new Person { Name = Osman });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal(("From Readarr.", "readarr"), (osman.Biography, osman.BiographySource));
        Assert.Equal((AudiblePhoto, "audible", "B074KL81KM"), (osman.ImageUrl, osman.PhotoSource, osman.Asin));
        Assert.NotNull(osman.ImageChangedAt);
        Assert.NotNull(osman.InfoCheckedAt);
        Assert.DoesNotContain(_web.Requests, r => r.Contains("/search/authors.json", StringComparison.Ordinal)); // nothing left for Open Library
    }

    [Fact]
    public async Task EveryAuthor_IsLookedUp_NotJustTheFirst()
    {
        var library = await SeedAsync(new Person { Name = Osman }, new Person { Name = Keyes });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(("From Audible.", "audible"), ((await PersonAsync(Osman)).Biography, (await PersonAsync(Osman)).BiographySource));
        var keyes = await PersonAsync(Keyes);
        Assert.Equal(("From Open Library.", "openlibrary", null), (keyes.Biography, keyes.BiographySource, keyes.ImageUrl));
        Assert.NotNull(keyes.InfoCheckedAt);
    }

    [Fact]
    public async Task APhotoInTheAuthorsFolder_WinsOverAnOnlineOne()
    {
        var photo = await AuthorPhotoAsync("author.JPG");
        var library = await SeedAsync(new Person { Name = Osman, ImageUrl = AudiblePhoto, PhotoSource = PersonSources.Audible, Biography = "Known." });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal(($"file://{photo}", "folder"), (osman.ImageUrl, osman.PhotoSource));
        Assert.NotNull(osman.ImageChangedAt);
    }

    [Fact]
    public async Task AFolderPhoto_IsntTakenForAFolderOfSeveralAuthors_OrOverAnUpload()
    {
        var shared = Path.Combine(Books, $"{Osman}, {Keyes}");
        Directory.CreateDirectory(shared);
        await File.WriteAllBytesAsync(Path.Combine(shared, "folder.jpg"), [0xFF, 0xD8, 0xFF]);
        await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(Path.Combine(shared, Title), new Person { Name = Keyes });
        await SeedAsync(Path.Combine(Books, Osman, "Another Book"), new Person { Name = Osman, ImageUrl = "file:///data/person-images/osman.jpg", PhotoSource = PersonSources.Upload, Biography = "Known." }, library);

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Null((await PersonAsync(Keyes)).ImageUrl);
        Assert.Equal(("file:///data/person-images/osman.jpg", "upload"), ((await PersonAsync(Osman)).ImageUrl, (await PersonAsync(Osman)).PhotoSource));
    }

    [Fact]
    public async Task ALockedAuthor_IsLeftAlone()
    {
        await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(new Person { Name = Osman, InfoLocked = true });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal((null, null, null), (osman.ImageUrl, osman.Biography, osman.InfoCheckedAt));
        Assert.DoesNotContain(_web.Requests, r => r.Contains("audnex.us/authors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAuthorLookedUpThisMonth_IsntLookedUpAgain_UntilAMonthOn()
    {
        var library = await SeedAsync(new Person { Name = Osman, InfoCheckedAt = DateTime.UtcNow.AddDays(-5) });

        await Refresher().RefreshLibraryAsync(library, force: true, default);
        var asked = _web.Requests.Count(r => r.Contains("audnex.us/authors", StringComparison.Ordinal));
        await _db.People.ExecuteUpdateAsync(u => u.SetProperty(p => p.InfoCheckedAt, DateTime.UtcNow.AddDays(-31)));
        _db.ChangeTracker.Clear();
        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal(0, asked);
        Assert.Equal("From Audible.", (await PersonAsync(Osman)).Biography);
    }

    [Fact]
    public async Task AProviderThatsDown_LeavesTheAuthorToBeTriedAgain()
    {
        _web.DownHosts.Add("api.audnex.us");
        var library = await SeedAsync(new Person { Name = Osman });

        await Refresher().RefreshLibraryAsync(library, force: true, default);
        Assert.Null((await PersonAsync(Osman)).InfoCheckedAt);

        _web.DownHosts.Clear();
        _db.ChangeTracker.Clear();
        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal(("From Audible.", AudiblePhoto), (osman.Biography, osman.ImageUrl));
        Assert.NotNull(osman.InfoCheckedAt);
    }

    [Fact]
    public async Task ReadarrsAuthorOfAnotherName_GoesOnlyToABooksOnlyAuthor()
    {
        var sir = new Integrations.Arr.Models.ReadarrAuthor { AuthorName = "Sir Richard Osman", Overview = "From Readarr." };
        await SeedAsync(new Person { Name = Osman }, new Person { Name = Keyes });
        await SeedAsync(Path.Combine(Books, Osman, "Alone"), new Person { Name = "Solo Writer" }, await _db.Libraries.SingleAsync());

        foreach (var book in await _db.MediaItems.Include(i => i.Credits).ThenInclude(c => c.Person).ToListAsync())
        {
            await Authors().FillBookAsync(book, [], sir, [], default);
        }

        await _db.SaveChangesAsync();
        Assert.Null((await PersonAsync(Osman)).Biography); // two authors: no telling whose Readarr's is
        Assert.Null((await PersonAsync(Keyes)).Biography);
        Assert.Equal("From Readarr.", (await PersonAsync("Solo Writer")).Biography);
    }

    [Fact]
    public async Task AFolderPhotoReplacedWithAnOlderOne_GetsANewVersion()
    {
        var photo = await AuthorPhotoAsync("author.jpg");
        File.SetLastWriteTimeUtc(photo, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });
        await Refresher().RefreshLibraryAsync(library, force: true, default);
        var first = (await PersonAsync(Osman)).ImageChangedAt;
        await _db.People.ExecuteUpdateAsync(u => u.SetProperty(p => p.ImageChangedAt, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
        _db.ChangeTracker.Clear();

        // The same again: no new version.
        await Refresher().RefreshLibraryAsync(library, force: true, default);
        var unchanged = (await PersonAsync(Osman)).ImageChangedAt;
        _db.ChangeTracker.Clear();

        // Replaced by a copy dated earlier still (cp -p): a new version all the same.
        await File.WriteAllBytesAsync(photo, [0xFF, 0xD8, 0xFF, 0xE0, 0x00]);
        File.SetLastWriteTimeUtc(photo, new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.NotNull(first);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), unchanged);
        Assert.True((await PersonAsync(Osman)).ImageChangedAt > unchanged);
    }

    [Fact]
    public async Task AFolderPhotoFromBeforeVersions_GetsOne()
    {
        var photo = await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", ImageUrl = $"file://{photo}", PhotoSource = PersonSources.Folder, InfoCheckedAt = DateTime.UtcNow });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.NotNull(osman.ImageChangedAt);
        Assert.NotNull(osman.ImageStamp);
    }

    [Fact]
    public async Task AFolderPhoto_StaysWhileItsShareIsOffline()
    {
        var photo = await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });
        await Refresher().RefreshLibraryAsync(library, force: true, default);
        var before = await PersonAsync(Osman);
        _db.ChangeTracker.Clear();

        // The share goes away (unmounted): its folder is missing altogether.
        var offline = Books + "-offline";
        Directory.Move(Books, offline);
        try
        {
            await Refresher().RefreshLibraryAsync(library, force: true, default);
        }
        finally
        {
            Directory.Move(offline, Books);
        }

        var after = await PersonAsync(Osman);
        Assert.Equal(($"file://{photo}", "folder"), (after.ImageUrl, after.PhotoSource));
        Assert.Equal(before.ImageChangedAt, after.ImageChangedAt);
    }

    [Fact]
    public async Task ARemovedFolderPhoto_IsFilledOnlineStraightAway()
    {
        var photo = await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });
        await Refresher().RefreshLibraryAsync(library, force: true, default);
        _db.ChangeTracker.Clear();

        File.Delete(photo);
        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal((AudiblePhoto, "audible"), (osman.ImageUrl, osman.PhotoSource)); // not a month on
    }

    [Theory]
    [InlineData(true, "author.jpg", true, true)]
    [InlineData(true, null, false, true)]
    [InlineData(false, null, false, false)]
    public void TheFolderCheck_TellsGoneFromUnreadable(bool readable, string? file, bool found, bool listed)
    {
        var (photo, wasListed) = AuthorInfo.FolderPhoto(Osman, "/books", $"/books/{Osman}/{Title}",
            folder => readable ? file is null ? [] : [Path.Combine(folder, file)] : null);

        Assert.Equal((found, listed), (photo is not null, wasListed));
    }

    [Fact]
    public async Task ALinkedAuthorPhoto_IsntTaken()
    {
        var elsewhere = Path.Combine(_dir, "elsewhere.jpg");
        await File.WriteAllBytesAsync(elsewhere, [0xFF, 0xD8, 0xFF]);
        var folder = Directory.CreateDirectory(Path.Combine(Books, Osman)).FullName;
        File.CreateSymbolicLink(Path.Combine(folder, "author.jpg"), elsewhere);
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Null((await PersonAsync(Osman)).ImageUrl);
    }

    [Fact]
    public async Task AnOldHttpPhoto_CountsAsMissing()
    {
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", ImageUrl = "http://images.example/osman.jpg", InfoCheckedAt = DateTime.UtcNow });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Equal((AudiblePhoto, "audible"), ((await PersonAsync(Osman)).ImageUrl, (await PersonAsync(Osman)).PhotoSource));
    }

    [Fact]
    public void ReadarrsPlaceholder_IsntAPhoto()
    {
        Assert.Null(AuthorInfo.ArrPhoto([new ArrImage("poster", "https://s.gr-assets.com/assets/nophoto/user/u_200x266-e183445fd1a1b5cc7075bb1cf7043306.png", null)]));
        Assert.Equal("https://images.gr-assets.com/authors/1.jpg",
            AuthorInfo.ArrPhoto([new ArrImage("poster", "https://s.gr-assets.com/assets/nophoto/author.png", null), new ArrImage("poster", "https://images.gr-assets.com/authors/1.jpg", null)]));
    }

    [Theory]
    [InlineData("https://m.media-amazon.com/images/I/osman.jpg", "audible", true)]
    [InlineData("https://images-na.ssl-images-amazon.com/images/I/osman.jpg", "audible", true)]
    [InlineData("https://192.168.1.1/x.jpg", "audible", false)]
    [InlineData("https://images.example/x.jpg", "audible", false)]
    [InlineData("https://covers.openlibrary.org/a/olid/OL1A-L.jpg", "openlibrary", true)]
    [InlineData("https://openlibrary.org.evil.example/x.jpg", "openlibrary", false)]
    [InlineData("https://images.gr-assets.com/authors/1.jpg", "readarr", true)]
    [InlineData("https://192.168.1.1/x.jpg", "readarr", false)]
    [InlineData("https://[::1]/x.jpg", "readarr", false)]
    [InlineData("https://router/x.jpg", "readarr", false)]
    [InlineData("https://nas.local/x.jpg", "readarr", false)]
    [InlineData("https://images.example:8443/x.jpg", "readarr", false)]
    [InlineData("http://images.example/x.jpg", "readarr", false)]
    public void AProvidersPhoto_IsOnlyTakenFromWhereItShouldBe(string url, string source, bool taken) =>
        Assert.Equal(taken, AuthorInfo.OnlinePhoto(url, source) is not null);

    [Fact]
    public async Task AnAudiblePhotoOnTheHomeNetwork_IsntTaken()
    {
        _web.Answers[("api.audnex.us", "/authors/B074KL81KM")] = """{"asin": "B074KL81KM", "name": "Richard Osman", "description": "From Audible.", "image": "https://192.168.1.1/admin.jpg"}""";
        var library = await SeedAsync(new Person { Name = Osman });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var osman = await PersonAsync(Osman);
        Assert.Equal(("From Audible.", null), (osman.Biography, osman.ImageUrl));
    }

    [Fact]
    public async Task AProvidersBiography_IsCutShort()
    {
        var huge = string.Join(' ', Enumerable.Repeat("word", 10_000));
        _web.Answers[("api.audnex.us", "/authors/B074KL81KM")] = $$"""{"asin": "B074KL81KM", "name": "Richard Osman", "description": "{{huge}}"}""";
        var library = await SeedAsync(new Person { Name = Osman });

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var biography = (await PersonAsync(Osman)).Biography!;
        Assert.True(biography.Length <= PersonSources.MaxBiography, $"{biography.Length}");
        Assert.EndsWith("word…", biography, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Candidates_LeaveThePersonAsTheyWere()
    {
        await SeedAsync(new Person { Name = Osman });
        var osman = await _db.People.SingleAsync(p => p.Name == Osman);

        var (photos, _) = await Authors(new BookProviders(new HttpClient(_web), () => new Settings(_dir, []), new BookProviderThrottle())).CandidatesAsync(osman, default);

        Assert.Contains(photos, p => p.Source == "audible");
        Assert.Null(osman.Asin); // found by name, for showing only
        Assert.False(_db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task SomeoneFromFilms_KeepsTheirPhoto_AndGetsNoBooksBiography()
    {
        const string Tmdb = "https://image.tmdb.org/t/p/original/osman.jpg";
        await AuthorPhotoAsync("author.jpg");
        var osman = new Person { Name = Osman, TmdbId = "123", ImageUrl = Tmdb, PhotoSource = PersonSources.Tmdb };
        var library = await SeedAsync(osman);
        await SeedFilmAsync(osman);

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        var after = await PersonAsync(Osman);
        Assert.Equal((Tmdb, "tmdb", null), (after.ImageUrl, after.PhotoSource, after.Biography));
        Assert.DoesNotContain(_web.Requests, r => r.Contains("audnex.us/authors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SomeoneWithoutBooks_IsntLookedUpAsAnAuthor()
    {
        var actor = new Person { Name = Osman };
        await SeedFilmAsync(actor);
        var tracked = await _db.People.SingleAsync(p => p.Name == Osman);
        var authors = Authors(new BookProviders(new HttpClient(_web), () => new Settings(_dir, []), new BookProviderThrottle()));

        await authors.RefreshPersonAsync(tracked, default);
        var (photos, biographies) = await authors.CandidatesAsync(tracked, default);

        Assert.Equal((null, null), (tracked.ImageUrl, tracked.Biography));
        Assert.Empty(photos);
        Assert.Empty(biographies);
        Assert.Empty(_web.Requests);
    }

    [Fact]
    public async Task AnAuthorInALibraryTheProfileHides_StillHasTheirFolderPhoto()
    {
        var photo = await AuthorPhotoAsync("author.jpg");
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });
        _db.HiddenLibraryIds = [library.Id];
        var osman = await _db.People.SingleAsync(p => p.Name == Osman);

        await Authors().RefreshPersonAsync(osman, default);
        var (photos, _) = await Authors().CandidatesAsync(osman, default);

        Assert.Equal(($"file://{photo}", "folder"), (osman.ImageUrl, osman.PhotoSource));
        Assert.Contains(photos, p => p.Source == "folder");
    }

    [Fact]
    public async Task ABooksAuthor_IsntAnActorOfTheSameName_ButIsAnotherBooksAuthor()
    {
        var actor = new Person { Name = Osman, TmdbId = "123" };
        await SeedFilmAsync(actor);

        Assert.Null(await BookAssembler.ExistingPersonAsync(_db, Osman, default));

        await SeedAsync(new Person { Name = Keyes });
        var keyes = await BookAssembler.ExistingPersonAsync(_db, Keyes, default);
        Assert.Equal(Keyes, keyes?.Name);
    }

    private async Task SeedFilmAsync(Person person)
    {
        var films = new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = Path.Combine(_dir, "films") }] };
        _db.Libraries.Add(films);
        _db.MediaItems.Add(new MediaItem
        {
            LibraryId = films.Id, Kind = MediaKind.Movie, Title = "A Film", SortTitle = "film", Path = Path.Combine(_dir, "films", "A Film"),
            Credits = [new Credit { Person = person, Role = CreditRole.Actor }],
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnAuthorsFolder_IsListedOncePerRefresh_WhateverTheirBooks()
    {
        var library = await SeedAsync(new Person { Name = Osman, Biography = "Known.", InfoCheckedAt = DateTime.UtcNow });
        var osman = await _db.People.SingleAsync(p => p.Name == Osman);
        for (var i = 0; i < 5; i++)
        {
            await SeedAsync(Path.Combine(Books, Osman, $"Book {i}"), osman, library);
        }

        var authors = Authors();
        foreach (var book in await _db.MediaItems.Include(i => i.Credits).ThenInclude(c => c.Person).ToListAsync())
        {
            await authors.FillBookAsync(book, [], null, [], default);
        }

        // A photo put there mid-refresh is seen next time, not this: the folder was read once.
        await AuthorPhotoAsync("author.jpg");
        var last = await _db.MediaItems.Include(i => i.Credits).ThenInclude(c => c.Person).FirstAsync();
        await authors.FillBookAsync(last, [], null, [], default);
        Assert.Null(osman.ImageUrl);

        await Authors().FillBookAsync(last, [], null, [], default);
        Assert.NotNull(osman.ImageUrl);
    }

    [Fact]
    public async Task AProviderThatFails_IsntAskedAgainThatRefresh()
    {
        _web.DownHosts.Add("api.audnex.us");
        _web.DownHosts.Add("openlibrary.org");
        var library = await SeedAsync(new Person { Name = Osman });
        await SeedAsync(Path.Combine(Books, Keyes, "Another"), new Person { Name = Keyes }, library);

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        Assert.Single(_web.Requests, r => r.Contains("audnex.us/authors", StringComparison.Ordinal));
        Assert.Single(_web.Requests, r => r.Contains("openlibrary.org/search/authors", StringComparison.Ordinal));
        Assert.All(await _db.People.AsNoTracking().ToListAsync(), p => Assert.Null(p.InfoCheckedAt)); // both tried again next time
    }

    /// <summary>Just the authors, online providers left out.</summary>
    private AuthorInfo Authors(BookProviders? providers = null) =>
        new(_db, new ArrClient(new HttpClient(_web)), new Settings(_dir, []), providers, NullLogger.Instance);

    private async Task<Person> PersonAsync(string name) => await _db.People.AsNoTracking().SingleAsync(p => p.Name == name);

    private async Task<string> AuthorPhotoAsync(string name)
    {
        var folder = Directory.CreateDirectory(Path.Combine(Books, Osman)).FullName;
        var path = Path.Combine(folder, name);
        await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF]);
        return path;
    }

    private Task<Library> SeedAsync(params Person[] authors) => SeedAsync(Path.Combine(Books, Osman, Title), authors[0], null, authors[1..]);

    private async Task<Library> SeedAsync(string path, Person author, Library? library = null, params Person[] more)
    {
        if (library is null)
        {
            library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = Books }] };
            _db.Libraries.Add(library);
        }

        var title = Path.GetFileName(path);
        _db.MediaItems.Add(new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = SortTitle.For(title), Path = path,
            Book = new BookDetails { DurationSeconds = 3600 },
            Credits = [.. new[] { author }.Concat(more).Select((p, i) => new Credit { Person = p, Role = CreditRole.Author, Order = i })],
        });
        await _db.SaveChangesAsync();
        return library;
    }

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir, _readarr ? [new ArrInstance { Name = "Readarr", Kind = ArrKind.Readarr, BaseUrl = new Uri("http://readarr.test"), ApiKey = "k" }] : []);
        var http = new HttpClient(_web);
        return new MetadataRefresher(_db, new ArrClient(http), new TvMazeClient(http), new ImageCache(http, settings, NullLogger<ImageCache>.Instance),
            settings, NullLogger<MetadataRefresher>.Instance, bookProviders: new BookProviders(http, () => settings, new BookProviderThrottle()));
    }

    private sealed class Settings(string cache, IReadOnlyList<ArrInstance> instances) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances => instances;
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }
}
