using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class PlaybackDeciderTests
{
    private static readonly DeviceProfile Chrome = new()
    {
        Containers = ["mp4", "webm"],
        VideoCodecs = ["h264", "vp9", "av1"],
        AudioCodecs = ["aac", "mp3", "opus", "flac"],
        MaxAudioChannels = 2,
    };

    private static readonly DeviceProfile AppleTv = new()
    {
        Client = "tvos",
        Containers = ["mp4"],
        VideoCodecs = ["h264", "hevc"],
        AudioCodecs = ["aac", "ac3", "eac3", "mp3"],
        Hdr = true,
        MaxAudioChannels = 6,
    };

    [Fact]
    public void Mp4H264Aac_DirectPlaysInChrome()
    {
        var decision = PlaybackDecider.Decide(File("mp4", "h264", "aac", 2), Chrome, [], new PlaybackOptions());

        Assert.Equal(PlaybackMethod.DirectPlay, decision.Method);
        Assert.Empty(decision.Reasons);
    }

    [Fact]
    public void MkvHevcEac3_TranscodesInChrome_WithStereoAac()
    {
        var decision = PlaybackDecider.Decide(File("mkv", "hevc", "eac3", 6), Chrome, [], new PlaybackOptions());

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Contains(decision.Reasons, r => r.Contains("MKV", StringComparison.Ordinal));
        Assert.Contains(decision.Reasons, r => r.Contains("HEVC", StringComparison.Ordinal));
        Assert.False(decision.Transcode!.CopyAudio);
        Assert.Equal(2, decision.Transcode.AudioChannels);
        Assert.Equal("mp4a.40.2", decision.Transcode.AudioCodecTag);
    }

    [Fact]
    public void AppleTv_KeepsSurroundSound_WhenRemuxingMkv()
    {
        var decision = PlaybackDecider.Decide(File("mkv", "h264", "eac3", 6), AppleTv, [], new PlaybackOptions());

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.True(decision.Transcode!.CopyAudio);
        Assert.Equal("ec-3", decision.Transcode.AudioCodecTag);
    }

    [Fact]
    public void HdrOnSdrDisplay_IsToneMapped()
    {
        var file = File("mp4", "hevc", "aac", 2, range: VideoRange.Hdr10, height: 2160);

        var decision = PlaybackDecider.Decide(file, AppleTv with { Hdr = false }, [], new PlaybackOptions());

        Assert.True(decision.Transcode!.ToneMap);
        Assert.Equal(2160, decision.Transcode.TargetHeight);
    }

    [Fact]
    public void QualityLimit_CapsResolutionAndBitrate()
    {
        var file = File("mp4", "h264", "aac", 2, height: 2160, bitrate: 40_000_000);

        var decision = PlaybackDecider.Decide(file, Chrome, [], new PlaybackOptions(MaxBitrate: 4_000_000, MaxHeight: 720));

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(720, decision.Transcode!.TargetHeight);
        Assert.True(decision.Transcode.VideoBitrate <= 4_000_000);
    }

    [Fact]
    public void PictureSubtitles_AreBurnedIn_TextSubtitlesAreNot()
    {
        var file = File("mp4", "h264", "aac", 2);
        SubtitleTrack[] subtitles =
        [
            new("e2", 2, null, "hdmv_pgs_subtitle", "eng", null, false, false),
            new("x0", null, "/media/Movie.en.srt", "subrip", "en", null, false, false),
        ];

        var pgs = PlaybackDecider.Decide(file, Chrome, subtitles, new PlaybackOptions(SubtitleKey: "e2"));
        var srt = PlaybackDecider.Decide(file, Chrome, subtitles, new PlaybackOptions(SubtitleKey: "x0"));

        Assert.Equal(subtitles[0], pgs.Transcode!.BurnSubtitle);
        Assert.Equal(PlaybackMethod.DirectPlay, srt.Method);
        Assert.Equal("x0", srt.Subtitle!.Key);
    }

    [Fact]
    public void SecondAudioTrack_RequiresTranscodeInBrowsers()
    {
        var file = File("mp4", "h264", "aac", 2);
        file.Streams.Add(new MediaStream { Index = 2, Type = StreamType.Audio, Codec = "aac", Channels = 2, Language = "fra" });

        var decision = PlaybackDecider.Decide(file, Chrome, [], new PlaybackOptions(AudioStreamIndex: 2));

        Assert.Equal(PlaybackMethod.Transcode, decision.Method);
        Assert.Equal(2, decision.Transcode!.AudioStreamIndex);
    }

    private static MediaFile File(string container, string video, string audio, int channels, VideoRange range = VideoRange.Sdr, int height = 1080, long bitrate = 8_000_000) => new()
    {
        Path = "/media/file",
        Container = container,
        VideoCodec = video,
        Width = height * 16 / 9,
        Height = height,
        VideoRange = range,
        Bitrate = bitrate,
        AudioCodec = audio,
        AudioChannels = channels,
        DurationSeconds = 3600,
        Streams =
        [
            new MediaStream { Index = 0, Type = StreamType.Video, Codec = video, Width = height * 16 / 9, Height = height },
            new MediaStream { Index = 1, Type = StreamType.Audio, Codec = audio, Channels = channels, IsDefault = true, Language = "eng" },
        ],
    };

    [Fact]
    public void WidescreenFilm_IsTreatedAs1080p()
    {
        var file = File("mkv", "hevc", "aac", 2, bitrate: 25_000_000);
        file.Width = 1920;
        file.Height = 800;

        var decision = PlaybackDecider.Decide(file, Chrome, [], new PlaybackOptions());

        Assert.Equal((1920, 800), (decision.Transcode!.TargetWidth, decision.Transcode.TargetHeight));
        Assert.Equal(PlaybackDecider.BitrateFor(1080), decision.Transcode.VideoBitrate);
    }

    [Fact]
    public void Downscaling_KeepsAspectRatio_WithEvenDimensions()
    {
        var file = File("mkv", "h264", "aac", 2, height: 2160);
        file.Width = 3840;
        file.Height = 1606;

        var decision = PlaybackDecider.Decide(file, Chrome, [], new PlaybackOptions(MaxHeight: 1080));

        Assert.Equal((1920, 804), (decision.Transcode!.TargetWidth, decision.Transcode.TargetHeight));
    }
}
