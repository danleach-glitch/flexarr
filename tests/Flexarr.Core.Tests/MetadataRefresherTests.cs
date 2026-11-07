using System.Net;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class MetadataRefresherTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-meta-").FullName;
    private readonly FakeRadarr _radarr = new();
    private FlexarrDbContext _db = null!;
    private Library _library = null!;

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
        _library = new Library { Name = "Movies", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/movies" }] };
        _db.Libraries.Add(_library);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _radarr.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task NothingStale_DoesNotFetchTheArrLibrary()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/dune.jpg");
        await AddMovieAsync("Unmatched", DateTime.UtcNow.AddHours(-1), poster: null); // Tried an hour ago: not again yet.

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(new MetadataRefreshResult(0, 0, 0), result);
        Assert.Equal(0, _radarr.Requests);
    }

    [Fact]
    public async Task ATimedRefresh_TakesTheLongestDueFirst_AFewAtATime()
    {
        var now = DateTime.UtcNow;
        for (var n = 0; n < 30; n++)
        {
            await AddMovieAsync($"Old {n:D2}", now.AddDays(-8 - ((n * 7) % 30)), poster: "https://image.test/old.jpg");
        }

        await AddMovieAsync("Fresh", now.AddDays(-1), poster: "https://image.test/fresh.jpg");
        var before = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title, i => i.MetadataUpdatedAt);
        var longestDue = before.Where(b => b.Key != "Fresh").OrderBy(b => b.Value).Select(b => b.Key).ToList();

        var first = await Refresher().RefreshDueAsync(_library, 25, [], default);

        Assert.Equal(25, first.FromArr + first.FromLookup + first.Unmatched + first.FromLocal);
        var after = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title, i => i.MetadataUpdatedAt);
        Assert.Equal(longestDue.Take(25).Order(), after.Where(a => a.Value != before[a.Key]).Select(a => a.Key).Order());

        var second = await Refresher().RefreshDueAsync(_library, 25, [], default);
        Assert.Equal(5, second.FromArr + second.FromLookup + second.Unmatched + second.FromLocal);
        var third = await Refresher().RefreshDueAsync(_library, 25, [], default);
        Assert.Equal(new MetadataRefreshResult(0, 0, 0), third);
        Assert.Equal(before["Fresh"], (await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Fresh")).MetadataUpdatedAt);
    }

    [Fact]
    public async Task AScansRefresh_TakesNewTitlesAndItsOwnFolder_AndLeavesTheMerelyOutOfDate()
    {
        var month = DateTime.UtcNow.AddDays(-30);
        await AddMovieAsync("Stale", month, poster: "https://image.test/stale.jpg");
        await AddMovieAsync("New", null, poster: null);
        await AddMovieAsync("Heat", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/heat.jpg");

        var webhook = await Refresher().RefreshScannedAsync(_library, false, ["/movies/Heat"], null, null, default);
        Assert.Equal(2, webhook.FromArr + webhook.FromLookup + webhook.Unmatched + webhook.FromLocal);
        Assert.Equal(month, (await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Stale")).MetadataUpdatedAt);

        await AddMovieAsync("Newer", null, poster: null);
        var full = await Refresher().RefreshScannedAsync(_library, false, [], null, null, default);
        Assert.Equal(1, full.FromArr + full.FromLookup + full.Unmatched + full.FromLocal);
        Assert.Equal(month, (await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Stale")).MetadataUpdatedAt);

        // Asked to refresh everything, it still does.
        var forced = await Refresher().RefreshScannedAsync(_library, true, [], null, null, default);
        Assert.Equal(4, forced.FromArr + forced.FromLookup + forced.Unmatched + forced.FromLocal);
    }

    [Fact]
    public async Task ATimedRefresh_SaysWhatRadarrNotAnsweringLeftDue_AndLeavesWhatItsToldToForNow()
    {
        var month = DateTime.UtcNow.AddDays(-30);
        await AddMovieAsync("Dune", month, poster: "https://image.test/dune.jpg", arrId: 7);
        _radarr.Unreachable.Add("/api/v3/movie/7");
        _radarr.Unreachable.Add("/api/v3/movie");

        var refresher = Refresher();
        await refresher.RefreshDueAsync(_library, 25, [], default);

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync();
        Assert.Equal([dune.Id], refresher.LeftDue);
        Assert.Equal(month, dune.MetadataUpdatedAt);

        await AddMovieAsync("Heat", month.AddDays(1), poster: "https://image.test/heat.jpg");
        var next = await Refresher().RefreshDueAsync(_library, 1, [dune.Id], default);
        Assert.Equal(1, next.FromArr + next.FromLookup + next.Unmatched + next.FromLocal);
        Assert.NotEqual(month.AddDays(1), (await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Heat")).MetadataUpdatedAt);
    }

    [Fact]
    public async Task ScannedFolder_IsRefreshed_EvenWhenNotStale()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/dune.jpg");
        await AddMovieAsync("Heat", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/heat.jpg");

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, ["/movies/Dune"], default);

        Assert.Equal(1, result.FromArr + result.FromLookup + result.Unmatched);
        Assert.True(_radarr.Requests > 0);
    }

    [Fact]
    public async Task WebhookFilmsAlone_AreFetchedOneByOne_NotRadarrsWholeLibrary()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/dune.jpg", arrId: 7);
        await AddMovieAsync("Heat", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/heat.jpg", tmdbId: "949");
        await AddMovieAsync("Up", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/up.jpg");
        _radarr.Responses["/api/v3/movie/7"] = """{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune"}""";
        _radarr.Responses["/api/v3/movie?tmdbId=949"] = """[{"id":9,"title":"Heat","year":1995,"tmdbId":949,"path":"/movies/Heat"}]""";

        var two = await Refresher().RefreshLibraryAsync(_library, force: false, ["/movies/Dune", "/movies/Heat"], default);
        var paths = _radarr.Paths.ToList();
        _radarr.Paths.Clear();
        var three = await Refresher().RefreshLibraryAsync(_library, force: false, ["/movies/Dune", "/movies/Heat", "/movies/Up"], default);

        Assert.Equal(2, two.FromArr);
        Assert.DoesNotContain("/api/v3/movie", paths); // the whole library
        Assert.Contains("/api/v3/movie/7", paths);
        Assert.Contains("/api/v3/movie?tmdbId=949", paths);
        Assert.Contains("/api/v3/movie", _radarr.Paths); // Up has no id to ask by
        _ = three;
    }

    [Fact]
    public async Task ANewFilm_IsFetchedByTheTmdbIdItsWebhookGave()
    {
        await AddMovieAsync("Arrival", null, poster: null);
        await AddMovieAsync("Heat", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/heat.jpg");
        _radarr.Responses["/api/v3/movie?tmdbId=329865"] = """[{"id":3,"title":"Arrival","year":2016,"tmdbId":329865,"path":"/movies/Arrival"}]""";

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, ["/movies/Arrival"], tmdbId: 329865, tvdbId: null, default);

        Assert.Equal(1, result.FromArr);
        Assert.DoesNotContain("/api/v3/movie", _radarr.Paths); // the whole library
        Assert.Equal(("329865", 3), await _db.MediaItems.Where(i => i.Title == "Arrival").Select(i => ValueTuple.Create(i.TmdbId, i.ArrId!.Value)).SingleAsync());
    }

    [Fact]
    public async Task HttpTimeout_CountsAsUnavailable_InsteadOfEscaping()
    {
        await AddMovieAsync("Dune", null, poster: null);
        _radarr.TimeOut = true;

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(1, result.Unmatched);
        Assert.NotNull((await _db.MediaItems.SingleAsync()).MetadataUpdatedAt);
    }

    [Fact]
    public async Task RadarrDown_LeavesItsFilmsLinked_AndDue()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-30), poster: "https://image.test/dune.jpg", arrId: 7);
        _radarr.Unreachable.Add("/api/v3/movie");

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync();
        Assert.Equal(new MetadataRefreshResult(0, 0, 0), result);
        Assert.Equal((7, "Radarr"), (dune.ArrId!.Value, dune.MetadataSource!));
        Assert.True(dune.MetadataUpdatedAt < DateTime.UtcNow.AddDays(-29)); // still due next time
    }

    [Fact]
    public async Task RadarrAnsweringWithoutTheFilm_StillRefreshesIt()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-30), poster: "https://image.test/dune.jpg", arrId: 7);

        await Refresher().RefreshLibraryAsync(_library, force: false, default); // Radarr answered: it has nothing

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync();
        Assert.True(dune.MetadataUpdatedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public async Task ArtworkDroppedMidDownload_DoesNotEndTheRefresh()
    {
        await AddMovieAsync("Dune", null, poster: null);
        await AddMovieAsync("Heat", null, poster: null);
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":1,"title":"Dune","year":2021,"path":"/movies/Dune","images":[{"coverType":"poster","remoteUrl":"https://image.test/dune.jpg"}]},
             {"id":2,"title":"Heat","year":1995,"path":"/movies/Heat","images":[{"coverType":"poster","remoteUrl":"https://image.test/heat.jpg"}]}]
            """;
        _radarr.DropArtwork = true;

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(2, result.FromArr);
        Assert.All(await _db.MediaItems.ToListAsync(), i => Assert.Equal($"https://image.test/{i.Title.ToLowerInvariant()}.jpg", i.PosterUrl));
    }

    [Fact]
    public async Task CastPhotos_LeaveALockedPerson_AndAFolderPhoto_Alone()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7);
        _db.People.AddRange(
            new Person { Name = "Locked", TmdbId = "1", ImageUrl = "https://mine.test/locked.jpg", PhotoSource = PersonSources.Manual, InfoLocked = true },
            new Person { Name = "Folder", TmdbId = "2", ImageUrl = "file:///books/Folder/author.jpg", PhotoSource = PersonSources.Folder },
            new Person { Name = "Anyone", TmdbId = "3", ImageUrl = "https://old.test/anyone.jpg" });
        await _db.SaveChangesAsync();
        _radarr.Responses["/api/v3/movie"] = """[{"id":7,"title":"Dune","year":2021,"path":"/movies/Dune"}]""";
        _radarr.Responses["/api/v3/credit?movieId=7"] = """
            [{"personName":"Locked","personTmdbId":1,"type":"cast","order":0,"images":[{"coverType":"headshot","remoteUrl":"https://image.tmdb.org/t/p/original/1.jpg"}]},
             {"personName":"Folder","personTmdbId":2,"type":"cast","order":1,"images":[{"coverType":"headshot","remoteUrl":"https://image.tmdb.org/t/p/original/2.jpg"}]},
             {"personName":"Anyone","personTmdbId":3,"type":"cast","order":2,"images":[{"coverType":"headshot","remoteUrl":"https://image.tmdb.org/t/p/original/3.jpg"}]}]
            """;

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        var people = await _db.People.AsNoTracking().ToDictionaryAsync(p => p.Name);
        Assert.Equal("https://mine.test/locked.jpg", people["Locked"].ImageUrl);
        Assert.Equal("file:///books/Folder/author.jpg", people["Folder"].ImageUrl);
        Assert.Equal(("https://image.tmdb.org/t/p/original/3.jpg", "tmdb"), (people["Anyone"].ImageUrl, people["Anyone"].PhotoSource));
        Assert.NotNull(people["Anyone"].ImageChangedAt);
    }

    [Fact]
    public async Task ACast_IsMatchedToThePeopleItCredits_WithoutReadingEveryoneElse()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7);
        await AddMovieAsync("Heat", null, poster: null, arrId: 8);
        _db.People.AddRange(
            new Person { Name = "Known", TmdbId = "3" },
            new Person { Name = "Elsewhere", TmdbId = "99" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":7,"title":"Dune","year":2021,"path":"/movies/Dune"},{"id":8,"title":"Heat","year":1995,"path":"/movies/Heat"}]
            """;
        _radarr.Responses["/api/v3/credit?movieId=7"] = """
            [{"personName":"Known","personTmdbId":3,"type":"cast","order":0,"images":[]},
             {"personName":"Newcomer","personTmdbId":4,"type":"cast","order":1,"images":[]}]
            """;
        _radarr.Responses["/api/v3/credit?movieId=8"] = """
            [{"personName":"Newcomer","personTmdbId":4,"type":"cast","order":0,"images":[]},
             {"personName":"Known","personTmdbId":3,"type":"crew","job":"Director","images":[]}]
            """;

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        // Someone the films don't credit was never read.
        Assert.DoesNotContain(_db.ChangeTracker.Entries<Person>(), e => e.Entity.Name == "Elsewhere");
        // The one already known is reused, and the newcomer in both films made once.
        var people = await _db.People.AsNoTracking().OrderBy(p => p.TmdbId).Select(p => new { p.Name, p.TmdbId }).ToListAsync();
        Assert.Equal([("Known", "3"), ("Newcomer", "4"), ("Elsewhere", "99")], people.Select(p => (p.Name, p.TmdbId!)));
        var credits = await _db.Credits.AsNoTracking().Select(c => new { c.MediaItem!.Title, c.Person!.Name, c.Role, c.Order }).ToListAsync();
        Assert.Equal(
            [("Dune", "Known", CreditRole.Actor, 0), ("Dune", "Newcomer", CreditRole.Actor, 1), ("Heat", "Newcomer", CreditRole.Actor, 0), ("Heat", "Known", CreditRole.Director, 1)],
            credits.OrderBy(c => c.Title).ThenBy(c => c.Order).Select(c => (c.Title, c.Name, c.Role, c.Order)));
    }

    [Fact]
    public async Task APhotoUploadedDuringARefresh_IsntOverwritten()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7);
        _db.People.Add(new Person { Name = "Anyone", TmdbId = "3", ImageUrl = "https://image.tmdb.org/t/p/original/3.jpg", PhotoSource = PersonSources.Tmdb });
        await _db.SaveChangesAsync();
        _radarr.Responses["/api/v3/movie"] = """[{"id":7,"title":"Dune","year":2021,"path":"/movies/Dune"}]""";
        _radarr.Responses["/api/v3/credit?movieId=7"] = """
            [{"personName":"Anyone","personTmdbId":3,"type":"cast","order":0,"images":[{"coverType":"headshot","remoteUrl":"https://image.tmdb.org/t/p/original/3.jpg"}]}]
            """;
        var refresher = Refresher();
        await refresher.RefreshLibraryAsync(_library, force: true, default); // the refresh has its people now

        // An admin uploads a photo meanwhile, and TMDB's changes.
        await _db.People.ExecuteUpdateAsync(u => u.SetProperty(p => p.ImageUrl, "file:///data/person-images/anyone.jpg")
            .SetProperty(p => p.PhotoSource, PersonSources.Upload).SetProperty(p => p.InfoLocked, true));
        _radarr.Responses["/api/v3/credit?movieId=7"] = """
            [{"personName":"Anyone","personTmdbId":3,"type":"cast","order":0,"images":[{"coverType":"headshot","remoteUrl":"https://image.tmdb.org/t/p/original/3-new.jpg"}]}]
            """;
        await refresher.RefreshLibraryAsync(_library, force: true, default);

        var anyone = await _db.People.AsNoTracking().SingleAsync(p => p.Name == "Anyone");
        Assert.Equal(("file:///data/person-images/anyone.jpg", "upload", true), (anyone.ImageUrl, anyone.PhotoSource, anyone.InfoLocked));
    }

    [Fact]
    public async Task LookingAFilmUpAgain_OnlyMovesItsArtworkOn_WhenTheArtworkChanged()
    {
        const string Radarrs = """[{"id":1,"title":"Dune","year":2021,"path":"/movies/Dune","images":[{"coverType":"poster","remoteUrl":"https://image.test/POSTER.jpg"}]}]""";
        var lastMonth = DateTime.UtcNow.AddDays(-30);
        await AddMovieAsync("Dune", lastMonth, poster: "https://image.test/dune.jpg", arrId: 1);
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.ArtworkChangedAt, (DateTime?)null)); // as an earlier version left it
        _db.ChangeTracker.Clear();
        _radarr.Responses["/api/v3/movie"] = Radarrs.Replace("POSTER", "dune", StringComparison.Ordinal);
        _radarr.DropArtwork = true;
        async Task<MediaItem> RefreshedAsync()
        {
            Assert.Equal(1, (await Refresher().RefreshLibraryAsync(_library, force: false, default)).FromArr);
            _db.ChangeTracker.Clear();
            return await _db.MediaItems.AsNoTracking().SingleAsync();
        }

        // From before artwork had a time of its own: it's given one the first time the film is looked up.
        var first = await RefreshedAsync();
        Assert.True(first.MetadataUpdatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(first.MetadataUpdatedAt, first.ArtworkChangedAt);

        // A week on (a month, here), looked up again and the same poster found: looked up now, the artwork as it was.
        var lastWeek = DateTime.UtcNow.AddDays(-8);
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, lastWeek).SetProperty(i => i.ArtworkChangedAt, lastWeek));
        var again = await RefreshedAsync();
        Assert.Equal("https://image.test/dune.jpg", again.PosterUrl);
        Assert.True(again.MetadataUpdatedAt > DateTime.UtcNow.AddMinutes(-1));
        Assert.Equal(lastWeek, again.ArtworkChangedAt);

        // And when Radarr has a new poster for it, the artwork is as new as the look-up.
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, lastWeek));
        _radarr.Responses["/api/v3/movie"] = Radarrs.Replace("POSTER", "dune-part-two", StringComparison.Ordinal);
        var redrawn = await RefreshedAsync();
        Assert.Equal("https://image.test/dune-part-two.jpg", redrawn.PosterUrl);
        Assert.True(redrawn.ArtworkChangedAt > DateTime.UtcNow.AddMinutes(-1));
    }

    private async Task AddMovieAsync(string title, DateTime? updatedAt, string? poster, int? arrId = null, string? tmdbId = null)
    {
        _db.MediaItems.Add(new MediaItem
        {
            LibraryId = _library.Id,
            Kind = MediaKind.Movie,
            Title = title,
            SortTitle = title.ToLowerInvariant(),
            Path = $"/movies/{title}",
            MetadataUpdatedAt = updatedAt,
            PosterUrl = poster,
            ArrId = arrId,
            TmdbId = tmdbId,
            MetadataSource = arrId is null ? null : "Radarr",
        });
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Scores_ComeFromRadarr_ThenSeerr()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune",
              "ratings":{"imdb":{"votes":900000,"value":8.0},"tmdb":{"value":7.8},"rottenTomatoes":{"value":83}}}]
            """;
        _radarr.Responses["/api/v1/movie/438631/ratingscombined"] = """
            {"rt":{"title":"Dune","criticsRating":"Certified Fresh","criticsScore":84,"audienceRating":"Upright","audienceScore":90},
             "imdb":{"title":"Dune","criticsScore":8.1}}
            """;

        await Refresher(seerr: true).RefreshLibraryAsync(_library, force: true, default);

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Dune");
        Assert.Equal((8.1, 84, true), (dune.ImdbRating, dune.CriticRating, dune.CriticCertified));
        Assert.Equal(8.0, dune.CommunityRating); // what sorting and smart collections use doesn't change
    }

    [Fact]
    public async Task WithoutSeerr_RadarrsScoresStand_AndNothingIsCertified()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune","ratings":{"imdb":{"value":8.0},"rottenTomatoes":{"value":83}}}]
            """;

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Dune");
        Assert.Equal((8.0, 83, false), (dune.ImdbRating, dune.CriticRating, dune.CriticCertified));
        Assert.DoesNotContain(_radarr.Paths, p => p.StartsWith("/api/v1/", StringComparison.Ordinal));
    }

    private const string TwoFilms = """
        [{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune","ratings":{"imdb":{"value":8.0},"rottenTomatoes":{"value":83}}},
         {"id":9,"title":"Heat","year":1995,"tmdbId":949,"path":"/movies/Heat","ratings":{"imdb":{"value":8.3},"rottenTomatoes":{"value":88}}}]
        """;

    [Fact]
    public async Task SeerrsErrorForOneTitle_KeepsItsScores_AndTheNextIsStillAsked()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        await AddMovieAsync("Heat", null, poster: null, arrId: 9, tmdbId: "949");
        _radarr.Responses["/api/v3/movie"] = TwoFilms;
        _radarr.Fail.Add("/api/v1/movie/438631/ratingscombined"); // Overseerr's 500 when a lookup behind it fails
        _radarr.Responses["/api/v1/movie/949/ratingscombined"] = """{"rt":{"criticsRating":"Certified Fresh","criticsScore":88}}""";

        await Refresher(seerr: true).RefreshLibraryAsync(_library, force: true, default);

        var items = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title);
        Assert.Equal((8.0, 83, false), (items["Dune"].ImdbRating, items["Dune"].CriticRating, items["Dune"].CriticCertified));
        Assert.Null(items["Dune"].ScoresCheckedAt); // asked again next time
        Assert.Equal((88, true), (items["Heat"].CriticRating, items["Heat"].CriticCertified));
        Assert.Equal(2, _radarr.Paths.Count(p => p.StartsWith("/api/v1/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task SeerrUnreachable_IsAskedOnce_AndTheScoresStay()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        await AddMovieAsync("Heat", null, poster: null, arrId: 9, tmdbId: "949");
        _radarr.Responses["/api/v3/movie"] = TwoFilms;
        _radarr.Unreachable.Add("/api/v1/movie/438631/ratingscombined");
        _radarr.Unreachable.Add("/api/v1/movie/949/ratingscombined");

        await Refresher(seerr: true).RefreshLibraryAsync(_library, force: true, default);

        var items = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title);
        Assert.Equal((8.0, 83), (items["Dune"].ImdbRating, items["Dune"].CriticRating));
        Assert.Equal((8.3, 88), (items["Heat"].ImdbRating, items["Heat"].CriticRating));
        Assert.Single(_radarr.Paths, p => p.StartsWith("/api/v1/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneSlowSeerrAnswer_DoesntStopTheNextTitlesScores()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        await AddMovieAsync("Heat", null, poster: null, arrId: 9, tmdbId: "949");
        _radarr.Responses["/api/v3/movie"] = TwoFilms;
        _radarr.Slow.Add("/api/v1/movie/438631/ratingscombined");
        _radarr.Responses["/api/v1/movie/949/ratingscombined"] = """{"rt":{"criticsRating":"Certified Fresh","criticsScore":88}}""";

        await Refresher(seerr: true).RefreshLibraryAsync(_library, force: true, default);

        var items = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title);
        Assert.Equal(83, items["Dune"].CriticRating);
        Assert.Null(items["Dune"].ScoresCheckedAt);
        Assert.Equal((88, true), (items["Heat"].CriticRating, items["Heat"].CriticCertified));
    }

    [Fact]
    public async Task SeerrsScores_AreKeptForAMonth()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune","ratings":{"rottenTomatoes":{"value":83}}}]
            """;
        _radarr.Responses["/api/v1/movie/438631/ratingscombined"] = """{"rt":{"criticsRating":"Certified Fresh","criticsScore":84}}""";
        var refresher = Refresher(seerr: true);

        await refresher.RefreshLibraryAsync(_library, force: true, default);
        await refresher.RefreshLibraryAsync(_library, force: true, default);

        Assert.Single(_radarr.Paths, p => p.StartsWith("/api/v1/", StringComparison.Ordinal));
        var dune = await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Dune");
        Assert.Equal((84, true), (dune.CriticRating, dune.CriticCertified)); // Seerr's, not Radarr's differing 83

        // Radarr losing its score meanwhile doesn't take Seerr's away either.
        _radarr.Responses["/api/v3/movie"] = """[{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune","ratings":{"imdb":{"value":8.0}}}]""";
        await refresher.RefreshLibraryAsync(_library, force: true, default);

        dune = await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Dune");
        Assert.Equal((84, true, 8.0), (dune.CriticRating, dune.CriticCertified, dune.ImdbRating)); // and Radarr fills what Seerr didn't give
    }

    [Fact]
    public async Task ARematch_DropsTheOldTitlesScores()
    {
        var folder = Directory.CreateDirectory(Path.Combine(_dir, "Arrival")).FullName;
        await File.WriteAllTextAsync(Path.Combine(folder, "movie.nfo"), """
            <movie><title>Arrival</title><ratings><rating name="imdb" max="10"><value>7.9</value></rating></ratings>
            <uniqueid type="tmdb">329865</uniqueid></movie>
            """);
        _db.MediaItems.Add(new MediaItem
        {
            LibraryId = _library.Id, Kind = MediaKind.Movie, Title = "Wrong film", SortTitle = "wrong film", Path = folder, TmdbId = "1",
            ImdbRating = 9.1, CriticRating = 97, CriticCertified = true, ScoresCheckedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        var item = await _db.MediaItems.AsNoTracking().SingleAsync();
        Assert.Equal(("329865", 7.9, (int?)null, false, (DateTime?)null),
            (item.TmdbId, item.ImdbRating, item.CriticRating, item.CriticCertified, item.ScoresCheckedAt));
    }

    [Fact]
    public async Task ARottenScore_LosesItsCertificate()
    {
        await AddMovieAsync("Dune", null, poster: null, arrId: 7, tmdbId: "438631");
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.CriticCertified, true).SetProperty(i => i.CriticRating, 84));
        _db.ChangeTracker.Clear();
        _radarr.Responses["/api/v3/movie"] = """
            [{"id":7,"title":"Dune","year":2021,"tmdbId":438631,"path":"/movies/Dune","ratings":{"rottenTomatoes":{"value":41}}}]
            """;

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        var dune = await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Title == "Dune");
        Assert.Equal((41, false, (double?)null), (dune.CriticRating, dune.CriticCertified, dune.ImdbRating));
    }

    private MetadataRefresher Refresher(bool seerr = false)
    {
        var settings = new Settings(_dir) { Seerr = seerr ? new SeerrInstance(new Uri("http://seerr.test"), "key") : null };
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(_radarr)), new TvMazeClient(new HttpClient(_radarr)),
            new ImageCache(new HttpClient(_radarr), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance,
            seerr: new SeerrClient(new HttpClient(_radarr)));
    }

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public SeerrInstance? Seerr { get; init; }

        public IReadOnlyList<ArrInstance> ArrInstances { get; } =
            [new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "key" }];

        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>
    /// Radarr with nothing in it (but what <see cref="Responses"/> says); or one that never answers, as HttpClient
    /// reports a timeout.
    /// </summary>
    private sealed class FakeRadarr : HttpMessageHandler
    {
        public int Requests;
        public bool TimeOut;

        /// <summary>Artwork (from image.test) starts to arrive, then the connection closes.</summary>
        public bool DropArtwork;

        /// <summary>Paths that answer 500.</summary>
        public HashSet<string> Fail { get; } = [];

        /// <summary>Paths that answer only when the caller has given up.</summary>
        public HashSet<string> Slow { get; } = [];

        /// <summary>Paths whose host can't be reached.</summary>
        public HashSet<string> Unreachable { get; } = [];

        /// <summary>Bodies by path and query.</summary>
        public Dictionary<string, string> Responses { get; } = [];

        public System.Collections.Concurrent.ConcurrentQueue<string> Paths { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Paths.Enqueue(request.RequestUri!.PathAndQuery);
            if (TimeOut)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

            if (DropArtwork && request.RequestUri!.Host == "image.test")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) });
            }

            if (Slow.Contains(request.RequestUri!.PathAndQuery))
            {
                return NeverAsync(cancellationToken);
            }

            if (Unreachable.Contains(request.RequestUri!.PathAndQuery))
            {
                throw new HttpRequestException("Connection refused (seerr.test:443)");
            }

            if (Fail.Contains(request.RequestUri!.PathAndQuery))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            }

            var body = Responses.GetValueOrDefault(request.RequestUri!.PathAndQuery, "[]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }

        private static async Task<HttpResponseMessage> NeverAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        }
    }

    /// <summary>The start of an answer, then the connection closes, as HttpClient reports it.</summary>
    private sealed class DroppingStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sent)
            {
                throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
            }

            _sent = true;
            buffer.Span[0] = (byte)'[';
            return ValueTask.FromResult(1);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
