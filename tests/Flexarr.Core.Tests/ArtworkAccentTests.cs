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

/// <summary>Radarr's artwork URLs are only ever fetched over https, even just to work out an accent colour.</summary>
public sealed class ArtworkAccentTests : IAsyncLifetime
{
    /// <summary>An 8×8 red JPEG.</summary>
    private const string RedJpeg = "/9j/4AAQSkZJRgABAgAAAQABAAD//gAPTGF2YzYzLjEuMTAxAP/bAEMACBQUFxQXGxsbGxsbIB4gISEhICAgICEhISQkJCoqKiQkJCEhJCQoKCoqLi8uKysqKy8vMjIyPDw5OUZGSFZWZ//EAEwAAQEAAAAAAAAAAAAAAAAAAAAGAQEBAAAAAAAAAAAAAAAAAAAGBxABAAAAAAAAAAAAAAAAAAAAABEBAAAAAAAAAAAAAAAAAAAAAP/AABEIAAgACAMBIgACEQADEQD/2gAMAwEAAhEDEQA/AIsATX9//9k=";

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-accent-").FullName;
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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Theory]
    [InlineData("file://")]
    [InlineData("embedded://")]
    public async Task LocalArtworkFromRadarr_IsNotReadForTheAccentColour(string scheme)
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        await File.WriteAllBytesAsync(cover, Convert.FromBase64String(RedJpeg));
        _db.MediaItems.Add(new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Movie, Title = "Dune", SortTitle = "dune", Path = "/movies/Dune" });
        await _db.SaveChangesAsync();
        using var radarr = new OneFilmRadarr($"{scheme}{cover}");

        await Refresher(radarr).RefreshLibraryAsync(_library, force: true, default);

        var item = await _db.MediaItems.AsNoTracking().SingleAsync();
        Assert.Null(item.PosterUrl); // dropped: local artwork is served, so only what Flexarr found itself may be
        Assert.Null(item.AccentColor);
        Assert.False(Directory.Exists(Path.Combine(_dir, "images")) && Directory.EnumerateFiles(Path.Combine(_dir, "images"), "*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public async Task AnAlbumCoverThatCantBeRead_IsntFetchedAgainEveryScan()
    {
        var music = new Library { Name = "Music", Type = LibraryType.Music };
        _db.Libraries.Add(music);
        _db.MediaItems.Add(new MediaItem { LibraryId = music.Id, Kind = MediaKind.Album, Title = "Album", SortTitle = "album", PosterUrl = $"https://covers.test/{Guid.NewGuid():N}.jpg" });
        await _db.SaveChangesAsync();
        var covers = new Missing();

        // The image cache itself would wait five minutes; this is about the next scan, hours later.
        await Refresher(covers, goneFor: TimeSpan.Zero).RefreshLibraryAsync(music, force: false, default);
        await Refresher(covers, goneFor: TimeSpan.Zero).RefreshLibraryAsync(music, force: false, default);
        Assert.Equal(1, covers.Requests);

        await Refresher(covers, goneFor: TimeSpan.Zero).RefreshLibraryAsync(music, force: true, default); // asked for: tried again
        Assert.Equal(2, covers.Requests);
    }

    [Fact]
    public async Task AnHttpsPictureBouncedToPlainHttp_IsAskedForAgainOverHttps()
    {
        var host = new BouncesOnce();
        var images = new ImageCache(new HttpClient(host), new Settings(_dir, "false"), NullLogger<ImageCache>.Instance); // No ffmpeg: the picture is kept as it came.

        var path = await images.GetAsync("https://static.tvmaze.com/uploads/images/original_untouched/31/79212.jpg", null, default);

        Assert.NotNull(path);
        Assert.Equal(["https", "https"], host.Schemes);
    }

    [Theory]
    [InlineData("https://static.tvmaze.com/a.jpg", "http://static.tvmaze.com/a.jpg", true)]
    [InlineData("https://static.tvmaze.com/a.jpg", "http://evil.example/a.jpg", false)]
    [InlineData("https://static.tvmaze.com/a.jpg", "http://static.tvmaze.com:8080/a.jpg", false)]
    [InlineData("https://static.tvmaze.com/a.jpg", "https://static.tvmaze.com/b.jpg", false)]
    public void OnlyASameSiteMoveToPlainHttp_IsADowngrade(string from, string to, bool downgrade) =>
        Assert.Equal(downgrade, ImageCache.Downgrade(new Uri(from), new Uri(to)));

    /// <summary>Like TVmaze now and then: the first request bounces to plain http, the next answers.</summary>
    private sealed class BouncesOnce : HttpMessageHandler
    {
        public List<string> Schemes { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Schemes)
            {
                Schemes.Add(request.RequestUri!.Scheme);
                if (Schemes.Count == 1)
                {
                    var bounce = new HttpResponseMessage(HttpStatusCode.MovedPermanently);
                    bounce.Headers.Location = new UriBuilder(request.RequestUri) { Scheme = "http", Port = -1 }.Uri;
                    return Task.FromResult(bounce);
                }
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Convert.FromBase64String(RedJpeg)) });
        }
    }

    private MetadataRefresher Refresher(HttpMessageHandler radarr, TimeSpan? goneFor = null)
    {
        var settings = new Settings(_dir);
        var images = new ImageCache(new HttpClient(radarr), settings, NullLogger<ImageCache>.Instance);
        images.GoneFor = goneFor ?? images.GoneFor;
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(radarr)), new TvMazeClient(new HttpClient(radarr)),
            images, settings, NullLogger<MetadataRefresher>.Instance);
    }

    /// <summary>Answers 404 to everything, and counts.</summary>
    private sealed class Missing : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Settings(string cache, string ffmpeg = "ffmpeg") : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } =
            [new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "key" }];

        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => ffmpeg;
    }

    /// <summary>Radarr with one film, whose poster is wherever it says.</summary>
    private sealed class OneFilmRadarr(string poster) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var json = request.RequestUri!.AbsolutePath.EndsWith("/movie", StringComparison.Ordinal)
                ? System.Text.Json.JsonSerializer.Serialize(new[]
                {
                    new { id = 1, title = "Dune", path = "/movies/Dune", tmdbId = 438631, images = new[] { new { coverType = "poster", remoteUrl = poster } } },
                })
                : "[]";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
