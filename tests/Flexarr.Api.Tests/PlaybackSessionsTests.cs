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
