using Flexarr.Core.Media;
using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class VersionRulesTests
{
    private static MediaFile File(string path = "/m/Film (2020)/Film (2020).mkv", int? width = 1920, int? height = 1080, double? duration = 7200,
        VideoRange range = VideoRange.Sdr, long? bitrate = 8_000_000, bool? hdr10Plus = null) =>
        new() { Path = path, Width = width, Height = height, DurationSeconds = duration, VideoRange = range, Bitrate = bitrate, Hdr10Plus = hdr10Plus };

    [Theory]
    [InlineData(3840, 2160, VideoRange.Sdr, null, "4K")]
    [InlineData(3840, 1600, VideoRange.Hdr10, null, "4K HDR10")] // An ultrawide isn't "1600p".
    [InlineData(1920, 804, VideoRange.Sdr, null, "1080p")]
    [InlineData(1280, 720, VideoRange.Hlg, null, "720p HLG")]
    [InlineData(3840, 2160, VideoRange.DolbyVision, null, "4K Dolby Vision")]
    [InlineData(3840, 2160, VideoRange.DolbyVision, true, "4K Dolby Vision HDR10+")]
    [InlineData(3840, 2160, VideoRange.Hdr10, true, "4K HDR10+")]
    public void Label_NamesThePicture(int width, int height, VideoRange range, bool? plus, string expected) =>
        Assert.Equal(expected, VersionRules.Label(File(width: width, height: height, range: range, hdr10Plus: plus)));

    [Fact]
    public void SameCut_AllowsAFrameRatesDifference() =>
        Assert.True(VersionRules.SameCut(File(duration: 7200), File(duration: 6900))); // PAL runs 4% shorter.

    [Fact]
    public void SameCut_SeparatesARealDifferenceInLength() =>
        Assert.False(VersionRules.SameCut(File(duration: 7200), File(duration: 8400)));

    [Fact]
    public void SameCut_AllowsAMinuteOnAShortEpisode() =>
        Assert.True(VersionRules.SameCut(File(duration: 1320), File(duration: 1270)));

    [Fact]
    public void SameCut_AnUnknownLengthDoesNotSeparate() =>
        Assert.True(VersionRules.SameCut(File(duration: null), File(duration: 7200)));

    [Fact]
    public void SameCut_EditionsApartAreDifferentCuts_HoweverClose() =>
        Assert.False(VersionRules.SameCut(File("/m/Film (2020) {edition-Extended}.mkv"), File("/m/Film (2020).mkv")));

    [Fact]
    public void Distinct_DifferentResolution() =>
        Assert.True(VersionRules.Distinct(File(width: 3840, height: 2160), File()));

    [Fact]
    public void Distinct_DifferentHdrType() =>
        Assert.True(VersionRules.Distinct(File(range: VideoRange.DolbyVision), File(range: VideoRange.Hdr10)));

    [Fact]
    public void Distinct_Hdr10PlusIsItsOwnPicture()
    {
        Assert.True(VersionRules.Distinct(File(range: VideoRange.Hdr10, hdr10Plus: true), File(range: VideoRange.Hdr10)));
        Assert.False(VersionRules.Distinct(File(range: VideoRange.Hdr10, hdr10Plus: false), File(range: VideoRange.Hdr10)));
    }

    [Fact]
    public void Distinct_DifferentCut() =>
        Assert.True(VersionRules.Distinct(File(duration: 9000), File()));

    [Fact]
    public void Distinct_AlikeFilesAreDuplicates_EvenAtDifferentBitrates() =>
        Assert.False(VersionRules.Distinct(File(bitrate: 4_000_000), File(bitrate: 9_000_000)));

    [Fact]
    public void BestFirst_CountsAnUltrawideAs4K()
    {
        var ultrawide = File(width: 3840, height: 1600);
        var hd = File(bitrate: 40_000_000);
        Assert.Same(ultrawide, VersionRules.BestFirst([hd, ultrawide]).First());
    }

    [Theory]
    [InlineData("/m/Film (2020) {edition-Director's Cut} [Bluray-1080p].mkv", "Director's Cut")]
    [InlineData("Film (2020) {EDITION-Remastered}.mkv", "Remastered")]
    [InlineData("Show - S01E02 {edition-Extended}.mkv", "Extended")]
    [InlineData("Film (2020) - Director's Cut [WEBDL-2160p].mkv", "Director's Cut")]
    [InlineData("Film.2020.Directors.Cut.1080p.mkv", "Director's Cut")]
    [InlineData("Film.2020.EXTENDED.2160p.mkv", "Extended")]
    [InlineData("Film (2020) Final Cut.mkv", "Final Cut")]
    [InlineData("Film (2020) IMAX.mkv", "IMAX")]
    public void Edition_IsRead(string path, string expected) => Assert.Equal(expected, EditionParser.Parse(path));

    [Theory]
    [InlineData("/m/Film (2020)/Film (2020) [Bluray-1080p].mkv")]
    [InlineData("Extended Family (2020).mkv")] // Before the year, it's the title.
    [InlineData("Final Cut.mkv")] // No year: it could be the title.
    [InlineData("Show (2019) - S01E02 - The Final Cut.mkv")] // An episode called this.
    [InlineData("/Extended (2020)/Film (2020).mkv")] // Only the file's name counts.
    [InlineData("Film (2020) {edition-}.mkv")]
    public void Edition_IsNotGuessed(string path) => Assert.Null(EditionParser.Parse(path));

    [Theory]
    [InlineData("Film {edition-Director's Cut}", "Film", null)]
    [InlineData("Film (2020) {edition-Director's Cut}", "Film", 2020)]
    public void MovieName_LeavesTheEditionOutOfTheTitle(string input, string title, int? year)
    {
        var parsed = MovieNameParser.Parse(input);
        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }
}
