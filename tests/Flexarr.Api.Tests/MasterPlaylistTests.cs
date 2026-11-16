using Flexarr.Api.Playback;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public class MasterPlaylistTests
{
    [Fact]
    public async Task SubtitleTags_CantAddLinesOrEndAttributes_EvenWhenDeduplicated()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-playlist-").FullName;
        var plan = new TranscodePlan(0, 1, 1920, 1080, 8_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
        await using var transcode = new TranscodeSession("abc", "/in.mkv", 600, plan, new TranscodeSettings { CacheDirectory = cache }, NullLogger.Instance);
        const string evil = "Evil\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,URI=\"https://attacker.example/x.m3u8";
        var session = new PlaybackSession
        {
            Id = "abc",
            ItemId = Guid.NewGuid(),
            ProfileId = "main",
            FileId = Guid.NewGuid(),
            FilePath = "/in.mkv",
            Container = "mkv",
            DurationSeconds = 600,
            Method = PlaybackMethod.Transcode,
            CreditsStart = null,
            Transcode = transcode,
            Subtitles =
            [
                new SubtitleTrack("e2", 2, null, "subrip", "en\"g\r\n#EXT-X-ENDLIST", evil, false, false),
                new SubtitleTrack("e3", 3, null, "subrip", "en\"g\r\n#EXT-X-ENDLIST", evil, false, false),
            ],
        };

        var playlist = PlaybackService.MasterPlaylist(session, includeSubtitles: true);

        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Count(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)));
        Assert.DoesNotContain(lines, l => l.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal));
        foreach (var media in lines.Where(l => l.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal)))
        {
            // Quotes only around the four quoted attributes (GROUP-ID, NAME, LANGUAGE, URI).
            Assert.Equal(8, media.Count(c => c == '"'));
            Assert.Contains("URI=\"subtitles/e", media, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// An Apple TV takes a copied HDR stream only when the master declares it fully, the way its own player's HLS
    /// does: the muxed soundtrack as an audio rendition with its channels, the frame rate, the average bandwidth,
    /// no closed captions. A converted stream keeps the plain line.
    /// </summary>
    [Fact]
    public async Task ACopiedStream_DeclaresItsSoundtrackFrameRateAndBandwidth()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-playlist-").FullName;
        var repackage = new Repackage([0, 6, 12], "hvc1", "hvc1.2.4.L153.90", null, "PQ", false, FrameRate: 24000.0 / 1001);
        var plan = new TranscodePlan(0, 1, 1920, 1080, 8_000_000, ToneMap: false, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null, "eac3", Fmp4: true) { Repackage = repackage };
        await using var transcode = new TranscodeSession("abc", "/in.mkv", 600, plan, new TranscodeSettings { CacheDirectory = cache }, NullLogger.Instance);
        var session = new PlaybackSession
        {
            Id = "abc", ItemId = Guid.NewGuid(), ProfileId = "main", FileId = Guid.NewGuid(), FilePath = "/in.mkv", Container = "mkv",
            DurationSeconds = 600, Method = PlaybackMethod.Transcode, CreditsStart = null, Transcode = transcode, Subtitles = [],
            AudioLanguage = "eng", AudioIsAtmos = true,
        };

        var playlist = PlaybackService.MasterPlaylist(session, includeSubtitles: true);

        Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"aud\",NAME=\"English\",LANGUAGE=\"en\",DEFAULT=YES,AUTOSELECT=YES,CHANNELS=\"16/JOC\"\n", playlist, StringComparison.Ordinal);
        var variant = playlist.Split('\n').Single(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
        Assert.Contains("AVERAGE-BANDWIDTH=", variant, StringComparison.Ordinal);
        Assert.Contains("CODECS=\"hvc1.2.4.L153.90,ec-3\"", variant, StringComparison.Ordinal);
        Assert.Contains("FRAME-RATE=23.976", variant, StringComparison.Ordinal);
        Assert.Contains("VIDEO-RANGE=PQ", variant, StringComparison.Ordinal);
        Assert.Contains("AUDIO=\"aud\"", variant, StringComparison.Ordinal);
        Assert.Contains("CLOSED-CAPTIONS=NONE", variant, StringComparison.Ordinal);

        var converted = new TranscodePlan(0, 1, 1920, 1080, 8_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
        await using var plain = new TranscodeSession("def", "/in.mkv", 600, converted, new TranscodeSettings { CacheDirectory = cache }, NullLogger.Instance);
        var plainPlaylist = PlaybackService.MasterPlaylist(new PlaybackSession
        {
            Id = "def", ItemId = Guid.NewGuid(), ProfileId = "main", FileId = Guid.NewGuid(), FilePath = "/in.mkv", Container = "mkv",
            DurationSeconds = 600, Method = PlaybackMethod.Transcode, CreditsStart = null, Transcode = plain, Subtitles = [],
        }, includeSubtitles: false);
        Assert.DoesNotContain("TYPE=AUDIO", plainPlaylist, StringComparison.Ordinal);
        Assert.DoesNotContain("CLOSED-CAPTIONS", plainPlaylist, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Music_SaysItHasNoVideo()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-master-").FullName;
        try
        {
            var audio = new TranscodePlan(-1, 0, 0, 0, 0, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null);
            await using var track = new TranscodeSession("music", "/in.flac", 240, audio, new TranscodeSettings { CacheDirectory = cache }, NullLogger.Instance);
            var playlist = PlaybackService.MasterPlaylist(new PlaybackSession
            {
                Id = "music", ItemId = Guid.NewGuid(), ProfileId = "main", FileId = Guid.NewGuid(), FilePath = "/in.flac", Container = "flac",
                DurationSeconds = 240, Method = PlaybackMethod.Transcode, CreditsStart = null, Transcode = track, Subtitles = [],
            }, includeSubtitles: false);

            Assert.Contains("CODECS=\"mp4a.40.2\"", playlist, StringComparison.Ordinal);
            Assert.DoesNotContain("avc1", playlist, StringComparison.Ordinal);
            Assert.DoesNotContain("RESOLUTION", playlist, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }

    [Fact]
    public async Task ALadder_ListsEachQuality_LowestFirst_WithBusyAndAverageBandwidth_AndKeepsTheSubtitles()
    {
        var cache = Directory.CreateTempSubdirectory("flexarr-playlist-").FullName;
        var top = new TranscodePlan(0, 1, 1920, 1080, 7_744_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null, Fmp4: true) { FrameRate = 24000.0 / 1001 };
        await using var manager = new TranscodeManager(() => new TranscodeSettings { CacheDirectory = cache, HardwareAcceleration = HardwareAcceleration.VideoToolbox },
            NullLoggerFactory.Instance);
        var lead = await manager.CreateAsync("main", "/in.mkv", 600, top, ladder:
        [
            top with { TargetWidth = 854, TargetHeight = 480, VideoBitrate = 1_244_000 },
            top with { TargetWidth = 1280, TargetHeight = 720, VideoBitrate = 3_744_000 },
            top,
        ]);
        var session = new PlaybackSession
        {
            Id = lead.Id, ItemId = Guid.NewGuid(), ProfileId = "main", FileId = Guid.NewGuid(), FilePath = "/in.mkv", Container = "mkv",
            DurationSeconds = 600, Method = PlaybackMethod.Transcode, CreditsStart = null, Transcode = lead,
            Subtitles = [new SubtitleTrack("e2", 2, null, "subrip", "eng", null, false, false)],
        };

        var playlist = PlaybackService.MasterPlaylist(session, includeSubtitles: true);

        var lines = playlist.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Single(lines, l => l.StartsWith("#EXT-X-MEDIA:TYPE=SUBTITLES", StringComparison.Ordinal));
        var variants = lines.Select((line, i) => (line, i)).Where(x => x.line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)).ToList();
        Assert.Equal(["v/0/main.m3u8", "v/1/main.m3u8", "v/2/main.m3u8"], variants.Select(v => lines[v.i + 1]));
        Assert.Equal(
            "#EXT-X-STREAM-INF:BANDWIDTH=1809200,AVERAGE-BANDWIDTH=1436000,RESOLUTION=854x480,CODECS=\"avc1.640028,mp4a.40.2\",FRAME-RATE=23.976,SUBTITLES=\"subs\"",
            variants[0].line);
        Assert.Contains("BANDWIDTH=10259200,AVERAGE-BANDWIDTH=7936000,RESOLUTION=1920x1080", variants[2].line, StringComparison.Ordinal);
    }
}
