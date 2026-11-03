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
    public async Task AStreamGotReadyAheadOfPlay_ThatNobodyTookUp_Ends()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 3 }, NullLoggerFactory.Instance);
        var sessions = new PlaybackSessions(transcodes, NullLogger<PlaybackSessions>.Instance);
        var stale = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);
        var fresh = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);
        sessions.Add(Playback(stale, warming: true, created: DateTime.UtcNow - PlaybackSessions.WarmingExpiry - TimeSpan.FromMinutes(1)));
        sessions.Add(Playback(fresh, warming: true));

        await sessions.SweepAsync();

        Assert.Null(sessions.Get(stale.Id));
        Assert.Null(transcodes.Get(stale.Id));
        Assert.NotNull(sessions.Get(fresh.Id));
    }

    [Fact]
    public async Task AStreamGotReadyAheadOfPlay_GivesWayToARealStart_WithoutBeingTakenOver()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-sessions-").FullName;
        await using var transcodes = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = cache, MaxConcurrentTranscodes = 2 }, NullLoggerFactory.Instance);
        var watching = await transcodes.CreateAsync("other", "/in.mkv", 600, Plan);
        var warm = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true);

        // Full: another warm-up is refused, but a real start takes the warm-up's slot, and nobody's stream is taken.
        await Assert.ThrowsAsync<TranscodeLimitException>(() => transcodes.CreateAsync("main", "/in.mkv", 600, Plan, preparing: true));
        var real = await transcodes.CreateAsync("main", "/in.mkv", 600, Plan);

        Assert.NotNull(transcodes.Get(warm.Id)); // still there, for a player that asks for it after all
        Assert.False(warm.IsActive);
        Assert.Null(transcodes.TakenOverReason(warm.Id));
        Assert.Null(transcodes.TakenOverReason(watching.Id));
        Assert.True(real.IsActive && watching.IsActive);
    }

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
