using Flexarr.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class TranscodeSessionTests : IDisposable
{
    private static readonly TranscodePlan Plan = new(0, 1, 1920, 1080, 10_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
    private readonly string _cache = Directory.CreateTempSubdirectory("flexarr-transcode-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    [Fact]
    public async Task Create_RefusesTranscodesBeyondTheLimit_UntilOneStops()
    {
        await using var manager = Manager(limit: 2);
        var first = manager.Create("/in.mkv", 600, Plan);
        manager.Create("/in.mkv", 600, Plan);

        Assert.Throws<TranscodeLimitException>(() => manager.Create("/in.mkv", 600, Plan));

        await manager.StopAsync(first.Id);
        Assert.NotNull(manager.Create("/in.mkv", 600, Plan));
    }

    [Fact]
    public async Task Create_WithNoLimit_AllowsAny()
    {
        await using var manager = Manager(limit: 0);
        for (var i = 0; i < 5; i++)
        {
            manager.Create("/in.mkv", 600, Plan);
        }

        Assert.Equal(5, manager.ActiveCount);
    }

    [Fact]
    public async Task Segments_AreServedOnceFfmpegListsThem_AndNewOnesAreSeenWhenThePlaylistGrows()
    {
        await using var manager = Manager(limit: 0);
        var session = manager.Create("/in.mkv", 600, Plan);
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTM3U\n#EXTINF:6.0,\nseg0.ts\n");

        Assert.EndsWith("seg0.ts", await session.GetSegmentAsync(0, CancellationToken.None), StringComparison.Ordinal);

        await File.AppendAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTINF:6.0,\nseg1.ts\n");
        Assert.EndsWith("seg1.ts", await session.GetSegmentAsync(1, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_DeletesSegmentsButKeepsTheSession()
    {
        await using var manager = Manager(limit: 0);
        var session = manager.Create("/in.mkv", 600, Plan);
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTM3U\nseg0.ts\n");
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "seg0.ts"), "");

        await session.ReleaseAsync();

        Assert.Empty(Directory.EnumerateFiles(session.Directory));
        Assert.Same(session, manager.Get(session.Id));
    }

    [Fact]
    public async Task SegmentRequests_AfterStopping_FailCleanly()
    {
        await using var manager = Manager(limit: 0);
        var session = manager.Create("/in.mkv", 600, Plan);
        await manager.StopAsync(session.Id);

        Assert.Null(await session.GetSegmentAsync(0, CancellationToken.None));
        Assert.False(Directory.Exists(session.Directory));
    }

    private TranscodeManager Manager(int limit) =>
        new(() => new TranscodeSettings { CacheDirectory = _cache, MaxConcurrentTranscodes = limit }, NullLoggerFactory.Instance);
}
