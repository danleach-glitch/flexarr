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

    [Theory]
    [InlineData(100, true)]
    [InlineData(888, true)]
    [InlineData(899, true)]
    [InlineData(99, false)]
    [InlineData(900, false)]
    [InlineData(1888, false)]
    [InlineData(0, false)]
    public void TeletextPages_AreThreeDigits_FromMagazineOneToEight(int page, bool valid) =>
        Assert.Equal(valid, Subtitles.IsTeletextPage(page));

    [Fact]
    public void Teletext_ReadsTheSubtitlePages_UnlessAPageIsChosen()
    {
        Assert.Equal(["-txt_format", "text", "-txt_page", "subtitle"], Subtitles.TeletextOptions());
        Assert.Equal(["-txt_format", "text", "-txt_page", "888"], Subtitles.TeletextOptions(888));
    }

    [Fact]
    public async Task AChosenTeletextPage_IsReadOnItsOwn_AndKeptApartFromTheUsualSubtitles()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var cache = Directory.CreateTempSubdirectory("flexarr-teletext-").FullName;
        try
        {
            // Stands in for ffmpeg: notes what it was asked, and writes the WebVTT it was asked for.
            var ffmpeg = Path.Combine(cache, "fake-ffmpeg");
            var asked = Path.Combine(cache, "asked");
            System.IO.File.WriteAllText(ffmpeg, $"""
                #!/bin/sh
                echo "$*" >> '{asked}'
                for last; do :; done
                printf 'WEBVTT\n' > "$last"
                """);
            System.IO.File.SetUnixFileMode(ffmpeg, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var settings = new TranscodeSettings { CacheDirectory = cache, FfmpegPath = ffmpeg };
            var file = File(false, Stream(0, StreamType.Video, "mpeg2video"), Stream(3, StreamType.Subtitle, "dvb_teletext", "eng"));
            var track = Assert.Single(Subtitles.For(file, teletextReadable: true));

            var path = await Subtitles.TeletextPageToWebVttAsync(file, track, 888, settings, CancellationToken.None);

            Assert.NotNull(path);
            Assert.NotEqual(Subtitles.CachePath(file, track, settings), path);
            Assert.Contains("-txt_page 888 -i /nowhere/Film (2020).mkv", System.IO.File.ReadAllText(asked), StringComparison.Ordinal);
            Assert.Contains("-map 0:3 -c:s webvtt", System.IO.File.ReadAllText(asked), StringComparison.Ordinal);

            // Asked again, it's the copy already made.
            Assert.Equal(path, await Subtitles.TeletextPageToWebVttAsync(file, track, 888, settings, CancellationToken.None));
            Assert.Single(System.IO.File.ReadAllLines(asked));

            await Assert.ThrowsAsync<ArgumentException>(() => Subtitles.TeletextPageToWebVttAsync(file, track, 999, settings, CancellationToken.None));
        }
        finally
        {
            Directory.Delete(cache, recursive: true);
        }
    }
}
