using Flexarr.Core.Media;
using Flexarr.Core.Probing;

namespace Flexarr.Core.Tests;

public class FfprobeMediaProbeTests
{
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
