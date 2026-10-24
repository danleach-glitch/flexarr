using Flexarr.Api.Playback;
using Flexarr.Streaming;
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
}
