using System.Net;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

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
    public async Task ASlowDrippingHost_TimesOut()
    {
        using var host = new SlowArtwork { Drip = true };
        host.Release.SetResult();
        var cache = Cache(host);
        cache.DownloadTimeout = TimeSpan.FromMilliseconds(200);

        var path = await cache.GetAsync("https://image.tmdb.org/t/p/original/drip.jpg", 342, default).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Null(path);
    }

    private ImageCache Cache(HttpMessageHandler handler) => new(new HttpClient(handler), new Settings(_dir), NullLogger<ImageCache>.Instance);

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = [];
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

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = Drip ? new StreamContent(new DripStream()) : new ByteArrayContent(Jpeg) };
        }
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
