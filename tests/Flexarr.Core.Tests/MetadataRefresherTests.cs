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
    public async Task ScannedFolder_IsRefreshed_EvenWhenNotStale()
    {
        await AddMovieAsync("Dune", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/dune.jpg");
        await AddMovieAsync("Heat", DateTime.UtcNow.AddDays(-1), poster: "https://image.test/heat.jpg");

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, ["/movies/Dune"], default);

        Assert.Equal(1, result.FromArr + result.FromLookup + result.Unmatched);
        Assert.True(_radarr.Requests > 0);
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

    private async Task AddMovieAsync(string title, DateTime? updatedAt, string? poster)
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
        });
        await _db.SaveChangesAsync();
    }

    private MetadataRefresher Refresher()
    {
        var settings = new Settings(_dir);
        return new MetadataRefresher(_db, new ArrClient(new HttpClient(_radarr)), new TvMazeClient(new HttpClient(_radarr)),
            new ImageCache(new HttpClient(_radarr), settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance);
    }

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } =
            [new ArrInstance { Name = "Radarr", Kind = ArrKind.Radarr, BaseUrl = new Uri("http://radarr.test"), ApiKey = "key" }];

        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>Radarr with nothing in it; or one that never answers, as HttpClient reports a timeout.</summary>
    private sealed class FakeRadarr : HttpMessageHandler
    {
        public int Requests;
        public bool TimeOut;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            if (TimeOut)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
