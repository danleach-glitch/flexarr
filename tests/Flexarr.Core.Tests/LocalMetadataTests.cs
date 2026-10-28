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

/// <summary>Films and shows filled in from the .nfo files and artwork beside them, with or without Radarr and Sonarr.</summary>
public sealed class LocalMetadataTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-local-").FullName;
    private readonly FakeArr _arr = new();
    private readonly List<ArrInstance> _instances = [];
    private FlexarrDbContext _db = null!;

    private string Media => Path.Combine(_dir, "media");

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _arr.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task FilmWithoutRadarr_IsFilledInFromItsFolder()
    {
        var folder = Write("Arrival (2016)/movie.nfo", """
            <movie><title>Arrival</title><year>2016</year><plot>A linguist works with the military.</plot>
            <genre>Drama</genre><uniqueid type="tmdb">329865</uniqueid></movie>
            """);
        Write("Arrival (2016)/poster.jpg", "x");
        Write("Arrival (2016)/fanart.jpg", "x");
        var (library, film) = await AddMovieAsync("Arrival (2016)", folder);

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        await _db.Entry(film).ReloadAsync();
        await _db.Entry(film).Collection(f => f.Genres).LoadAsync();
        Assert.Equal(new MetadataRefreshResult(0, 0, 0, 1), result);
        Assert.Equal("Arrival", film.Title);
        Assert.Equal("A linguist works with the military.", film.Overview);
        Assert.Equal("329865", film.TmdbId);
        Assert.Equal(["Drama"], film.Genres.Select(g => g.Name));
        Assert.Equal($"file://{Path.Combine(folder, "poster.jpg")}", film.PosterUrl);
        Assert.Equal($"file://{Path.Combine(folder, "fanart.jpg")}", film.BackdropUrl);
        Assert.Equal(MetadataRefresher.Local, film.MetadataSource);
    }

    [Fact]
    public async Task LocalPosterAndNfo_WinOverRadarrs()
    {
        var folder = Write("Heat (1995)/poster.jpg", "x");
        Write("Heat (1995)/movie.nfo", "<movie><title>Heat (Director's Definitive Edition)</title></movie>");
        var (library, film) = await AddMovieAsync("Heat", folder);
        _instances.Add(new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "key" });
        _arr.Responses["/api/v3/movie"] = $$"""
            [{"id":9,"title":"Heat","year":1995,"tmdbId":949,"path":{{System.Text.Json.JsonSerializer.Serialize(folder)}},"overview":"A heist.",
              "images":[{"coverType":"poster","remoteUrl":"https://image.test/heat.jpg"},{"coverType":"fanart","remoteUrl":"https://image.test/heat-fanart.jpg"}]}]
            """;

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        await _db.Entry(film).ReloadAsync();
        Assert.Equal(1, result.FromArr);
        Assert.Equal("Heat (Director's Definitive Edition)", film.Title);
        Assert.Equal("A heist.", film.Overview); // the .nfo doesn't say, so Radarr's stays
        Assert.Equal($"file://{Path.Combine(folder, "poster.jpg")}", film.PosterUrl);
        Assert.Equal("https://image.test/heat-fanart.jpg", film.BackdropUrl);
        Assert.Equal("Radarr", film.MetadataSource);
    }

    [Fact]
    public async Task DeletedPoster_IsForgotten()
    {
        var folder = Write("Up (2009)/poster.jpg", "x");
        var (library, film) = await AddMovieAsync("Up", folder);
        await Refresher().RefreshLibraryAsync(library, force: false, default);

        File.Delete(Path.Combine(folder, "poster.jpg"));
        var result = await Refresher().RefreshLibraryAsync(library, force: true, default);

        await _db.Entry(film).ReloadAsync();
        Assert.Equal(1, result.Unmatched);
        Assert.Null(film.PosterUrl);
        Assert.Null(film.MetadataSource);
    }

    [Fact]
    public async Task MatchFixedByHand_IgnoresTheNfo()
    {
        var folder = Write("Dune/movie.nfo", "<movie><title>Dune (1984)</title><uniqueid type=\"tmdb\">841</uniqueid></movie>");
        var (library, film) = await AddMovieAsync("Dune", folder);
        film.TmdbId = "438631";
        film.MatchLocked = true;
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(library, force: true, default);

        await _db.Entry(film).ReloadAsync();
        Assert.Equal("Dune", film.Title);
        Assert.Equal("438631", film.TmdbId);
    }

    [Fact]
    public async Task ShowWithoutSonarr_GetsSeasonPostersAndEpisodeDetails()
    {
        var show = Write("Severance/tvshow.nfo", "<tvshow><title>Severance</title><plot>Work and life, split.</plot></tvshow>");
        Write("Severance/poster.jpg", "x");
        Write("Severance/season01-poster.jpg", "x");
        Write("Severance/Season 2/folder.jpg", "x");
        var first = Path.Combine(Write("Severance/Season 1/Severance - S01E01.mkv", "x"), "Severance - S01E01.mkv");
        Write("Severance/Season 1/Severance - S01E01.nfo", "<episodedetails><title>Good News About Hell</title><aired>2022-02-18</aired></episodedetails>");
        Write("Severance/Season 1/Severance - S01E01-thumb.jpg", "x");
        var second = Path.Combine(Write("Severance/Season 2/Severance - S02E01.mkv", "x"), "Severance - S02E01.mkv");

        var library = new Library { Name = "TV", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = Media }] };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Severance", SortTitle = "severance", Path = show };
        var one = new MediaItem { LibraryId = library.Id, ParentId = series.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1 };
        var two = new MediaItem { LibraryId = library.Id, ParentId = series.Id, Kind = MediaKind.Season, Title = "Season 2", SortTitle = "2", SeasonNumber = 2 };
        var pilot = new MediaItem { LibraryId = library.Id, ParentId = one.Id, Kind = MediaKind.Episode, Title = "Episode 1", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1 };
        var hello = new MediaItem { LibraryId = library.Id, ParentId = two.Id, Kind = MediaKind.Episode, Title = "Episode 1", SortTitle = "1", SeasonNumber = 2, EpisodeNumber = 1 };
        _db.Libraries.Add(library);
        _db.MediaItems.AddRange(series, one, two, pilot, hello);
        _db.MediaFiles.AddRange(new MediaFile { MediaItemId = pilot.Id, Path = first }, new MediaFile { MediaItemId = hello.Id, Path = second });
        await _db.SaveChangesAsync();

        var result = await Refresher().RefreshLibraryAsync(library, force: false, default);

        foreach (var item in new[] { series, one, two, pilot, hello })
        {
            await _db.Entry(item).ReloadAsync();
        }

        Assert.Equal(1, result.FromLocal);
        Assert.Equal("Work and life, split.", series.Overview);
        Assert.Equal($"file://{Path.Combine(show, "poster.jpg")}", series.PosterUrl);
        Assert.Equal($"file://{Path.Combine(show, "season01-poster.jpg")}", one.PosterUrl);
        Assert.Equal($"file://{Path.Combine(show, "Season 2", "folder.jpg")}", two.PosterUrl);
        Assert.Equal("Good News About Hell", pilot.Title);
        Assert.Equal(new DateOnly(2022, 2, 18), pilot.ReleaseDate);
        Assert.Equal($"file://{Path.Combine(show, "Season 1", "Severance - S01E01-thumb.jpg")}", pilot.ThumbUrl);
        Assert.Equal("Episode 1", hello.Title);
        Assert.Null(hello.ThumbUrl);
    }

    [Fact]
    public void LooseFilm_OnlyTakesArtworkNamedAfterIt()
    {
        var film = Path.Combine(Write("Heat (1995).mkv", "x"), "Heat (1995).mkv");
        Write("poster.jpg", "x"); // the library folder's own, not Heat's
        Write("Heat (1995)-fanart.jpg", "x");

        var local = LocalFiles.ForMovie(film);

        Assert.Null(local.Poster);
        Assert.Equal($"file://{Path.Combine(Media, "Heat (1995)-fanart.jpg")}", local.Backdrop);
    }

    /// <returns>The folder the file was written to.</returns>
    private string Write(string relative, string content)
    {
        var path = Path.Combine(Media, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return Path.GetDirectoryName(path)!;
    }

    private async Task<(Library, MediaItem)> AddMovieAsync(string title, string folder)
    {
        var library = new Library { Name = "Movies", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = Media }] };
        var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant(), Path = folder };
        _db.Libraries.Add(library);
        _db.MediaItems.Add(film);
        await _db.SaveChangesAsync();
        return (library, film);
    }

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir, _instances);
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(_arr)), new TvMazeClient(new HttpClient(_arr)),
            new ImageCache(new HttpClient(_arr), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance);
    }

    private sealed class Settings(string cache, IReadOnlyList<ArrInstance> instances) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances => instances;
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>Radarr (and image hosts) answering only what <see cref="Responses"/> says.</summary>
    private sealed class FakeArr : HttpMessageHandler
    {
        public Dictionary<string, string> Responses { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = Responses.GetValueOrDefault(request.RequestUri!.PathAndQuery, "[]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
