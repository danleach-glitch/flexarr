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

/// <summary>Films from TMDB (with the owner's key) and shows from TVmaze, when there's no Radarr or Sonarr.</summary>
public sealed class OnlineMetadataTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-online-").FullName;
    private readonly FakeWeb _web = new();
    private FlexarrDbContext _db = null!;
    private string? _tmdbKey = "key123";

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _web.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task FilmWithoutRadarr_ComesFromTmdb()
    {
        var (library, film) = await AddAsync(LibraryType.Movies, new MediaItem { Kind = MediaKind.Movie, Title = "Arrival", SortTitle = "arrival", Year = 2016, Path = "/nowhere/Arrival (2016)" });
        _web.Responses["api.themoviedb.org/3/search/movie?include_adult=false&query=Arrival&year=2016&api_key=key123"] =
            """{"results":[{"id":11,"title":"Arrivals","release_date":"2016-01-01"},{"id":329865,"title":"Arrival","release_date":"2016-11-10"}]}""";
        _web.Responses["api.themoviedb.org/3/movie/329865?append_to_response=credits,release_dates,videos,images&include_image_language=en,null&api_key=key123"] = """
            {"id":329865,"imdb_id":"tt2543164","title":"Arrival","overview":"Linguistics.","release_date":"2016-11-10","runtime":116,"vote_average":7.6,
             "popularity":50,"poster_path":"/arrival.jpg","backdrop_path":"/arrival-bg.jpg","genres":[{"id":18,"name":"Drama"}],
             "production_companies":[{"name":"21 Laps"}],
             "credits":{"cast":[{"id":1,"name":"Amy Adams","character":"Louise","profile_path":"/amy.jpg","order":0}],
                        "crew":[{"id":2,"name":"Denis Villeneuve","job":"Director"},{"id":3,"name":"Someone","job":"Grip"}]},
             "release_dates":{"results":[{"iso_3166_1":"GB","release_dates":[{"certification":"12A","type":3}]},
                                         {"iso_3166_1":"US","release_dates":[{"certification":"","type":1},{"certification":"PG-13","type":3}]}]},
             "videos":{"results":[{"site":"YouTube","key":"abc","type":"Trailer","official":true}]},
             "images":{"posters":[],"backdrops":[],"logos":[{"file_path":"/logo.svg","iso_639_1":"en","vote_average":9},{"file_path":"/logo.png","iso_639_1":"en","vote_average":5}]}}
            """;

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        var item = await _db.MediaItems.Include(i => i.Genres).Include(i => i.Credits).ThenInclude(c => c.Person).SingleAsync(i => i.Id == film.Id);
        Assert.Equal(1, result.FromLookup);
        Assert.Equal(("329865", "tt2543164", MetadataRefresher.TmdbSearch), (item.TmdbId, item.ImdbId, item.MetadataSource));
        Assert.Equal("Linguistics.", item.Overview);
        Assert.Equal(("12A", 12), (item.ContentRating, item.RatingAge)); // the UK's, for age limits
        Assert.Equal("21 Laps", item.Studio);
        Assert.Equal("abc", item.TrailerYouTubeId);
        Assert.Equal("https://image.tmdb.org/t/p/original/arrival.jpg", item.PosterUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/arrival-bg.jpg", item.BackdropUrl);
        Assert.Equal("https://image.tmdb.org/t/p/original/logo.png", item.LogoUrl);
        Assert.Equal(["Drama"], item.Genres.Select(g => g.Name));
        Assert.Equal([("Amy Adams", CreditRole.Actor), ("Denis Villeneuve", CreditRole.Director)], item.Credits.OrderBy(c => c.Order).Select(c => (c.Person!.Name, c.Role)));
    }

    [Fact]
    public async Task WithoutAKey_TmdbIsNotAsked()
    {
        _tmdbKey = null;
        var (library, _) = await AddAsync(LibraryType.Movies, new MediaItem { Kind = MediaKind.Movie, Title = "Arrival", SortTitle = "arrival", Path = "/nowhere/Arrival" });

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        Assert.Equal(1, result.Unmatched);
        Assert.DoesNotContain(_web.Asked, a => a.Contains("themoviedb", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AReadAccessToken_GoesInTheHeader()
    {
        _tmdbKey = "eyJhbGciOiJIUzI1NiJ9.token";
        var (library, _) = await AddAsync(LibraryType.Movies, new MediaItem { Kind = MediaKind.Movie, Title = "Heat", SortTitle = "heat", TmdbId = "949", Path = "/nowhere/Heat" });

        await Refresher().RefreshLibraryAsync(library, force: false, default);

        Assert.Contains("api.themoviedb.org/3/movie/949?append_to_response=credits,release_dates,videos,images&include_image_language=en,null", _web.Asked);
        Assert.Equal("Bearer eyJhbGciOiJIUzI1NiJ9.token", _web.Authorization);
    }

    [Fact]
    public async Task ShowWithoutSonarr_ComesFromTvMaze()
    {
        var series = new MediaItem { Kind = MediaKind.Series, Title = "Severance", SortTitle = "severance", Path = "/nowhere/Severance" };
        var (library, _) = await AddAsync(LibraryType.TvShows, series);
        var season = new MediaItem { LibraryId = library.Id, ParentId = series.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1 };
        var pilot = new MediaItem { LibraryId = library.Id, ParentId = season.Id, Kind = MediaKind.Episode, Title = "Episode 1", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1 };
        _db.MediaItems.AddRange(season, pilot);
        await _db.SaveChangesAsync();
        _web.Responses["api.tvmaze.com/search/shows?q=Severance"] = """
            [{"score":0.9,"show":{"id":44933,"name":"Severance","premiered":"2022-02-18","summary":"<p>Work &amp; <b>life</b>.</p>","genres":["Drama"],
              "rating":{"average":8.4},"webChannel":{"name":"Apple TV+"},"averageRuntime":55,"status":"Running",
              "image":{"medium":"https://static.tvmaze.com/uploads/images/medium_portrait/1/1.jpg","original":"https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg"},
              "externals":{"thetvdb":371980,"imdb":"tt11280740"}}}]
            """;
        _web.Responses["api.tvmaze.com/shows/44933/images"] = """[{"type":"background","main":true,"resolutions":{"original":{"url":"https://static.tvmaze.com/uploads/images/original_untouched/2/2.jpg"}}}]""";
        _web.Responses["api.tvmaze.com/shows/44933/seasons"] = """[{"id":1,"number":1,"image":{"original":"https://static.tvmaze.com/uploads/images/original_untouched/3/3.jpg"}}]""";
        _web.Responses["api.tvmaze.com/shows/44933/episodes?specials=1"] = """
            [{"id":9,"name":"Good News About Hell","season":1,"number":1,"airdate":"2022-02-18","runtime":57,"summary":"<p>Mark is promoted.</p>",
              "image":{"original":"https://static.tvmaze.com/uploads/images/original_untouched/4/4.jpg"}}]
            """;

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        foreach (var item in new[] { series, season, pilot })
        {
            await _db.Entry(item).ReloadAsync();
        }

        Assert.Equal(1, result.FromLookup);
        Assert.Equal(("44933", "371980", MetadataRefresher.TvMazeSearch), (series.TvMazeId, series.TvdbId, series.MetadataSource));
        Assert.Equal("Work & life.", series.Overview);
        Assert.Equal("Apple TV+", series.Studio);
        Assert.Equal("https://static.tvmaze.com/uploads/images/original_untouched/2/2.jpg", series.BackdropUrl);
        Assert.Equal("https://static.tvmaze.com/uploads/images/original_untouched/3/3.jpg", season.PosterUrl);
        Assert.Equal("Good News About Hell", pilot.Title);
        Assert.Equal("Mark is promoted.", pilot.Overview);
        Assert.Equal("https://static.tvmaze.com/uploads/images/original_untouched/4/4.jpg", pilot.ThumbUrl);
    }

    [Fact]
    public async Task ShowFixedByHand_IsFetchedByItsTvMazeId_NotSearched()
    {
        var (library, _) = await AddAsync(LibraryType.TvShows,
            new MediaItem { Kind = MediaKind.Series, Title = "The Office", SortTitle = "office", Path = "/nowhere/The Office", TvMazeId = "526", MatchLocked = true });
        _web.Responses["api.tvmaze.com/shows/526"] = """{"id":526,"name":"The Office","premiered":"2005-03-24"}""";

        await Refresher().RefreshLibraryAsync(library, force: false, default);

        Assert.Contains("api.tvmaze.com/shows/526", _web.Asked);
        Assert.DoesNotContain(_web.Asked, a => a.Contains("search", StringComparison.Ordinal));
        Assert.Equal(2005, (await _db.MediaItems.SingleAsync()).Year);
    }

    [Theory]
    [InlineData("<p>One</p><p>Two &amp; three</p>", "One\n\nTwo & three")]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TvMazeSummaries_BecomePlainText(string? html, string? text)
    {
        Assert.Equal(text, MetadataRefresher.PlainText(html));
    }

    private async Task<(Library, MediaItem)> AddAsync(LibraryType type, MediaItem item)
    {
        var library = new Library { Name = type.ToString(), Type = type, Folders = [new LibraryFolder { Path = "/nowhere" }] };
        _db.Libraries.Add(library);
        item.LibraryId = library.Id;
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        return (library, item);
    }

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir, _tmdbKey);
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(_web)), new TvMazeClient(new HttpClient(_web)),
            new ImageCache(new HttpClient(_web), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance,
            new TmdbClient(new HttpClient(_web)));
    }

    private sealed class Settings(string cache, string? tmdbKey) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = [];
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
        public string? TmdbApiKey => tmdbKey;
    }

    /// <summary>TMDB and TVmaze, answering what <see cref="Responses"/> holds (by host, path and query) and 404 otherwise.</summary>
    private sealed class FakeWeb : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];

        public List<string> Asked { get; } = [];

        public string? Authorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = request.RequestUri!.Host + Uri.UnescapeDataString(request.RequestUri.PathAndQuery);
            lock (Asked)
            {
                Asked.Add(key);
            }

            Authorization ??= request.Headers.Authorization?.ToString();
            return Task.FromResult(Responses.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
