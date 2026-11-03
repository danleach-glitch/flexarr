using Flexarr.Api.Playback;
using Flexarr.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public class PlaybackSessionsTests
{
    private static readonly TranscodePlan Plan = new(0, 1, 1920, 1080, 8_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);

    [Fact]
    public async Task ATranscodeEndedForAnotherScreen_BeforeItsPlaybackIsAdded_DoesntLeaveAPlaybackBehind()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);

        // Simultaneous starts on one profile: with every slot in use, the first's transcode makes way for the fourth's
        // before the first's request has added its playback.
        var first = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);
        for (var i = 0; i < 3; i++)
        {
            await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);
        }

        Assert.NotNull(transcodes.TakenOverReason(first.Id));

        sessions.Add(Playback(first));

        Assert.Null(sessions.Get(first.Id));
    }

    [Fact]
    public async Task APlaybackAddedFirst_GoesWhenItsTranscodeIsEndedForAnotherScreen()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);

        var first = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);
        sessions.Add(Playback(first));
        Assert.NotNull(sessions.Get(first.Id));

        for (var i = 0; i < 3; i++)
        {
            await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);
        }

        Assert.Null(sessions.Get(first.Id));
    }

    [Fact]
    public async Task AStreamGotReadyAheadOfPlay_IsntPlaying_UntilAPlayerUsesIt()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var transcode = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);
        var warming = Playback(transcode, warming: true);
        sessions.Add(warming);

        Assert.False(PlaybackSessions.Shows(warming, DateTime.UtcNow));
        Assert.Empty(sessions.Playing);
        Assert.Single(sessions.Active);
        Assert.True(transcode.Warming);

        sessions.Reported(warming, new ProgressRequest(12, Paused: false));

        Assert.False(warming.Warming);
        Assert.False(transcode.Warming);
        Assert.True(PlaybackSessions.Shows(warming, DateTime.UtcNow));
        Assert.Single(sessions.Playing);
    }

    [Fact]
    public async Task AStreamGotReadyAheadOfPlay_IsntSweptEarly_SoANextEpisodeCanStillBeTaken()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var credits = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);
        sessions.Add(Playback(credits, warming: true, created: DateTime.UtcNow - TimeSpan.FromMinutes(20)));

        await sessions.SweepAsync();

        Assert.NotNull(sessions.Get(credits.Id));
    }

    [Fact]
    public async Task PlaybacksGotReadyAheadOfPlay_AndNeverTakenUp_GoOnceNoAppWouldTakeThem()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var old = DateTime.UtcNow - PlaybackSessions.WarmingExpiry - TimeSpan.FromMinutes(1);
        var stale = Direct(old);
        var recent = Direct(DateTime.UtcNow - TimeSpan.FromMinutes(5));
        var playing = Direct(old);
        playing.Warming = false;
        sessions.Add(stale);
        sessions.Add(recent);
        sessions.Add(playing);

        await sessions.SweepAsync();

        Assert.Null(sessions.Get(stale.Id));
        Assert.NotNull(sessions.Get(recent.Id));
        Assert.NotNull(sessions.Get(playing.Id));
    }

    [Fact]
    public async Task AStreamGotReadyAheadOfPlay_GivesWayToARealStart_AndItsPlaybackGoes()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 2 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var watching = await transcodes.CreateAsync("other", "/in.mkv", 600, Plan);
        var warm = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);
        sessions.Add(Playback(warm, warming: true));

        // Full: another warm-up is refused, but a real start takes the warm-up's slot, and nobody's stream is taken.
        await Assert.ThrowsAsync<TranscodeLimitException>(() => transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true));
        var real = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);

        Assert.Null(transcodes.Get(warm.Id));
        Assert.Null(sessions.Get(warm.Id)); // a player taking it up after all finds it gone and starts afresh
        Assert.Null(transcodes.TakenOverReason(warm.Id)); // nobody is told their stream was taken
        Assert.Null(transcodes.TakenOverReason(watching.Id));
        Assert.True(real.IsActive && watching.IsActive);
    }

    [Fact]
    public async Task StreamsGotReadyAheadOfPlay_DontCountAsBusy_AndOneProfileKeepsTwo()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 0 }, NullLoggerFactory.Instance);
        var first = await transcodes.CreateAsync("main", "/a.mkv", 600, Plan, preparing: true);
        var second = await transcodes.CreateAsync("main", "/b.mkv", 600, Plan, preparing: true);
        var third = await transcodes.CreateAsync("main", "/c.mkv", 600, Plan, preparing: true);
        var someoneElse = await transcodes.CreateAsync("other", "/d.mkv", 600, Plan, preparing: true);

        Assert.Null(transcodes.Get(first.Id)); // the oldest made way, even with no limit
        Assert.NotNull(transcodes.Get(second.Id));
        Assert.NotNull(transcodes.Get(third.Id));
        Assert.NotNull(transcodes.Get(someoneElse.Id));
        Assert.Equal(0, transcodes.ActiveCount); // Live TV and download preparation don't wait for browsing
    }

    [Fact]
    public async Task UsingAStreamGotReadyAheadOfPlay_DoesTheWorkPutOff_Once()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var done = 0;
        var direct = Direct(DateTime.UtcNow);
        direct.OnUsed = () => done++;
        sessions.Add(direct);

        // The opening bytes, fetched straight after it was got ready: still only ready.
        sessions.Streamed(direct);
        Assert.True(direct.Warming);
        Assert.Equal(0, done);

        sessions.Used(direct);
        sessions.Used(direct);
        Assert.False(direct.Warming);
        Assert.Equal(1, done);
    }

    [Fact]
    public async Task AFileStillReadAfterTheGrace_CountsAsPlaying()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var direct = Direct(DateTime.UtcNow - PlaybackSessions.WarmingGrace - TimeSpan.FromSeconds(1));
        sessions.Add(direct);

        sessions.Streamed(direct);

        Assert.False(direct.Warming);
    }

    private static PlaybackSession Direct(DateTime created) => new()
    {
        Id = Guid.NewGuid().ToString("N"),
        ItemId = Guid.NewGuid(),
        ProfileId = "main",
        FileId = Guid.NewGuid(),
        FilePath = "/in.mp4",
        Container = "mp4",
        DurationSeconds = 600,
        Method = PlaybackMethod.DirectPlay,
        Subtitles = [],
        CreditsStart = null,
        Warming = true,
        Created = created,
    };

    private static PlaybackSession Playback(TranscodeSession transcode, bool warming, DateTime? created = null) => new()
    {
        Id = transcode.Id,
        ItemId = Guid.NewGuid(),
        ProfileId = "main",
        FileId = Guid.NewGuid(),
        FilePath = "/in.mkv",
        Container = "mkv",
        DurationSeconds = 600,
        Method = PlaybackMethod.Transcode,
        Subtitles = [],
        CreditsStart = null,
        Transcode = transcode,
        Warming = warming,
        Created = created ?? DateTime.UtcNow,
    };

    private static PlaybackSession Playback(TranscodeSession transcode) => new()
    {
        Id = transcode.Id,
        ItemId = Guid.NewGuid(),
        ProfileId = "main",
        FileId = Guid.NewGuid(),
        FilePath = "/in.mkv",
        Container = "mkv",
        DurationSeconds = 600,
        Method = PlaybackMethod.Transcode,
        Subtitles = [],
        CreditsStart = null,
        Transcode = transcode,
    };
}
