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

            var body = Responses.GetValueOrDefault(request.RequestUri!.PathAndQuery, "[]");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
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
