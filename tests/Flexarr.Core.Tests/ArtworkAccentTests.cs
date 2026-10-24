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
        Assert.Equal($"{scheme}{cover}", item.PosterUrl); // kept, never served (ImageEndpoints)
        Assert.Null(item.AccentColor);
        Assert.False(Directory.Exists(Path.Combine(_dir, "images")) && Directory.EnumerateFiles(Path.Combine(_dir, "images"), "*", SearchOption.AllDirectories).Any());
    }

    private MetadataRefresher Refresher(HttpMessageHandler radarr)
    {
        var settings = new Settings(_dir);
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(radarr)), new TvMazeClient(new HttpClient(radarr)),
            new ImageCache(new HttpClient(radarr), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance);
    }

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } =
            [new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "key" }];

        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
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
