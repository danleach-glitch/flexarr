using Flexarr.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class TranscodeSessionTests : IDisposable
{
    private static readonly TranscodePlan Plan = new(0, 1, 1920, 1080, 10_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
    private readonly string _cache = Directory.CreateTempSubdirectory("flexarr-transcode-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    [Fact]
    public async Task AHardwareEncoderThatFailsAtOnce_GivesWayToSoftware()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffmpeg: VA-API fails straight away (no device); libx264 writes the segment it was asked for.
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        File.WriteAllText(ffmpeg, """
            #!/bin/sh
            case "$*" in *libx264*) ;; *) echo "No VA display found" >&2; exit 1 ;; esac
            for last; do :; done
            dir=$(dirname "$last")
            echo x > "$dir/seg0.ts"
            printf '#EXTM3U\nseg0.ts\n' > "$last"
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = ffmpeg, HardwareAcceleration = HardwareAcceleration.Vaapi };
        await using var session = new TranscodeSession("fallback", "/in.mkv", 600, Plan, settings, NullLogger.Instance);

        var segment = await session.GetSegmentAsync(0, CancellationToken.None);

        Assert.NotNull(segment);
    }

    [Fact]
    public async Task OnceAnFmp4PlayerHasTheHardwareInitSegment_ASeekDoesntFallBackToSoftware()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // The first (hardware) run writes its init segment and opening segment; every later hardware run fails at once,
        // and a software run would write a segment (with an init segment that wouldn't match the player's).
        var runs = Path.Combine(_cache, "runs");
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        File.WriteAllText(ffmpeg, $$"""
            #!/bin/sh
            echo run >> "{{runs}}"
            for last; do :; done
            dir=$(dirname "$last")
            case "$*" in *libx264*) echo x > "$dir/init.mp4"; echo x > "$dir/seg50.m4s"; printf '#EXTM3U\nseg50.m4s\n' > "$last"; exit 0 ;; esac
            if [ "$(wc -l < "{{runs}}")" -gt 1 ]; then echo "VideoToolbox session failed" >&2; exit 1; fi
            echo x > "$dir/init.mp4"
            echo x > "$dir/seg0.m4s"
            printf '#EXTM3U\nseg0.m4s\n' > "$last"
            sleep 30
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = ffmpeg, HardwareAcceleration = HardwareAcceleration.Vaapi };
        await using var session = new TranscodeSession("fmp4", "/in.mkv", 600, Plan with { Fmp4 = true }, settings, NullLogger.Instance);

        Assert.NotNull(await session.GetInitBytesAsync(CancellationToken.None));
        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));

        Assert.Null(await session.GetSegmentAsync(50, CancellationToken.None));
        Assert.Equal(2, File.ReadAllLines(runs).Length);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStreamGotReadyAheadOfPlay_StopsOnceItCanOpen_UnlessAPlayerTookItUp(bool takenUp)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Writes two minutes of segments at once, then keeps going (as ffmpeg would at playing speed).
        var args = Path.Combine(_cache, "args");
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        File.WriteAllText(ffmpeg, $$"""
            #!/bin/sh
            echo "$*" > "{{args}}"
            for last; do :; done
            dir=$(dirname "$last")
            printf '#EXTM3U\n' > "$dir/list"
            i=0; while [ $i -lt 20 ]; do echo x > "$dir/seg$i.ts"; echo "seg$i.ts" >> "$dir/list"; i=$((i+1)); done
            mv "$dir/list" "$last"
            sleep 30
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await using var manager = Manager(limit: 0, ffmpeg);
        var session = await manager.CreateAsync("a", "/in.mkv", 600, Plan, preparing: true);
        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        session.Warming = !takenUp;

        await manager.SweepAsync();

        // Started with the full run-ahead, so one taken up while it's still going keeps building it.
        Assert.Contains($"-readrate_initial_burst {FfmpegCommand.RunAheadSeconds}", File.ReadAllText(args), StringComparison.Ordinal);
        Assert.Equal(takenUp, session.IsRunning);
        Assert.NotNull(await session.GetSegmentAsync(1, CancellationToken.None));
    }

    /// <summary>
    /// Stands in for ffmpeg: notes what each run was asked, writes <paramref name="segments"/> segments from the one it
    /// was started at, all at once (its head start), then keeps going as ffmpeg would at its pace.
    /// </summary>
    private string PacedFfmpeg(string runs, int segments)
    {
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        File.WriteAllText(ffmpeg, $$"""
            #!/bin/sh
            echo "$*" >> "{{runs}}"
            start=0; previous=""
            for arg; do [ "$previous" = "-start_number" ] && start=$arg; previous=$arg; done
            dir=$(dirname "$previous")
            printf '#EXTM3U\n' > "$dir/list"
            i=$start; end=$((start + {{segments}})); while [ $i -lt $end ]; do echo x > "$dir/seg$i.ts"; echo "seg$i.ts" >> "$dir/list"; i=$((i+1)); done
            mv "$dir/list" "$previous"
            sleep 30
            """);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return ffmpeg;
    }

    [Fact]
    public async Task AStreamWatchedFasterFromTheStart_IsMadeAtThatSpeed()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runs = Path.Combine(_cache, "runs");
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = PacedFfmpeg(runs, 40) };
        await using var session = new TranscodeSession("fast", "/in.mkv", 3600, Plan, settings, NullLogger.Instance) { Speed = 1.5 };

        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));

        Assert.Contains("-readrate 1.5 -readrate_initial_burst 180 ", Assert.Single(File.ReadAllLines(runs)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AViewerWhoSpeedsUpAndCatchesUpWithTheStream_GetsItMadeAtTheirSpeedFromThere()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Each run makes four minutes at once: more than the head start, so by its last segment it's being paced.
        var runs = Path.Combine(_cache, "runs");
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = PacedFfmpeg(runs, 40) };
        await using var session = new TranscodeSession("faster", "/in.mkv", 3600, Plan, settings, NullLogger.Instance);

        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        Assert.NotNull(await session.GetSegmentAsync(39, CancellationToken.None));
        Assert.Contains("-readrate 1.0 ", Assert.Single(File.ReadAllLines(runs)), StringComparison.Ordinal);

        // Sped up: what's already made is still served as it is.
        session.Speed = 2;
        Assert.NotNull(await session.GetSegmentAsync(20, CancellationToken.None));
        Assert.Single(File.ReadAllLines(runs));

        // The next segment isn't made yet, and at real time never would be in time: made from there at twice the speed.
        Assert.NotNull(await session.GetSegmentAsync(40, CancellationToken.None));
        var again = File.ReadAllLines(runs);
        Assert.Equal(2, again.Length);
        Assert.Contains("-readrate 2 -readrate_initial_burst 180 ", again[1], StringComparison.Ordinal);
        Assert.Contains("-start_number 40 ", again[1], StringComparison.Ordinal);

        // Now at the viewer's pace, catching up with it again just waits for it, as before.
        Assert.NotNull(await session.GetSegmentAsync(79, CancellationToken.None));
        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(80, soon.Token));
        Assert.Equal(2, File.ReadAllLines(runs).Length);
    }

    [Fact]
    public async Task AViewerAtNormalSpeedOrSlower_WhoCatchesUpWithTheStream_JustWaitsForIt_AsBefore()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runs = Path.Combine(_cache, "runs");
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = PacedFfmpeg(runs, 40) };
        await using var session = new TranscodeSession("normal", "/in.mkv", 3600, Plan, settings, NullLogger.Instance);

        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        Assert.NotNull(await session.GetSegmentAsync(39, CancellationToken.None));

        // Past the head start and at the end of what's made: the next segment is waited for, never a reason to start again.
        using (var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(40, soon.Token));
        }

        session.Speed = 0.5;
        using (var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(40, soon.Token));
        }

        Assert.Contains("-readrate 1.0 ", Assert.Single(File.ReadAllLines(runs)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AViewerWhoSlowsDownAgain_KeepsTheStreamTheyHave()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var runs = Path.Combine(_cache, "runs");
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = PacedFfmpeg(runs, 40) };
        await using var session = new TranscodeSession("slower", "/in.mkv", 3600, Plan, settings, NullLogger.Instance) { Speed = 2 };

        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        Assert.NotNull(await session.GetSegmentAsync(39, CancellationToken.None));

        // Back to normal speed, then up again to what the stream is already made at: neither starts it again.
        session.Speed = 1;
        using (var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(40, soon.Token));
        }

        session.Speed = 2;
        using (var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(500)))
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(40, soon.Token));
        }

        Assert.Contains("-readrate 2 ", Assert.Single(File.ReadAllLines(runs)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SpeedingUpInsideTheHeadStart_DoesntStartTheStreamAgain()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // A minute made so far: ffmpeg is still on its head start, and making it as fast as it can.
        var runs = Path.Combine(_cache, "runs");
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = PacedFfmpeg(runs, 10) };
        await using var session = new TranscodeSession("early", "/in.mkv", 3600, Plan, settings, NullLogger.Instance);

        Assert.NotNull(await session.GetSegmentAsync(0, CancellationToken.None));
        session.Speed = 2;

        using var soon = new CancellationTokenSource(TimeSpan.FromMilliseconds(700));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.GetSegmentAsync(10, soon.Token));
        Assert.Single(File.ReadAllLines(runs));
    }

    [Fact]
    public async Task WithoutASoftwareEncoder_AFailingHardwareEncoderJustFails()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var ffmpeg = Path.Combine(_cache, "failing-ffmpeg");
        File.WriteAllText(ffmpeg, "#!/bin/sh\nexit 1\n");
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = ffmpeg, HardwareAcceleration = HardwareAcceleration.VideoToolbox, SoftwareFallback = false };
        await using var session = new TranscodeSession("mac", "/in.mkv", 600, Plan, settings, NullLogger.Instance);

        Assert.Null(await session.GetSegmentAsync(0, CancellationToken.None));
    }

    [Fact]
    public async Task SegmentsWellBehindTheViewer_AreDeleted_AndOnlyThoseAreAskedForAgain()
    {
        var settings = new TranscodeSettings { CacheDirectory = _cache, FfmpegPath = "ffmpeg" };
        await using var session = new TranscodeSession("prune", "/in.mkv", 7200, Plan, settings, NullLogger.Instance);
        var extension = Plan.Fmp4 ? ".m4s" : ".ts";
        // As though ffmpeg had already written the first 200 segments (20 minutes).
        File.WriteAllLines(Path.Combine(session.Directory, "ffmpeg.m3u8"), Enumerable.Range(0, 200).Select(i => $"seg{i}{extension}"));
        foreach (var i in Enumerable.Range(0, 200))
        {
            File.WriteAllText(Path.Combine(session.Directory, $"seg{i}{extension}"), "x");
        }

        var watching = await session.GetSegmentAsync(150, CancellationToken.None);

        // 150 segments of 6 s is 15 minutes in; ten minutes back from there is segment 50.
        var kept = (int)(TranscodeSession.KeptBehind.TotalSeconds / FfmpegCommand.SegmentSeconds);
        Assert.NotNull(watching);
        Assert.False(File.Exists(Path.Combine(session.Directory, $"seg0{extension}")));
        Assert.False(File.Exists(Path.Combine(session.Directory, $"seg{149 - kept}{extension}")));
        Assert.True(File.Exists(Path.Combine(session.Directory, $"seg{150 - kept}{extension}")));
        Assert.NotNull(await session.GetSegmentAsync(150 - kept, CancellationToken.None));
    }

    [Fact]
    public async Task ALiveChannelBeingConverted_TakesASlot_AndIsntTakenOver()
    {
        var liveChannels = 1;
        await using var manager = new TranscodeManager(() => new TranscodeSettings { CacheDirectory = _cache, MaxConcurrentTranscodes = 2 },
            NullLoggerFactory.Instance, () => liveChannels);
        await manager.CreateAsync("a", "/in.mkv", 600, Plan);

        // One film and one channel fill two slots; the channel can't be ended to make room, and the film is the only
        // other profile's.
        await Assert.ThrowsAsync<TranscodeLimitException>(() => manager.CreateAsync("b", "/in.mkv", 600, Plan));

        liveChannels = 0;
        Assert.NotNull(await manager.CreateAsync("b", "/in.mkv", 600, Plan));
    }

    [Fact]
    public async Task Create_RefusesTranscodesBeyondTheLimit_UntilOneStops()
    {
        await using var manager = Manager(limit: 2);
        var first = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        await manager.CreateAsync("b", "/in.mkv", 600, Plan);

        // Each holds its share (one of two), so a third profile has to wait.
        await Assert.ThrowsAsync<TranscodeLimitException>(() => manager.CreateAsync("c", "/in.mkv", 600, Plan));

        await manager.StopAsync(first.Id);
        Assert.NotNull(await manager.CreateAsync("c", "/in.mkv", 600, Plan));
    }

    [Fact]
    public async Task OneProfile_UsesFreeSlots_WithoutEndingItsOtherScreens()
    {
        await using var manager = Manager(limit: 3);
        var screens = new[]
        {
            await manager.CreateAsync("a", "/in.mkv", 600, Plan),
            await manager.CreateAsync("a", "/in.mkv", 600, Plan),
            await manager.CreateAsync("a", "/in.mkv", 600, Plan),
        };

        Assert.All(screens, s => Assert.Same(s, manager.Get(s.Id)));
        Assert.All(screens, s => Assert.Null(manager.TakenOverReason(s.Id)));
    }

    [Fact]
    public async Task AtTheLimit_AProfileOverItsShare_GivesUpItsOldest()
    {
        await using var manager = Manager(limit: 3);
        var ended = new List<string>();
        manager.TakenOver += ended.Add;
        var oldest = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        var second = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        var other = await manager.CreateAsync("b", "/in.mkv", 600, Plan);

        var third = await manager.CreateAsync("a", "/in.mkv", 600, Plan);

        Assert.Equal([oldest.Id], ended);
        Assert.Null(manager.Get(oldest.Id));
        Assert.Equal(TranscodeManager.TakenOverBySameProfile, manager.TakenOverReason(oldest.Id));
        Assert.All(new[] { second, other, third }, s => Assert.Same(s, manager.Get(s.Id)));
    }

    [Fact]
    public async Task AtTheLimit_AProfileHoldingEverySlot_GivesOneUpForSomeoneElse()
    {
        await using var manager = Manager(limit: 2);
        var oldest = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        await manager.CreateAsync("a", "/in.mkv", 600, Plan);

        await manager.CreateAsync("b", "/in.mkv", 600, Plan);

        Assert.Equal(TranscodeManager.TakenOverByOtherProfile, manager.TakenOverReason(oldest.Id));
        Assert.Equal(2, manager.ActiveCount);
    }

    [Fact]
    public async Task ARestartingStream_NeverEndsAnother_AndIsToldWhy()
    {
        await using var manager = Manager(limit: 2);
        var first = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        var second = await manager.CreateAsync("a", "/in.mkv", 600, Plan);

        var refused = await Assert.ThrowsAsync<TranscodeLimitException>(() => manager.CreateAsync("a", "/in.mkv", 600, Plan, restarting: true));

        Assert.StartsWith("The stream stopped", refused.Message, StringComparison.Ordinal);
        Assert.Same(first, manager.Get(first.Id));
        Assert.Same(second, manager.Get(second.Id));
    }

    [Fact]
    public async Task SimultaneousStarts_LeaveAProfileNoMoreThanItsShare_OnceTheServerIsFull()
    {
        await using var manager = Manager(limit: 3);
        await manager.CreateAsync("b", "/in.mkv", 600, Plan);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => manager.CreateAsync("a", "/in.mkv", 600, Plan))));

        Assert.Equal(3, manager.ActiveCount);
        Assert.Equal(2, manager.Sessions.Count(s => s.ProfileId == "a"));
    }

    [Fact]
    public async Task Create_DoesNotCountSessionsThatNeverStartedFfmpeg_OnceTheyAreAFewSecondsOld()
    {
        await using var manager = Manager(limit: 2);
        foreach (var unplayed in new[] { await manager.CreateAsync("a", "/in.mkv", 600, Plan), await manager.CreateAsync("a", "/in.mkv", 600, Plan) })
        {
            unplayed.StartedAt = DateTime.UtcNow.AddMinutes(-1);
        }

        Assert.Equal(0, manager.ActiveCount);
        Assert.NotNull(await manager.CreateAsync("a", "/in.mkv", 600, Plan));
    }

    [Fact]
    public async Task Segments_ForAStoppedSession_AreRefusedWhileOthersUseEverySlot()
    {
        await using var manager = Manager(limit: 1, ffmpeg: "true");
        var paused = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        paused.StartedAt = DateTime.UtcNow.AddMinutes(-5); // ffmpeg stopped after a long pause
        var other = await manager.CreateAsync("b", "/in.mkv", 600, Plan);

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
        var session = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
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
            await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        }

        Assert.Equal(5, manager.ActiveCount);
    }

    [Fact]
    public async Task Segments_AreServedOnceFfmpegListsThem_AndNewOnesAreSeenWhenThePlaylistGrows()
    {
        await using var manager = Manager(limit: 0);
        var session = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        await File.WriteAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTM3U\n#EXTINF:6.0,\nseg0.ts\n");

        Assert.EndsWith("seg0.ts", await session.GetSegmentAsync(0, CancellationToken.None), StringComparison.Ordinal);

        await File.AppendAllTextAsync(Path.Combine(session.Directory, "ffmpeg.m3u8"), "#EXTINF:6.0,\nseg1.ts\n");
        Assert.EndsWith("seg1.ts", await session.GetSegmentAsync(1, CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Release_DeletesSegmentsButKeepsTheSession()
    {
        await using var manager = Manager(limit: 0);
        var session = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
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
        var session = await manager.CreateAsync("a", "/in.mkv", 600, Plan);
        await manager.StopAsync(session.Id);

        Assert.Null(await session.GetSegmentAsync(0, CancellationToken.None));
        Assert.False(Directory.Exists(session.Directory));
    }

    private TranscodeManager Manager(int limit, string ffmpeg = "ffmpeg") =>
        new(() => new TranscodeSettings { CacheDirectory = _cache, MaxConcurrentTranscodes = limit, FfmpegPath = ffmpeg }, NullLoggerFactory.Instance);
}
