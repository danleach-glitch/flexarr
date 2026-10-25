using System.Net;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>Alone: the cache's work and its limits are shared by the whole process.</summary>
[CollectionDefinition(nameof(ImageCacheDownloadTests), DisableParallelization = true)]
public sealed class ImageCacheDownloadCollection;

[Collection(nameof(ImageCacheDownloadTests))]
public sealed class ImageCacheDownloadTests : IDisposable
{
    /// <summary>Enough of a JPEG for the cache to take it as one.</summary>
    private static readonly byte[] Jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-images-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task AGivenUpRequest_LeavesTheDownloadRunning_ForTheNextOne()
    {
        using var host = new SlowArtwork();
        var cache = Cache(host);
        const string url = "https://image.tmdb.org/t/p/original/scrolled-past.jpg";

        using var scrolledAway = new CancellationTokenSource();
        var first = cache.GetAsync(url, 342, scrolledAway.Token);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scrolledAway.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        host.Release.SetResult();
        var path = await cache.GetAsync(url, 342, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(1, host.Requests);
    }

    [Fact]
    public async Task WorkNobodyWaitsFor_StopsAfterTheGracePeriod()
    {
        using var host = new SlowArtwork();
        var cache = Cache(host);
        cache.AbandonAfter = TimeSpan.FromMilliseconds(100);
        const string url = "https://image.tmdb.org/t/p/original/scrolled-far-past.jpg";

        using var scrolledAway = new CancellationTokenSource();
        var first = cache.GetAsync(url, 342, scrolledAway.Token);
        await host.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await scrolledAway.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);

        await host.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10)); // the download itself stopped

        // Asked for again later: fetched afresh, not handed the stopped work's failure.
        host.Release.SetResult();
        var path = await cache.GetAsync(url, 342, default).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotNull(path);
        Assert.Equal(2, host.Requests);
    }

    [Fact]
    public async Task Downloads_RunAFewAtATime()
    {
        using var host = new SlowArtwork();
        var cache = Cache(host);

        var all = Enumerable.Range(0, 20).Select(i => cache.GetAsync($"https://image.tmdb.org/t/p/original/page-{i}.jpg", 342, default)).ToList();
        for (var waited = 0; host.Requests < ImageCache.MaxDownloads && waited < 200; waited++)
        {
            await Task.Delay(50);
        }

        await Task.Delay(300); // time for any more to start, if they could
        Assert.Equal(ImageCache.MaxDownloads, host.Requests);

        host.Release.SetResult();
        var paths = await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.All(paths, Assert.NotNull);
        Assert.Equal(20, host.Requests);
    }

    [Fact]
    public async Task PastTheLimit_NewImagesAreRefused_ButSharedOnesStillJoin()
    {
        using var host = new SlowArtwork();
        var cache = Cache(host);
        string Url(int i) => $"https://image.tmdb.org/t/p/original/flood-{i}.jpg";

        var all = Enumerable.Range(0, ImageCache.MaxJobs).Select(i => cache.GetAsync(Url(i), 342, default)).ToList();

        await Assert.ThrowsAsync<ImageCacheBusyException>(() => cache.GetAsync(Url(ImageCache.MaxJobs), 342, default));
        all.Add(cache.GetAsync(Url(0), 342, default));

        host.Release.SetResult();
        var paths = await Task.WhenAll(all).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.All(paths, Assert.NotNull);
        Assert.Equal(ImageCache.MaxJobs, host.Requests);

        // Room again once they're done.
        Assert.NotNull(await cache.GetAsync(Url(ImageCache.MaxJobs), 342, default).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task ASlowDrippingHost_TimesOut()
    {
        using var host = new SlowArtwork { Drip = true };
        host.Release.SetResult();
        var cache = Cache(host);
        cache.DownloadTimeout = TimeSpan.FromMilliseconds(200);

        var path = await cache.GetAsync("https://image.tmdb.org/t/p/original/drip.jpg", 342, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(path);
    }

    [Fact]
    public async Task AConnectionDroppedMidImage_IsAFailedImage_NotAnException()
    {
        var host = new ScriptedArtwork(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DroppingStream()) });
        var cache = Cache(host);

        var path = await cache.GetAsync("https://image.tmdb.org/t/p/original/dropped.jpg", 342, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(path);
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.download", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AMissingImage_IsAnsweredAtOnce_UntilItsWorthAskingAgain()
    {
        var host = new ScriptedArtwork(() => new HttpResponseMessage(HttpStatusCode.NotFound));
        var cache = Cache(host);
        cache.GoneFor = TimeSpan.FromMilliseconds(500);
        const string url = "https://image.tmdb.org/t/p/original/gone.jpg";

        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Equal(1, host.Requests);

        Assert.Null(await cache.GetAsync(url, 780, default)); // another width is another download
        Assert.Equal(2, host.Requests);

        await Task.Delay(700);
        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Equal(3, host.Requests);
    }

    [Fact]
    public async Task AHostThatCantBeFound_IsRememberedAsLongAsAMissingImage()
    {
        var host = new ScriptedArtwork(() => throw new HttpRequestException(HttpRequestError.NameResolutionError));
        var cache = Cache(host);
        cache.UnavailableFor = TimeSpan.Zero;
        const string url = "https://image.tmdb.org/t/p/original/offline.jpg";

        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Null(await cache.GetAsync(url, 342, default));

        Assert.Equal(1, host.Requests);
    }

    [Fact]
    public async Task AServerError_IsOnlyRememberedBriefly()
    {
        var status = HttpStatusCode.ServiceUnavailable;
        var host = new ScriptedArtwork(() => status == HttpStatusCode.OK
            ? new HttpResponseMessage(status) { Content = new ByteArrayContent(Jpeg) }
            : new HttpResponseMessage(status));
        var cache = Cache(host);
        cache.UnavailableFor = TimeSpan.FromMilliseconds(300);
        const string url = "https://image.tmdb.org/t/p/original/busy.jpg";

        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Null(await cache.GetAsync(url, 342, default));
        Assert.Equal(1, host.Requests);

        status = HttpStatusCode.OK;
        await Task.Delay(500);
        Assert.NotNull(await cache.GetAsync(url, 342, default));
        Assert.Equal(2, host.Requests);
    }

    [Fact]
    public async Task ArrArtwork_IsFetchedFromTheInstance_WithItsKey()
    {
        HttpRequestMessage? seen = null;
        var host = new ScriptedArtwork(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) }) { OnRequest = r => seen = r };
        var cache = Cache(host, new ArrInstance { Name = "My Lidarr", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr.test:8686/lidarr/"), ApiKey = "secret" });

        var path = await cache.GetAsync(ArtworkUrl.Arr("My Lidarr", "mediacover/album/2/cover.jpg?lastWrite=1"), 342, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(path);
        Assert.Equal("http://lidarr.test:8686/lidarr/api/v1/mediacover/album/2/cover.jpg?lastWrite=1", seen!.RequestUri!.ToString());
        Assert.Equal("secret", seen.Headers.GetValues("X-Api-Key").Single());
    }

    [Fact]
    public async Task ArrArtwork_OfAnInstanceNoLongerConfigured_IsNotFetchedAnywhere()
    {
        var host = new ScriptedArtwork(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Jpeg) });
        var cache = Cache(host);

        Assert.Null(await cache.GetAsync(ArtworkUrl.Arr("Lidarr", "mediacover/artist/1/poster.jpg"), 342, default));
        Assert.Null(await cache.GetAsync("arr://Lidarr/../../etc/passwd", 342, default));
        Assert.Equal(0, host.Requests);
    }

    private ImageCache Cache(HttpMessageHandler handler, params ArrInstance[] instances) =>
        new(new HttpClient(handler), new Settings(_dir) { ArrInstances = instances }, NullLogger<ImageCache>.Instance);

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; init; } = [];
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "false"; // resizing fails, so the download itself is kept
    }

    /// <summary>An artwork host that answers when told to, or sends one byte and then nothing.</summary>
    private sealed class SlowArtwork : HttpMessageHandler
    {
        public int Requests;
        public bool Drip;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Drip ? new StreamContent(new DripStream()) : new ByteArrayContent(Jpeg) };
        }
    }

    /// <summary>An artwork host that answers every request the same way.</summary>
    private sealed class ScriptedArtwork(Func<HttpResponseMessage> answer) : HttpMessageHandler
    {
        public int Requests;
        public Action<HttpRequestMessage>? OnRequest { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            OnRequest?.Invoke(request);
            return Task.FromResult(answer());
        }
    }

    /// <summary>The start of an image, then the connection closes, as HttpClient reports it.</summary>
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
            Jpeg.CopyTo(buffer.Span);
            return ValueTask.FromResult(Jpeg.Length);
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DripStream : Stream
    {
        private bool _sent;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!_sent)
            {
                _sent = true;
                buffer.Span[0] = 0xFF;
                return 1;
            }

            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
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
