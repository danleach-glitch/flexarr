using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class SubtitleShiftTests
{
    private const string Vtt = "WEBVTT\n\n1\n00:00:01.000 --> 00:00:02.500\nHello\n\n2\n00:01:00.000 --> 00:01:03.250\n<i>There</i>\n";

    [Fact]
    public void Later_MovesEveryCue()
    {
        var shifted = Subtitles.Shift(Vtt, 1.5);

        Assert.Contains("00:00:02.500 --> 00:00:04.000\nHello", shifted, StringComparison.Ordinal);
        Assert.Contains("00:01:01.500 --> 00:01:04.750\n<i>There</i>", shifted, StringComparison.Ordinal);
        Assert.StartsWith("WEBVTT\n\n1\n", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void Earlier_DropsCuesThatWouldEndBeforeTheStart_AndClampsOnesThatStraddleIt()
    {
        var shifted = Subtitles.Shift(Vtt, -2);

        Assert.Contains("00:00:00.000 --> 00:00:00.500\nHello", shifted, StringComparison.Ordinal);
        Assert.Contains("00:00:58.000 --> 00:01:01.250", shifted, StringComparison.Ordinal);

        Assert.DoesNotContain("Hello", Subtitles.Shift(Vtt, -3), StringComparison.Ordinal);
    }

    [Fact]
    public void ShortTimesAndWindowsLineEnds_AreRead()
    {
        var shifted = Subtitles.Shift("WEBVTT\r\n\r\n00:05.000 --> 00:06.000 line:90%\r\nHi\r\n", 0.25);

        Assert.Contains("00:00:05.250 --> 00:00:06.250 line:90%\nHi", shifted, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDelay_LeavesTheFileAlone() => Assert.Same(Vtt, Subtitles.Shift(Vtt, 0));

    [Fact]
    public void Placed_SetsEveryCuesLine_KeepingItsOtherSettings()
    {
        var vtt = "WEBVTT\n\n00:00:01.000 --> 00:00:02.500 align:start line:0\nSign\n\n00:00:03.000 --> 00:00:04.000\nHello\n";

        var placed = Subtitles.Placed(vtt, Flexarr.Core.Media.SubtitlePosition.Raised);

        Assert.Contains("00:00:01.000 --> 00:00:02.500 align:start line:82%,end\nSign", placed, StringComparison.Ordinal);
        Assert.Contains("00:00:03.000 --> 00:00:04.000 line:82%,end\nHello", placed, StringComparison.Ordinal);
        Assert.Same(vtt, Subtitles.Placed(vtt, Flexarr.Core.Media.SubtitlePosition.Bottom));
    }
}
