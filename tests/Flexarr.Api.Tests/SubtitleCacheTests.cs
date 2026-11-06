using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>What the subtitle cache does about a file whose subtitles didn't convert, with a script standing in for ffmpeg.</summary>
public sealed class SubtitleCacheTests : IDisposable
{
    private readonly string _cache = Directory.CreateTempSubdirectory("flexarr-subtitle-cache-").FullName;

    public void Dispose() => Directory.Delete(_cache, recursive: true);

    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    [Fact]
    public void ABigFile_IsGivenLongerToBeRead_UpToAnHour()
    {
        const long gb = 1024L * 1024 * 1024;
        Assert.Equal(TimeSpan.FromMinutes(10), SubtitleCache.LimitFor(0));
        Assert.Equal(TimeSpan.FromMinutes(10), SubtitleCache.LimitFor(8 * gb));
        Assert.Equal(TimeSpan.FromSeconds(30 * 1024 / 25.0), SubtitleCache.LimitFor(30 * gb)); // about twenty minutes
        Assert.Equal(TimeSpan.FromHours(1), SubtitleCache.LimitFor(90 * gb));
    }

    [Fact]
    public void OnlyEmbeddedTracksReadTogether_ShareAPass()
    {
        SubtitleTrack Track(string key, string codec, string? sidecar = null, bool inVideo = false) =>
            new(key, sidecar is null ? 2 : null, sidecar, codec, "eng", null, false, false) { InVideo = inVideo, TeletextReadable = true };

        var one = Track("e2", "subrip");
        var two = Track("e3", "ass");
        var teletext = Track("e4", "dvb_teletext");
        var pictures = Track("e5", "hdmv_pgs_subtitle");
        var sidecar = Track("x0", "subrip", "/films/Film.en.srt");
        var captions = Track("cc0", "eia_608", inVideo: true);

        Assert.True(SubtitleCache.SharedItsPass(one, [one, two, teletext, pictures, sidecar, captions]));
        Assert.False(SubtitleCache.SharedItsPass(one, [one, teletext, pictures, sidecar, captions])); // read by itself
        Assert.False(SubtitleCache.SharedItsPass(teletext, [one, two, teletext])); // teletext has its own pass
        Assert.False(SubtitleCache.SharedItsPass(sidecar, [one, two, sidecar]));
        Assert.False(SubtitleCache.SharedItsPass(captions, [one, two, captions]));
    }

    [Fact]
    public async Task OneBadTrack_DoesntTakeTheOthersWithIt_AndIsntReadAgainAtEveryPlay()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffmpeg: notes each read of the film, fails any that includes stream 3, and otherwise writes
        // the WebVTT it was asked for.
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        var asked = Path.Combine(_cache, "asked");
        File.WriteAllText(ffmpeg, $"""
            #!/bin/sh
            case "$*" in *-filters*|*"filter=movie"*) exit 0;; esac
            echo "$*" >> '{asked}'
            case "$*" in *"-map 0:3 "*) exit 1;; esac
            for arg; do case "$arg" in *.tmp) printf 'WEBVTT\n' > "$arg";; esac; done
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cache = new SubtitleCache(new StaticOptions(new FlexarrOptions { FfmpegPath = ffmpeg, CacheDirectory = _cache }));
        var settings = PlaybackEndpoints.TranscodeSettingsFor(new FlexarrOptions { FfmpegPath = ffmpeg, CacheDirectory = _cache });
        var file = new MediaFile
        {
            Id = Guid.NewGuid(),
            Path = "/nowhere/Film (2020).mkv",
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264" },
                new MediaStream { Index = 2, Type = StreamType.Subtitle, Codec = "subrip", Language = "eng" },
                new MediaStream { Index = 3, Type = StreamType.Subtitle, Codec = "subrip", Language = "fre" },
            ],
        };
        var tracks = Subtitles.For(file);
        var (good, bad) = (tracks[0], tracks[1]);

        // The read of both fails because of the bad one; the good one, asked for, is then read by itself.
        Assert.Equal(Subtitles.CachePath(file, good, settings), await cache.GetAsync(file, tracks, good, CancellationToken.None));
        Assert.Equal(2, File.ReadAllLines(asked).Length);
        Assert.False(Subtitles.Failed(file, good, settings));
        Assert.True(Subtitles.Failed(file, bad, settings));

        // Playback starting again finds one kept and one that failed: the film isn't read.
        cache.Prefetch(file, tracks);
        await Task.Delay(500);
        Assert.Equal(2, File.ReadAllLines(asked).Length);

        // Asked for, the bad one is tried again (by itself), and fails again.
        Assert.Null(await cache.GetAsync(file, tracks, bad, CancellationToken.None));
        var reads = File.ReadAllLines(asked);
        Assert.Equal(3, reads.Length);
        Assert.DoesNotContain("-map 0:2 ", reads[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptionsThatCouldntBeDecoded_ArentDecodedAgainAtEveryPlay_OnlyWhenTheyreChosen()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffmpeg: notes each decode of the film for its captions, which fails.
        var ffmpeg = Path.Combine(_cache, "fake-ffmpeg");
        var asked = Path.Combine(_cache, "asked");
        File.WriteAllText(ffmpeg, $"""
            #!/bin/sh
            case "$*" in *-filters*|*"filter=movie"*) exit 0;; esac
            echo "$*" >> '{asked}'
            exit 1
            """);
        File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var cache = new SubtitleCache(new StaticOptions(new FlexarrOptions { FfmpegPath = ffmpeg, CacheDirectory = _cache }));
        var settings = PlaybackEndpoints.TranscodeSettingsFor(new FlexarrOptions { FfmpegPath = ffmpeg, CacheDirectory = _cache });
        var file = new MediaFile
        {
            Id = Guid.NewGuid(),
            Path = "/nowhere/Recording.ts",
            ClosedCaptions = true,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "mpeg2video" },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "ac3", Language = "eng" },
            ],
        };
        var tracks = Subtitles.For(file);
        var captions = Assert.Single(tracks);
        Assert.True(captions.InVideo);

        // The first play decodes the film once, and it fails.
        Assert.Null(await cache.GetAsync(file, tracks, captions, CancellationToken.None));
        Assert.Single(File.ReadAllLines(asked));
        Assert.True(Subtitles.Failed(file, captions, settings));

        // Later plays leave it alone.
        cache.Prefetch(file, tracks);
        cache.Prefetch(file, tracks);
        await Task.Delay(500);
        Assert.Single(File.ReadAllLines(asked));

        // Choosing the captions tries once more.
        Assert.Null(await cache.GetAsync(file, tracks, captions, CancellationToken.None));
        Assert.Equal(2, File.ReadAllLines(asked).Length);
    }
}
