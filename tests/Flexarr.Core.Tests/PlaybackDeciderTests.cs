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

    /// <summary>The Apple apps from 0.17: fragmented MP4 segments, FLAC and ALAC, and 7.1.</summary>
    private static readonly DeviceProfile AppleTvFmp4 = AppleTv with { AudioCodecs = ["aac", "ac3", "eac3", "mp3", "flac", "alac"], MaxAudioChannels = 8, Fmp4 = true };

    [Fact]
    public void Fmp4_PassesDolbyAtmosThrough_AndKeeps71()
    {
        var atmos = PlaybackDecider.Decide(File("mkv", "h264", "eac3", 6, audioProfile: "Dolby Digital Plus + Dolby Atmos"), AppleTvFmp4, [], new PlaybackOptions()).Transcode!;
        Assert.True(atmos.Fmp4);
        Assert.True(atmos.CopyAudio);
        Assert.Equal("ec-3", atmos.AudioCodecTag);

        var sevenOne = PlaybackDecider.Decide(File("mkv", "h264", "eac3", 8), AppleTvFmp4, [], new PlaybackOptions()).Transcode!;
        Assert.True(sevenOne.CopyAudio);
        Assert.Equal(8, sevenOne.AudioChannels);
    }

    [Theory]
    [InlineData("truehd", null, 8, true)]
    [InlineData("dts", "DTS-HD MA", 8, true)]
    [InlineData("pcm_s24le", null, 6, true)]
    [InlineData("dts", "DTS", 6, false)]
    [InlineData("opus", null, 8, false)]
    public void Fmp4_TurnsWhatAppleCantPlay_IntoLosslessFlac(string codec, string? profile, int channels, bool highResolution)
    {
        var plan = PlaybackDecider.Decide(File("mkv", "h264", codec, channels, audioProfile: profile), AppleTvFmp4, [], new PlaybackOptions()).Transcode!;

        Assert.True(plan.LosslessAudio);
        Assert.False(plan.CopyAudio);
        Assert.Equal(channels, plan.AudioChannels);
        Assert.Equal(highResolution, plan.HighResolutionAudio);
        Assert.Equal("fLaC", plan.AudioCodecTag);
    }

    [Fact]
    public void Fmp4_StereoOrATightBitrateCap_StillGetsAac()
    {
        var stereo = PlaybackDecider.Decide(File("mkv", "h264", "mp2", 2), AppleTvFmp4, [], new PlaybackOptions()).Transcode!;
        Assert.False(stereo.LosslessAudio);
        Assert.Equal("mp4a.40.2", stereo.AudioCodecTag);

        var remote = PlaybackDecider.Decide(File("mkv", "h264", "truehd", 8), AppleTvFmp4, [], new PlaybackOptions(MaxBitrate: 8_000_000)).Transcode!;
        Assert.False(remote.LosslessAudio);
        Assert.Equal(6, remote.AudioChannels);
    }

    [Fact]
    public void WithoutFmp4_TrueHdIsAac51_AsBefore()
    {
        var plan = PlaybackDecider.Decide(File("mkv", "h264", "truehd", 8), AppleTv, [], new PlaybackOptions()).Transcode!;

        Assert.False(plan.Fmp4);
        Assert.False(plan.LosslessAudio);
        Assert.Equal(6, plan.AudioChannels);
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

    private static MediaFile File(string container, string video, string audio, int channels, VideoRange range = VideoRange.Sdr, int height = 1080, long bitrate = 8_000_000,
        string? audioProfile = null) => new()
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
            new MediaStream { Index = 1, Type = StreamType.Audio, Codec = audio, Profile = audioProfile, Channels = channels, IsDefault = true, Language = "eng" },
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
