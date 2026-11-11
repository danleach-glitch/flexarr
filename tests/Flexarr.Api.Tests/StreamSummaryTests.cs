using Flexarr.Api.Playback;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;

namespace Flexarr.Api.Tests;

public class StreamSummaryTests
{
    [Theory]
    [InlineData(VideoRange.Hdr10, false, false, "Original HEVC (HDR10), repackaged")]
    [InlineData(VideoRange.Hdr10, true, false, "Original HEVC (HDR10+), repackaged")]
    [InlineData(VideoRange.DolbyVision, true, false, "Original HEVC (Dolby Vision, HDR10+), repackaged")]
    [InlineData(VideoRange.DolbyVision, true, true, "Original HEVC (HDR10+), repackaged")] // Profile 7: its HDR10 base layer.
    [InlineData(VideoRange.DolbyVision, false, true, "Original HEVC (HDR10), repackaged")]
    public void RepackagedVideo_NamesHdr10Plus_WhichTravelsWithTheHdr10(VideoRange range, bool hdr10Plus, bool stripDolbyVision, string expected)
    {
        var plan = new TranscodePlan(0, 1, 3840, 2160, 0, ToneMap: false, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null)
        {
            Repackage = new Repackage([0], "hvc1", "hvc1.2.4.L153.B0", null, "PQ", stripDolbyVision),
        };
        var file = new MediaFile { Path = "/m.mkv", VideoCodec = "hevc", VideoRange = range, Hdr10Plus = hdr10Plus };

        Assert.Equal(expected, PlaybackService.Summary(plan, file));
    }

    [Fact]
    public void ConvertedVideo_SaysNothingOfHdr10Plus()
    {
        var plan = new TranscodePlan(0, 1, 1920, 1080, 8_000_000, ToneMap: true, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null);
        var file = new MediaFile { Path = "/m.mkv", VideoCodec = "hevc", VideoRange = VideoRange.Hdr10, Hdr10Plus = true };

        Assert.Equal("1080p H.264 at 8 Mbps, HDR converted to SDR", PlaybackService.Summary(plan, file));
    }

    [Theory]
    [InlineData(VideoRange.Hdr10, false, false, "HDR10", false)]
    [InlineData(VideoRange.DolbyVision, true, false, "DolbyVision", true)]
    [InlineData(VideoRange.DolbyVision, true, true, "HDR10", true)]
    [InlineData(VideoRange.Sdr, false, false, "SDR", false)]
    public void RepackagedVideo_IsDescribedInFields_AsTheSummarySaysIt(VideoRange range, bool hdr10Plus, bool stripDolbyVision, string expected, bool plus)
    {
        var plan = new TranscodePlan(0, 1, 3840, 2160, 0, ToneMap: false, CopyAudio: false, AudioChannels: 8, BurnSubtitle: null, LosslessAudio: true)
        {
            Repackage = new Repackage([0], "hvc1", "hvc1.2.4.L153.B0", null, "PQ", stripDolbyVision),
        };
        var file = new MediaFile { Path = "/m.mkv", VideoCodec = "hevc", VideoRange = range, Hdr10Plus = hdr10Plus };

        Assert.Equal(new StreamSummaryDto(true, "hevc", expected, plus, null, null, false, true, 8), PlaybackService.StreamOf(plan, file));
    }

    [Fact]
    public void ConvertedVideo_IsDescribedInFields()
    {
        var plan = new TranscodePlan(0, 1, 1920, 1080, 8_000_000, ToneMap: true, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null);
        var file = new MediaFile { Path = "/m.mkv", VideoCodec = "hevc", VideoRange = VideoRange.Hdr10, Hdr10Plus = true };

        Assert.Equal(new StreamSummaryDto(false, "h264", null, false, 1080, 8_000_000, true, false, null), PlaybackService.StreamOf(plan, file));
    }
}
