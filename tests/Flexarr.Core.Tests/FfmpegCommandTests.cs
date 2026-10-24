using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class FfmpegCommandTests
{
    private static readonly TranscodePlan Plan = new(0, 1, 1920, 1080, 10_000_000, ToneMap: true, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null, "eac3");

    [Fact]
    public void VideoToolbox_DecodesScalesTonemapsAndEncodesOnTheGpu()
    {
        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 10, "/out", Settings(HardwareAcceleration.VideoToolbox)));

        Assert.Contains("-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld", args, StringComparison.Ordinal);
        Assert.Contains("-ss 60 -i /in.mkv", args, StringComparison.Ordinal);
        Assert.Contains("scale_vt=w=1920:h=1080:color_matrix=bt709:color_primaries=bt709:color_transfer=bt709", args, StringComparison.Ordinal);
        Assert.Contains("-c:v h264_videotoolbox", args, StringComparison.Ordinal);
        Assert.Contains("-output_ts_offset 60", args, StringComparison.Ordinal);
        Assert.Contains("-start_number 10", args, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*6)", args, StringComparison.Ordinal);
        Assert.Contains("-c:a aac -ac 2", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Software_UsesLibx264()
    {
        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { ToneMap = false }, 0, "/out", Settings(HardwareAcceleration.None)));

        Assert.DoesNotContain("-hwaccel", args, StringComparison.Ordinal);
        Assert.Contains("scale=1920:1080,format=yuv420p", args, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264 -preset veryfast", args, StringComparison.Ordinal);
    }

    [Fact]
    public void BurnedSubtitles_OverlayBeforeScaling_WithFramesOnTheCpu()
    {
        var burn = new SubtitleTrack("e3", 3, null, "hdmv_pgs_subtitle", "eng", null, false, false);

        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { BurnSubtitle = burn }, 0, "/out", Settings(HardwareAcceleration.VideoToolbox)));

        Assert.DoesNotContain("hwaccel_output_format", args, StringComparison.Ordinal);
        Assert.Contains("-filter_complex [0:0][0:3]overlay,scale=1920:1080,format=yuv420p[v] -map [v]", args, StringComparison.Ordinal);
    }

    [Fact]
    public void MediaPlaylist_ListsEverySegment_WithAShortLastOne()
    {
        var playlist = FfmpegCommand.MediaPlaylist(20);

        Assert.Contains("#EXT-X-PLAYLIST-TYPE:VOD", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:6.000,\n0.ts", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:2.000,\n3.ts", playlist, StringComparison.Ordinal);
        Assert.EndsWith("#EXT-X-ENDLIST\n", playlist, StringComparison.Ordinal);
    }

    private static TranscodeSettings Settings(HardwareAcceleration hw) => new() { CacheDirectory = "/cache", HardwareAcceleration = hw };
}

public class LanguagesTests
{
    [Theory]
    [InlineData("eng", "iTunes", false, "English")]
    [InlineData("eng", "SDH", false, "English (SDH)")]
    [InlineData("fre", null, true, "French (forced)")]
    [InlineData("pt-BR", null, false, "Portuguese (Brazil)")]
    [InlineData(null, "Commentary", false, "Unknown (Commentary)")]
    public void Label(string? code, string? title, bool forced, string expected) =>
        Assert.Equal(expected, Flexarr.Streaming.Languages.Label(code, title, forced));
}
