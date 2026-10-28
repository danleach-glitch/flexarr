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
        Assert.Contains("-ss 60 -protocol_whitelist file -i /in.mkv", args, StringComparison.Ordinal);
        Assert.Contains("scale_vt=w=1920:h=1080:color_matrix=bt709:color_primaries=bt709:color_transfer=bt709", args, StringComparison.Ordinal);
        Assert.Contains("-c:v h264_videotoolbox", args, StringComparison.Ordinal);
        Assert.Contains("-output_ts_offset 60", args, StringComparison.Ordinal);
        Assert.Contains("-start_number 10", args, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*6)", args, StringComparison.Ordinal);
        Assert.Contains("-c:a aac -ac 2", args, StringComparison.Ordinal);
    }

    [Fact]
    public void ATrack_BecomesStereoAacInAnM4a_WithNoPicture()
    {
        var args = string.Join(' ', FfmpegCommand.M4a("/in.ogg", 1, 6, "/out/x.part.m4a"));

        Assert.Contains("-progress pipe:1", args, StringComparison.Ordinal);
        Assert.Contains("-protocol_whitelist file -i /in.ogg -map 0:1 -vn", args, StringComparison.Ordinal);
        Assert.Contains("-c:a aac -ac 2 -b:a 256k", args, StringComparison.Ordinal);
        Assert.EndsWith("-f ipod /out/x.part.m4a", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:v", args, StringComparison.Ordinal);
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
        // HDR is mapped to SDR on the CPU before the subtitles go on.
        Assert.Contains("-filter_complex [0:0]zscale=transfer=linear:npl=100,format=gbrpf32le,tonemap=hable:desat=0,zscale=transfer=bt709:matrix=bt709:primaries=bt709:range=tv[sdr];"
            + "[0:3][sdr]scale2ref=w=oh*mdar:h=rh[sub][base];[base][sub]overlay=(W-w)/2:(H-h)/2,scale=1920:1080,format=yuv420p[v] -map [v]", args, StringComparison.Ordinal);

        var sdr = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { BurnSubtitle = burn, ToneMap = false }, 0, "/out", Settings(HardwareAcceleration.VideoToolbox)));
        Assert.Contains("-filter_complex [0:3][0:0]scale2ref=w=oh*mdar:h=rh[sub][base];[base][sub]overlay=(W-w)/2:(H-h)/2,scale=1920:1080,format=yuv420p[v] -map [v]", sdr, StringComparison.Ordinal);
    }

    [Fact]
    public void Hdr_IsMappedToSdr_InSoftware_AndOnTheCpuForNvencAndQuickSync()
    {
        var software = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", Settings(HardwareAcceleration.None)));
        var nvenc = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { SourceVideoCodec = "hevc" }, 0, "/out", Settings(HardwareAcceleration.Nvenc)));
        var qsv = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { SourceVideoCodec = "hevc" }, 0, "/out", Settings(HardwareAcceleration.Qsv)));

        Assert.Contains("-vf zscale=transfer=linear", software, StringComparison.Ordinal);
        Assert.Contains("-c:v libx264", software, StringComparison.Ordinal);
        // Decoded on the CPU, mapped there, then encoded on the GPU.
        Assert.DoesNotContain("-hwaccel cuda", nvenc, StringComparison.Ordinal);
        Assert.Contains("zscale=transfer=linear", nvenc, StringComparison.Ordinal);
        Assert.Contains("h264_nvenc", nvenc, StringComparison.Ordinal);
        Assert.DoesNotContain("-hwaccel qsv", qsv, StringComparison.Ordinal);
        Assert.Contains("zscale=transfer=linear", qsv, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutZscale_HdrIsSentOnRatherThanFailing()
    {
        var settings = Settings(HardwareAcceleration.None) with { CanToneMapInSoftware = false };

        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", settings));

        Assert.DoesNotContain("zscale", args, StringComparison.Ordinal);
        Assert.Contains("-vf scale=1920:1080,format=yuv420p", args, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioOnly_SkipsVideoAndHardware()
    {
        var plan = new TranscodePlan(-1, 0, 0, 0, 0, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null, "wmav2");

        var args = string.Join(' ', FfmpegCommand.Hls("/in.wma", plan, 0, "/out", Settings(HardwareAcceleration.VideoToolbox)));

        Assert.DoesNotContain("-hwaccel", args, StringComparison.Ordinal);
        Assert.DoesNotContain("-c:v", args, StringComparison.Ordinal);
        Assert.Contains("-map 0:0 -c:a aac -ac 2", args, StringComparison.Ordinal);
    }

    /// <summary>An AVI's MPEG-4 (XviD) asked of VideoToolbox fails outright: decoded in software, encoded in hardware.</summary>
    [Fact]
    public void AVideoTheGpuCantDecode_IsDecodedInSoftware_AndStillEncodedInHardware()
    {
        var plan = new TranscodePlan(0, 1, 720, 304, 1_500_000, ToneMap: false, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null, "ac3", Fmp4: true, SourceVideoCodec: "mpeg4");

        var args = FfmpegCommand.Hls("/in.avi", plan, 20, "/out", Settings(HardwareAcceleration.VideoToolbox));

        Assert.DoesNotContain("-hwaccel", args);
        Assert.Contains("scale=720:304,format=yuv420p", args);
        Assert.Contains("h264_videotoolbox", args);

        var hevc = FfmpegCommand.Hls("/in.mkv", plan with { SourceVideoCodec = "hevc" }, 20, "/out", Settings(HardwareAcceleration.VideoToolbox));
        Assert.Contains("-hwaccel", hevc);
        Assert.Contains(hevc, a => a.StartsWith("scale_vt=", StringComparison.Ordinal));
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

    [Fact]
    public void MediaPlaylist_SaysWhereToOpen_OnlyWhenResuming()
    {
        Assert.Contains("#EXT-X-START:TIME-OFFSET=395.000,PRECISE=YES\n", FfmpegCommand.MediaPlaylist(2600, 395), StringComparison.Ordinal);
        Assert.DoesNotContain("EXT-X-START", FfmpegCommand.MediaPlaylist(2600), StringComparison.Ordinal);
        Assert.DoesNotContain("EXT-X-START", FfmpegCommand.MediaPlaylist(2600, 2700), StringComparison.Ordinal);
    }

    [Fact]
    public void MediaPlaylist_InFragmentedMp4_PointsAtTheInitSegmentAndM4sSegments()
    {
        var playlist = FfmpegCommand.MediaPlaylist(20, fmp4: true);

        Assert.StartsWith("#EXTM3U\n#EXT-X-VERSION:7\n", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4\"\n", playlist, StringComparison.Ordinal);
        Assert.Contains("#EXTINF:6.000,\n0.m4s", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain(".ts", playlist, StringComparison.Ordinal);
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
    [InlineData("eng", "iTunes (SDH)", false, "English (SDH)")]
    [InlineData("eng", "English SDH", false, "English (SDH)")]
    [InlineData("eng", "iTunes (SDH) v2", false, "English (SDH)")]
    public void Label(string? code, string? title, bool forced, string expected) =>
        Assert.Equal(expected, Flexarr.Streaming.Languages.Label(code, title, forced));
}
