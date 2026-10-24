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
    public async Task Create_DoesNotCountSessionsThatNeverStartedFfmpeg_OnceTheyAreAFewSecondsOld()
    {
        await using var manager = Manager(limit: 2);
        foreach (var unplayed in new[] { manager.Create("/in.mkv", 600, Plan), manager.Create("/in.mkv", 600, Plan) })
        {
            unplayed.StartedAt = DateTime.UtcNow.AddMinutes(-1);
        }

        Assert.Equal(0, manager.ActiveCount);
        Assert.NotNull(manager.Create("/in.mkv", 600, Plan));
    }

    [Fact]
    public async Task Segments_ForAStoppedSession_AreRefusedWhileOthersUseEverySlot()
    {
        await using var manager = Manager(limit: 1, ffmpeg: "true");
        var paused = manager.Create("/in.mkv", 600, Plan);
        paused.StartedAt = DateTime.UtcNow.AddMinutes(-5); // ffmpeg stopped after a long pause
        var other = manager.Create("/in.mkv", 600, Plan);

        await Assert.ThrowsAsync<TranscodeLimitException>(() => paused.GetSegmentAsync(10, CancellationToken.None));

        // Once the slot is free it starts again (here "ffmpeg" exits at once, so the segment fails rather than waits).
        await manager.StopAsync(other.Id);
        Assert.Null(await paused.GetSegmentAsync(10, CancellationToken.None));
        Assert.True(paused.IsActive);
    }

    [Fact]
    public async Task PerProfileLimit_LeavesOneSlotForSomeoneElse()
    {
        await using var three = Manager(limit: 3);
        await using var one = Manager(limit: 1);
        await using var none = Manager(limit: 0);

        Assert.Equal((2, 1, 0), (three.PerProfileLimit, one.PerProfileLimit, none.PerProfileLimit));
    }

    [Fact]
    public async Task Release_SkipsASessionUsedSinceTheSweepLookedAtIt()
    {
        await using var manager = Manager(limit: 0);
        var session = manager.Create("/in.mkv", 600, Plan);
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTM3U\nseg0.ts\n");
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "seg0.ts"), "");

        // The sweep saw it idle, but a segment request came in before the release got going.
        session.LastAccess = DateTime.UtcNow.AddHours(-1);
        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        await session.ReleaseAsync(TimeSpan.FromMinutes(30));
        await session.StopProcessAsync(TimeSpan.FromMinutes(2));

        Assert.True(File.Exists(Path.Combine(session.Directory, "seg0.ts")));

        session.LastAccess = DateTime.UtcNow.AddHours(-1);
        await session.ReleaseAsync(TimeSpan.FromMinutes(30));
        Assert.False(File.Exists(Path.Combine(session.Directory, "seg0.ts")));
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

    private TranscodeManager Manager(int limit, string ffmpeg = "ffmpeg") =>
        new(() => new TranscodeSettings { CacheDirectory = _cache, MaxConcurrentTranscodes = limit, FfmpegPath = ffmpeg }, NullLoggerFactory.Instance);
}
