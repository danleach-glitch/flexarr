using Flexarr.Core.Media;
using Flexarr.Core.Probing;

namespace Flexarr.Core.Tests;

public class FfprobeMediaProbeTests
{
    [Fact]
    public async Task ACancelledProbe_StopsFfprobe()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffprobe stuck on a share that's stopped answering: it notes its process id, then hangs.
        var dir = Directory.CreateTempSubdirectory("flexarr-probe-").FullName;
        try
        {
            var pidFile = Path.Combine(dir, "pid");
            var ffprobe = Path.Combine(dir, "ffprobe");
            File.WriteAllText(ffprobe, $"#!/bin/sh\necho $$ > '{pidFile}'\nexec sleep 30\n");
            File.SetUnixFileMode(ffprobe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            using var cancel = new CancellationTokenSource();
            var probing = new FfprobeMediaProbe(ffprobe).ProbeAsync("/film.mkv", cancel.Token);
            for (var i = 0; i < 100 && !File.Exists(pidFile); i++)
            {
                await Task.Delay(20);
            }

            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probing);
            var pid = int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture);
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            Assert.True(process.WaitForExit(5000), "ffprobe was left running");
        }
        catch (ArgumentException)
        {
            // GetProcessById: already gone, as it should be.
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Parse_RealHevcAacFile()
    {
        var result = FfprobeMediaProbe.Parse(File.ReadAllText(Path.Combine("Fixtures", "ffprobe-hevc-aac.json")));

        Assert.Equal("mp4", result.Container);
        Assert.True(result.DurationSeconds > 60);
        Assert.Equal(VideoRange.Sdr, result.VideoRange);
        var video = Assert.Single(result.Streams, s => s.Type == StreamType.Video);
        Assert.Equal(("hevc", 1920), (video.Codec, video.Width));
        Assert.Equal(6, Assert.Single(result.Streams, s => s.Type == StreamType.Audio).Channels);
    }

    [Theory]
    [InlineData("""{"color_transfer":"smpte2084"}""", VideoRange.Hdr10)]
    [InlineData("""{"color_transfer":"arib-std-b67"}""", VideoRange.Hlg)]
    [InlineData("""{"color_transfer":"smpte2084","side_data_list":[{"side_data_type":"DOVI configuration record"}]}""", VideoRange.DolbyVision)]
    [InlineData("""{"color_transfer":"bt709"}""", VideoRange.Sdr)]
    public void Parse_DetectsVideoRange(string extra, VideoRange expected)
    {
        var stream = """{"index":0,"codec_type":"video","codec_name":"hevc",""" + extra.TrimStart('{');
        var json = $$"""{"format":{"format_name":"matroska,webm"},"streams":[{{stream}}]}""";

        var result = FfprobeMediaProbe.Parse(json);

        Assert.Equal(expected, result.VideoRange);
        Assert.Equal("mkv", result.Container);
    }

    [Theory]
    [InlineData("""{"side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":5}]}""", 5)]
    [InlineData("""{"side_data_list":[{"side_data_type":"DOVI configuration record"}]}""", 0)]
    [InlineData("""{"color_transfer":"smpte2084"}""", null)]
    public void Parse_KeepsTheDolbyVisionProfile(string extra, int? expected)
    {
        var stream = """{"index":0,"codec_type":"video","codec_name":"hevc",""" + extra.TrimStart('{');
        Assert.Equal(expected, FfprobeMediaProbe.Parse($$"""{"format":{"format_name":"matroska,webm"},"streams":[{{stream}}]}""").DolbyVisionProfile);
    }

    [Theory]
    [InlineData("""{"frames":[{"side_data_list":[{"side_data_type":"Mastering display metadata"},{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)","application_version":1}]}]}""", true)]
    [InlineData("""{"frames":[{"side_data_list":[{"side_data_type":"Mastering display metadata"},{"side_data_type":"Content light level metadata"}]},{}]}""", false)]
    [InlineData("""{"frames":[]}""", false)]
    [InlineData("""{}""", false)]
    public void HasSideData_FindsHdr10PlusInTheFirstFrames(string json, bool expected) =>
        Assert.Equal(expected, FfprobeMediaProbe.HasSideData(json, "2094-40"));

    [Theory]
    [InlineData("""{"frames":[{"side_data_list":[{"side_data_type":"ATSC A53 Part 4 Closed Captions"}]}]}""", true)]
    [InlineData("""{"frames":[{"side_data_list":[{"side_data_type":"Mastering display metadata"}]},{}]}""", false)]
    public void HasSideData_FindsClosedCaptionsInTheFirstFrames(string json, bool expected) =>
        Assert.Equal(expected, FfprobeMediaProbe.HasSideData(json, "A53"));

    [Theory]
    [InlineData("h264", true)]
    [InlineData("mpeg2video", true)]
    [InlineData("hevc", true)]
    [InlineData("av1", false)]
    [InlineData(null, false)]
    public void CanCarryCaptions_OnlyBroadcastCodecs(string? codec, bool expected) =>
        Assert.Equal(expected, FfprobeMediaProbe.CanCarryCaptions(codec));

    /// <summary>
    /// Runs <see cref="FfprobeMediaProbe.ProbeAsync"/> against a stand-in ffprobe that answers <paramref name="all"/>
    /// (stream details, and frames when asked) or, asked for the video's frames alone, <paramref name="videoOnly"/>, and
    /// gives what came back with how many times ffprobe ran.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task<(ProbeResult Result, string[] Runs)> ProbeWithStandInAsync(string file, string all, string videoOnly)
    {
        var dir = Directory.CreateTempSubdirectory("flexarr-probe-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(dir, "all.json"), all);
            File.WriteAllText(Path.Combine(dir, "video.json"), videoOnly);
            var log = Path.Combine(dir, "runs");
            var ffprobe = Path.Combine(dir, "ffprobe");
            File.WriteAllText(ffprobe, $$"""
                #!/bin/sh
                echo "$*" >> '{{log}}'
                case "$*" in *-select_streams*) cat '{{dir}}/video.json' ;; *) cat '{{dir}}/all.json' ;; esac
                """);
            File.SetUnixFileMode(ffprobe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var result = await new FfprobeMediaProbe(ffprobe).ProbeAsync(file, default);
            return (result, File.ReadAllLines(log));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private const string HdrStreams = """
        "format":{"format_name":"matroska,webm"},
        "streams":[{"index":0,"codec_type":"video","codec_name":"hevc","width":3840,"color_transfer":"smpte2084","color_primaries":"bt2020"},
          {"index":1,"codec_type":"audio","codec_name":"eac3","channels":6}]
        """;

    private const string Hdr10PlusFrame = """{"media_type":"video","side_data_list":[{"side_data_type":"HDR Dynamic Metadata SMPTE2094-40 (HDR10+)"}]}""";

    [Fact]
    public async Task AVideo_IsProbedOnce_WithItsFirstFrames()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (result, runs) = await ProbeWithStandInAsync("/films/Dune.mkv", $$"""{{{HdrStreams}},"frames":[{"media_type":"audio"},{{Hdr10PlusFrame}}]}""", "{}");

        var run = Assert.Single(runs);
        Assert.Contains("-show_streams", run, StringComparison.Ordinal);
        Assert.Contains("-show_frames", run, StringComparison.Ordinal);
        Assert.Equal(VideoRange.Hdr10, result.VideoRange);
        Assert.True(result.Hdr10Plus);
        Assert.False(result.ClosedCaptions);
    }

    [Fact]
    public async Task AVideo_WhoseFirstPacketsAreAllAudio_HasItsOwnFramesReadAfter()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (result, runs) = await ProbeWithStandInAsync("/films/Dune.mkv", $$"""{{{HdrStreams}},"frames":[{"media_type":"audio"}]}""", $$"""{"frames":[{{Hdr10PlusFrame}}]}""");

        Assert.Equal(2, runs.Length);
        Assert.Contains("-select_streams v:0", runs[1], StringComparison.Ordinal);
        Assert.True(result.Hdr10Plus);
    }

    [Fact]
    public async Task Music_IsProbedForItsStreamsAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var (result, runs) = await ProbeWithStandInAsync("/music/Track.flac", """{"streams":[{"index":0,"codec_type":"audio","codec_name":"flac","channels":2}]}""", "{}");

        Assert.DoesNotContain("-show_frames", Assert.Single(runs), StringComparison.Ordinal);
        Assert.Equal(StreamType.Audio, Assert.Single(result.Streams).Type);
    }

    [Fact]
    public void Parse_SkipsEmbeddedCoverArt()
    {
        const string json = """
            {"streams":[
              {"index":0,"codec_type":"video","codec_name":"h264","width":1280},
              {"index":1,"codec_type":"video","codec_name":"mjpeg","disposition":{"attached_pic":1}},
              {"index":2,"codec_type":"subtitle","codec_name":"subrip","tags":{"language":"eng"},"disposition":{"forced":1}}
            ]}
            """;

        var result = FfprobeMediaProbe.Parse(json);

        Assert.Equal([StreamType.Video, StreamType.Subtitle], result.Streams.Select(s => s.Type));
        Assert.True(result.Streams[1].IsForced);
        Assert.Equal("eng", result.Streams[1].Language);
    }
}
