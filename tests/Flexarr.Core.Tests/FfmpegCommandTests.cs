using Flexarr.Streaming;
using Flexarr.Streaming.Remux;

namespace Flexarr.Core.Tests;

public class FfmpegCommandTests
{
    private static readonly TranscodePlan Plan = new(0, 1, 1920, 1080, 10_000_000, ToneMap: true, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null, "eac3");

    [Fact]
    public void VideoToolbox_DecodesScalesTonemapsAndEncodesOnTheGpu()
    {
        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 10, "/out", Settings(HardwareAcceleration.VideoToolbox)));

        Assert.Contains("-hwaccel videotoolbox -hwaccel_output_format videotoolbox_vld", args, StringComparison.Ordinal);
        // Segment 10: after three 2-second opening segments, seven of 6 seconds.
        Assert.Contains("-ss 48 -protocol_whitelist file -i /in.mkv", args, StringComparison.Ordinal);
        Assert.Contains("scale_vt=w=1920:h=1080:color_matrix=bt709:color_primaries=bt709:color_transfer=bt709", args, StringComparison.Ordinal);
        Assert.Contains("-c:v h264_videotoolbox", args, StringComparison.Ordinal);
        Assert.Contains("-output_ts_offset 48", args, StringComparison.Ordinal);
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

    [Fact]
    public void SoundShaping_LiftsTheCentreChannel_OrTheVoiceBand_ThenCompressesAndLimits()
    {
        var surround = Plan with { Sound = new SoundShaping(DialogueBoostDb: 6, Night: true), SourceAudioChannels = 6 };
        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", surround, 0, "/out", Settings(HardwareAcceleration.None)));
        Assert.Contains("-af pan=5.1|c0=c0|c1=c1|c2=1.995*c2|c3=c3|c4=c4|c5=c5,acompressor=threshold=-40dB:ratio=8:attack=5:release=250:knee=6:makeup=14dB,alimiter=limit=-1dB:level=false -c:a aac -ac 2",
            args, StringComparison.Ordinal);

        var stereo = Plan with { Sound = new SoundShaping(DialogueBoostDb: 9), SourceAudioChannels = 2 };
        Assert.Equal(["-af", "equalizer=f=2000:t=o:w=1.5:g=9,alimiter=limit=-1dB:level=false"], FfmpegCommand.SoundFilter(stereo));

        Assert.Empty(FfmpegCommand.SoundFilter(Plan));
        Assert.DoesNotContain("-af", string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", Settings(HardwareAcceleration.None))), StringComparison.Ordinal);
    }

    [Fact]
    public void FromTheStart_AConvertedVideoOpensWithShortSegments_ThenEvenOnes()
    {
        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", Settings(HardwareAcceleration.VideoToolbox)));
        Assert.Contains("-force_key_frames expr:if(lt(n_forced,3),gte(t,n_forced*2),gte(t,6+(n_forced-3)*6))", args, StringComparison.Ordinal);
        Assert.Contains("-hls_time 2 ", args, StringComparison.Ordinal);

        // Within the opening, ffmpeg starts again from the beginning.
        var second = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 2, "/out", Settings(HardwareAcceleration.VideoToolbox)));
        Assert.Contains("-ss 0 ", second, StringComparison.Ordinal);
        Assert.Contains("-start_number 0 ", second, StringComparison.Ordinal);

        // After it, segments are even, from a 6-second boundary.
        var later = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 3, "/out", Settings(HardwareAcceleration.VideoToolbox)));
        Assert.Contains("-ss 6 ", later, StringComparison.Ordinal);
        Assert.Contains("-hls_time 6 ", later, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*6)", later, StringComparison.Ordinal);

        // Encoders that may add keyframes of their own (Quick Sync, VA-API) keep even segments throughout.
        var qsv = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", Settings(HardwareAcceleration.Qsv)));
        Assert.Contains("-hls_time 6 ", qsv, StringComparison.Ordinal);
        Assert.Contains("-force_key_frames expr:gte(t,n_forced*6)", qsv, StringComparison.Ordinal);

        // Got ready ahead of Play: a short burst.
        var warming = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 0, "/out", Settings(HardwareAcceleration.VideoToolbox), burstSeconds: FfmpegCommand.WarmingRunAheadSeconds));
        Assert.Contains("-readrate_initial_burst 30 ", warming, StringComparison.Ordinal);

        // Music (no picture) is cut on its sound and stays even.
        var music = string.Join(' ', FfmpegCommand.Hls("/in.flac", Plan with { VideoStreamIndex = -1 }, 0, "/out", Settings(HardwareAcceleration.None)));
        Assert.Contains("-hls_time 6 ", music, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1.9, 0)]
    [InlineData(2, 1)]
    [InlineData(5.9, 2)]
    [InlineData(6, 3)]
    [InlineData(11.9, 3)]
    [InlineData(12, 4)]
    [InlineData(395, 67)]
    public void OpeningSegments_LineUpWithWhereTheyStart(double seconds, int segment)
    {
        Assert.Equal(segment, FfmpegCommand.SegmentAtSeconds(seconds, opening: true));
        Assert.True(FfmpegCommand.SegmentStartSeconds(segment, opening: true) <= seconds);
        Assert.True(FfmpegCommand.SegmentStartSeconds(segment + 1, opening: true) > seconds);
    }

    [Fact]
    public void APlaylistWithAnOpening_ListsShortSegmentsFirst_AndAddsUpToTheLength()
    {
        var playlist = FfmpegCommand.MediaPlaylist(20, opening: true);
        var lengths = playlist.Split('\n').Where(l => l.StartsWith("#EXTINF:", StringComparison.Ordinal))
            .Select(l => double.Parse(l[8..^1], System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal([2, 2, 2, 6, 6, 2], lengths);
        Assert.Equal(6, FfmpegCommand.SegmentCount(20, opening: true));
        Assert.Equal(2, FfmpegCommand.SegmentCount(3.5, opening: true));
    }

    [Fact]
    public void SoundOutOfSync_ComesFromTheFileAgain_ReadFromTheDelayBeforeThePicture()
    {
        // Later: the sound that goes with the picture at 48 s (segment 10) is the file's at 47.5 s.
        var later = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { AudioDelaySeconds = 0.5 }, 10, "/out", Settings(HardwareAcceleration.None)));
        Assert.Contains("-itsoffset 0 -readrate 1.0 -readrate_initial_burst 180 -ss 47.5 -protocol_whitelist file -i /in.mkv", later, StringComparison.Ordinal);
        Assert.Contains("-map 1:1", later, StringComparison.Ordinal);
        Assert.DoesNotContain("-map 0:1", later, StringComparison.Ordinal);

        // Earlier, from the start: read ahead of the picture.
        var earlier = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { AudioDelaySeconds = -0.3 }, 0, "/out", Settings(HardwareAcceleration.None)));
        Assert.Contains("-itsoffset 0 -readrate 1.0 -readrate_initial_burst 180 -ss 0.3 -protocol_whitelist file -i /in.mkv", earlier, StringComparison.Ordinal);

        // Later, from the start: there's nothing before the beginning to read, so the sound starts after a pause.
        var fromStart = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { AudioDelaySeconds = 0.5 }, 0, "/out", Settings(HardwareAcceleration.None)));
        Assert.Contains("-itsoffset 0.5 -readrate 1.0 -readrate_initial_burst 180 -ss 0 -protocol_whitelist file -i /in.mkv", fromStart, StringComparison.Ordinal);

        var inSync = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 10, "/out", Settings(HardwareAcceleration.None)));
        Assert.DoesNotContain("-itsoffset", inSync, StringComparison.Ordinal);
        Assert.Contains("-map 0:1", inSync, StringComparison.Ordinal);
    }

    [Fact]
    public void SoundOutOfSync_InARepackagedVideo_KeepsTheFilesTimelineMovedByTheDelay()
    {
        var repackage = new Repackage([0, 4, 8.5], "hvc1", "hvc1.2.4.L150.B0", null, "PQ", StripDolbyVision: false);
        var plan = Plan with { Repackage = repackage, AudioDelaySeconds = -0.25, CopyAudio = true };

        var args = string.Join(' ', FfmpegCommand.Hls("/in.mkv", plan, 2, "/out", Settings(HardwareAcceleration.None)));

        Assert.Contains("-itsoffset -0.25 -readrate 1.0 -readrate_initial_burst 180 -ss 8.75 -protocol_whitelist file -i /in.mkv", args, StringComparison.Ordinal);
        Assert.Contains("-map 1:1 -c:a copy", args, StringComparison.Ordinal);
    }

    [Fact]
    public void AStreamWatchedFaster_IsMadeThatFast_AndNeverSlowerThanRealTime()
    {
        string Made(TranscodePlan plan, double speed) => string.Join(' ', FfmpegCommand.Hls("/in.mkv", plan, 10, "/out", Settings(HardwareAcceleration.None), speed: speed));

        // Nothing said, normal speed, slower, or nonsense: real time, written as it always was.
        Assert.Contains("-readrate 1.0 -readrate_initial_burst 180 ", string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan, 10, "/out", Settings(HardwareAcceleration.None))), StringComparison.Ordinal);
        Assert.Contains("-readrate 1.0 -readrate_initial_burst 180 ", Made(Plan, 1), StringComparison.Ordinal);
        Assert.Contains("-readrate 1.0 -readrate_initial_burst 180 ", Made(Plan, 0.5), StringComparison.Ordinal);
        Assert.Contains("-readrate 1.0 -readrate_initial_burst 180 ", Made(Plan, double.NaN), StringComparison.Ordinal);

        Assert.Contains("-readrate 1.5 -readrate_initial_burst 180 ", Made(Plan, 1.5), StringComparison.Ordinal);
        Assert.Contains("-readrate 1.25 -readrate_initial_burst 180 ", Made(Plan, 1.25), StringComparison.Ordinal);
        Assert.Contains("-readrate 2 -readrate_initial_burst 180 ", Made(Plan, 2), StringComparison.Ordinal);
        Assert.Contains("-readrate 4 -readrate_initial_burst 180 ", Made(Plan, 9), StringComparison.Ordinal); // no faster than players play

        // The sound's own input, when it's out of sync, keeps pace with the picture's.
        var shifted = Made(Plan with { AudioDelaySeconds = 0.5 }, 1.5);
        Assert.Contains("-readrate 1.5 -readrate_initial_burst 180 -ss 48 ", shifted, StringComparison.Ordinal);
        Assert.Contains("-itsoffset 0 -readrate 1.5 -readrate_initial_burst 180 -ss 47.5 ", shifted, StringComparison.Ordinal);
        Assert.DoesNotContain("-readrate 1.0", shifted, StringComparison.Ordinal);

        // A video repackaged as it is, too.
        var repackage = new Repackage([0, 4, 8.5], "hvc1", "hvc1.2.4.L150.B0", null, "PQ", StripDolbyVision: false);
        var copied = string.Join(' ', FfmpegCommand.Hls("/in.mkv", Plan with { Repackage = repackage, CopyAudio = true }, 2, "/out", Settings(HardwareAcceleration.None), speed: 2));
        Assert.Contains("-readrate 2 -readrate_initial_burst 180 -ss 8.5 ", copied, StringComparison.Ordinal);
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
