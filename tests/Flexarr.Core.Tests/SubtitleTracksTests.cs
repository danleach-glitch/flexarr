using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

public class SubtitleTracksTests
{
    private static MediaFile File(bool? closedCaptions, params MediaStream[] streams) =>
        new() { Path = "/nowhere/Film (2020).mkv", ClosedCaptions = closedCaptions, Streams = [.. streams] };

    private static MediaStream Stream(int index, StreamType type, string codec, string? language = null) =>
        new() { Index = index, Type = type, Codec = codec, Language = language };

    [Fact]
    public void CaptionsInTheVideo_AreATextTrack_InTheProgrammesLanguage()
    {
        var tracks = Subtitles.For(File(true, Stream(0, StreamType.Video, "h264"), Stream(1, StreamType.Audio, "ac3", "eng")));

        var captions = Assert.Single(tracks);
        Assert.Equal(("cc0", 0, "eng", "CC"), (captions.Key, captions.StreamIndex, captions.Language, captions.Title));
        Assert.True(captions.InVideo);
        Assert.True(captions.IsText);
    }

    [Fact]
    public void NoCaptionsTrack_WhenTheVideoHasNoneOrHasntBeenChecked()
    {
        Assert.Empty(Subtitles.For(File(false, Stream(0, StreamType.Video, "h264"))));
        Assert.Empty(Subtitles.For(File(null, Stream(0, StreamType.Video, "h264"))));
    }

    [Fact]
    public void Teletext_IsTextOnlyWhenThisServerCanReadIt()
    {
        var file = File(false, Stream(0, StreamType.Video, "mpeg2video"), Stream(1, StreamType.Subtitle, "dvb_teletext", "deu"));

        Assert.False(Assert.Single(Subtitles.For(file)).IsText);
        Assert.True(Assert.Single(Subtitles.For(file, teletextReadable: true)).IsText);
    }

    [Theory]
    [InlineData("ass", true)]
    [InlineData("ssa", true)]
    [InlineData("subrip", false)]
    public void StyledTracks_AreAssAndSsa(string codec, bool styled) =>
        Assert.Equal(styled, Assert.Single(Subtitles.For(File(false, Stream(2, StreamType.Subtitle, codec)))).IsStyled);
}
