using System.Globalization;
using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class TvNameParserTests
{
    [Theory]
    [InlineData("Show - S01E02.mkv", 1, 2, null, null)]
    [InlineData("Show.Name.S03E07.1080p.WEB.x264.mkv", 3, 7, null, null)]
    [InlineData("Show - S01E02E03.mkv", 1, 2, 3, null)]
    [InlineData("Show - S01E02-E04.mkv", 1, 2, 4, null)]
    [InlineData("Show S02E11E12E13.mkv", 2, 11, 13, null)]
    [InlineData("Show - 2x05.mkv", 2, 5, null, null)]
    [InlineData("Daily Show - 2026-10-24.mkv", 2026, 0, null, "10/24/2026")]
    [InlineData("Daily.Show.2026.10.24.mp4", 2026, 0, null, "10/24/2026")]
    public void ParseEpisode_ReadsSeasonEpisodeForms(string file, int season, int episode, int? end, string? airDate)
    {
        var parsed = TvNameParser.ParseEpisode(file);

        Assert.NotNull(parsed);
        Assert.Equal(season, parsed!.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(end, parsed.EpisodeEnd);
        Assert.Equal(airDate is null ? null : DateOnly.Parse(airDate, CultureInfo.InvariantCulture), parsed.AirDate);
    }

    [Theory]
    [InlineData("Show - S01E02 - The Beginning.mkv", "The Beginning")]
    [InlineData("Show.S01E02.Episode.Title.1080p.WEB-DL.x264.mkv", "Episode Title")]
    [InlineData("Show - S01E02 - 1994 DVDRip.mkv", null)]
    public void ParseEpisode_KeepsTheEpisodeTitle(string file, string? title)
    {
        var parsed = TvNameParser.ParseEpisode(file);

        Assert.NotNull(parsed);
        Assert.Equal(title, parsed!.Title);
    }

    [Theory]
    [InlineData("Only Fools And Horses 03 Thicker Than Water 1983 DVDRip", "Only Fools And Horses", 3, null)]
    [InlineData("Cowboy Bebop - 26", "Cowboy Bebop", 26, null)]
    [InlineData("One Piece - 1015", "One Piece", 1015, null)]
    [InlineData("One Piece - 1015 [1080p].mkv", "One Piece", 1015, null)]
    public void ParseEpisode_ReadsBareNumbersAfterTheTitle(string file, string title, int episode, int? end)
    {
        var parsed = TvNameParser.ParseEpisode(file, seasonFromFolder: 1, seriesTitle: title);

        Assert.NotNull(parsed);
        Assert.Equal(1, parsed!.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(end, parsed.EpisodeEnd);
    }

    [Fact]
    public void ParseEpisode_ReturnsNullForNamesWithNoEpisode()
    {
        Assert.Null(TvNameParser.ParseEpisode("Season 1/Show.mkv"));
        Assert.Null(TvNameParser.ParseEpisode("random junk.mkv", seasonFromFolder: null, seriesTitle: "Show"));
    }

    [Theory]
    [InlineData("Season 02", 2)]
    [InlineData("Season 1", 1)]
    [InlineData("Specials", 0)]
    [InlineData("Extras", null)]
    public void ParseSeasonFolder_ReadsSeasonFolders(string folder, int? season) =>
        Assert.Equal(season, TvNameParser.ParseSeasonFolder(folder));

    [Theory]
    [InlineData("The Show (2019)", "The Show", 2019)]
    [InlineData("The Show {tvdb-123}", "The Show", null)]
    [InlineData("Show", "Show", null)]
    public void ParseSeriesFolder_ReadsTitleAndYear(string folder, string title, int? year)
    {
        var parsed = TvNameParser.ParseSeriesFolder(folder);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
    }

    [Fact]
    public void ParseSeriesFolder_KeepsExternalIds()
    {
        var parsed = TvNameParser.ParseSeriesFolder("The Show {tvdb-123}");

        Assert.Equal("123", parsed.Ids.TvdbId);
    }
}
