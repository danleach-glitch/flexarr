using Flexarr.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>A ladder of qualities (docs/remote-streaming.md): one slot and one viewer, each quality started on its own.</summary>
public sealed class TranscodeLadderTests : IDisposable
{
    private static readonly TranscodePlan Top = new(0, 1, 1920, 1080, 7_744_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
    private static readonly TranscodePlan[] Qualities =
    [
        Top with { TargetWidth = 854, TargetHeight = 480, VideoBitrate = 1_244_000 },
        Top with { TargetWidth = 1280, TargetHeight = 720, VideoBitrate = 3_744_000 },
        Top,
    ];

    private readonly string _cache = Directory.CreateTempSubdirectory("flexarr-ladder-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    /// <summary>Stands in for ffmpeg: writes ten segments from the one it was started at, then keeps going.</summary>
    private string Ffmpeg()
    {
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        File.WriteAllText(ffmpeg, """
            #!/bin/sh
            start=0; previous=""
            for arg; do [ "$previous" = "-start_number" ] && start=$arg; previous=$arg; done
            dir=$(dirname "$previous")
            printf '#EXTM3U\n' > "$dir/list"
            i=$start; end=$((start + 10)); while [ $i -lt $end ]; do echo x > "$dir/seg$i.ts"; echo "seg$i.ts" >> "$dir/list"; i=$((i+1)); done
            mv "$dir/list" "$previous"
            sleep 30
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return ffmpeg;
    }

    private TranscodeManager Manager(int limit, string ffmpeg = "ffmpeg") =>
        new(() => new TranscodeSettings { CacheDirectory = _cache, MaxConcurrentTranscodes = limit, FfmpegPath = ffmpeg, HardwareAcceleration = HardwareAcceleration.VideoToolbox },
            NullLoggerFactory.Instance);

    [Fact]
    public async Task ALadder_IsItsTopQuality_WithTheOthersInFoldersInsideIt_AndPlaylistsStartNothing()
    {
        await using var manager = Manager(limit: 3);

        var lead = await manager.CreateAsync("main", "/in.mkv", 600, Top, openingSeconds: 1, ladder: Qualities);

        var ladder = Assert.IsType<TranscodeLadder>(lead.Ladder);
        Assert.Same(lead, ladder.Lead);
        Assert.Equal([480, 720, 1080], ladder.Variants.Select(v => v.Plan.TargetHeight));
        Assert.Same(lead, Assert.Single(manager.Sessions));
        Assert.All(ladder.Variants.Take(2), v => Assert.Equal(lead.Directory, Path.GetDirectoryName(v.Directory)));
        // The same grid for each, a one-second opening included.
        Assert.All(ladder.Variants, v => Assert.Equal(1, v.OpeningSeconds));
        Assert.All(ladder.Variants, v => Assert.Equal(lead.MediaPlaylist(), v.MediaPlaylist()));
        Assert.All(ladder.Variants, v => Assert.False(v.IsRunning));

        // What the viewer does is shared by every quality.
        lead.Speed = 1.5;
        lead.StartSeconds = 42;
        Assert.All(ladder.Variants, v => Assert.Equal((1.5, 42.0), (v.Speed, v.StartSeconds)));
    }

    [Fact]
    public async Task EveryQualityConverting_CountsAsOneSlot()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var manager = Manager(limit: 2, Ffmpeg());
        var lead = await manager.CreateAsync("main", "/in.mkv", 600, Top, ladder: Qualities);

        foreach (var quality in lead.Ladder!.Variants)
        {
            Assert.NotNull(await quality.GetSegmentAsync(4, CancellationToken.None));
        }

        Assert.All(lead.Ladder.Variants, v => Assert.True(v.IsRunning));
        Assert.Equal(1, manager.ActiveCount);
        Assert.Equal(1, manager.EncodingCount);

        // A second viewer still has a slot; a third, with both taken and nobody over their share, doesn't.
        await manager.CreateAsync("guest", "/in.mkv", 600, Top);
        await Assert.ThrowsAsync<TranscodeLimitException>(() => manager.CreateAsync("other", "/in.mkv", 600, Top));
        Assert.Same(lead, manager.Get(lead.Id));
    }

    [Fact]
    public async Task ALadder_NeverTakesAnotherScreensPlace()
    {
        await using var manager = Manager(limit: 1);
        var other = await manager.CreateAsync("guest", "/in.mkv", 600, Top);

        await Assert.ThrowsAsync<TranscodeLimitException>(() => manager.CreateAsync("main", "/in.mkv", 600, Top, ladder: Qualities));

        Assert.Same(other, Assert.Single(manager.Sessions));
    }

    [Fact]
    public async Task AQualityNotFetchedFor30Seconds_Stops_TheOneBeingWatchedDoesnt_AndEndingTheLadderEndsThemAll()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var manager = Manager(limit: 3, Ffmpeg());
        var lead = await manager.CreateAsync("main", "/in.mkv", 600, Top, ladder: Qualities);
        var (low, middle) = (lead.Ladder!.Variants[0], lead.Ladder.Variants[1]);
        Assert.NotNull(await low.GetSegmentAsync(3, CancellationToken.None));
        Assert.NotNull(await middle.GetSegmentAsync(4, CancellationToken.None));
        Assert.Same(middle, lead.Ladder.Current);

        // The player moved up from 480p to 720p 40 seconds ago, and 720p was last fetched just as long ago.
        low.LastAccess = middle.LastAccess = DateTime.UtcNow - TimeSpan.FromSeconds(40);
        await manager.SweepAsync();

        Assert.False(low.IsRunning);
        Assert.True(middle.IsRunning);
        Assert.Equal(1, manager.ActiveCount);

        await manager.StopAsync(lead.Id);
        Assert.False(middle.IsRunning);
        Assert.False(Directory.Exists(lead.Directory));
    }

    [Fact]
    public async Task TheDiskIsLookedAt_AsEachQualityStarts()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        await using var manager = Manager(limit: 3, Ffmpeg());
        long free = TranscodeManager.MinFreeBytes * 2;
        manager.FreeBytes = _ => free;
        var lead = await manager.CreateAsync("main", "/in.mkv", 600, Top, ladder: Qualities);
        Assert.NotNull(await lead.Ladder!.Variants[0].GetSegmentAsync(3, CancellationToken.None));

        free = TranscodeManager.MinFreeBytes - 1;

        await Assert.ThrowsAsync<TranscodeLimitException>(() => lead.GetSegmentAsync(3, CancellationToken.None));
    }
}
