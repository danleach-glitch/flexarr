using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class TvNameParserTests
{
    [Theory]
    [InlineData("A Man on the Inside (2024) - S01E02 - The Man Who Knew Too Much About Bridges [WEBRip-1080p][AAC 5.1][h265].mp4", 1, 2, null, "The Man Who Knew Too Much About Bridges")]
    [InlineData("30 Rock (2006) - S06E06-E07 - Hey Baby Whats Wrong [HDTV-720p][AC3 5.1][h264].mkv", 6, 6, 7, "Hey Baby Whats Wrong")]
    [InlineData("Arrested Development (2003) - S01E08-E13 - My Mother The Car...Beef Consomme [SDTV][AAC 2.0][x264].mkv", 1, 8, 13, "My Mother The Car...Beef Consomme")]
    [InlineData("Celebrity Gogglebox (2019) - S07E01 - Episode 1 [WEBDL-1080p][AAC 2.0][x264].mkv", 7, 1, null, "Episode 1")]
    [InlineData("show.name.s02e05.720p.hdtv.x264-grp.mkv", 2, 5, null, null)]
    [InlineData("Show S01E01E02E03.mkv", 1, 1, 3, null)]
    [InlineData("Spaced 1x07 Ends.avi", 1, 7, null, "Ends")]
    public void ParseEpisode_SeasonEpisodeForms(string file, int season, int episode, int? end, string? title)
    {
        var parsed = TvNameParser.ParseEpisode(file);

        Assert.NotNull(parsed);
        Assert.Equal(season, parsed.Season);
        Assert.Equal(episode, parsed.Episode);
        Assert.Equal(end, parsed.EpisodeEnd);
        Assert.Equal(title, parsed.Title);
    }

    [Fact]
    public void ParseEpisode_SpecialsWithBareNumber()
    {
        var parsed = TvNameParser.ParseEpisode(
            "Only Fools And Horses 03 Thicker Than Water 1983 DVDRip HEVC H265.mp4",
            seasonFromFolder: 0,
            seriesTitle: "Only Fools and Horses");

        Assert.Equal(new ParsedEpisode(0, 3, null, "Thicker Than Water", null), parsed);
    }

    [Fact]
    public void ParseEpisode_Daily()
    {
        var parsed = TvNameParser.ParseEpisode("The Daily Show - 2026-10-23 - Guest Name [WEBDL-1080p].mkv");

        Assert.Equal(new DateOnly(2026, 10, 23), parsed?.AirDate);
        Assert.Equal(2026, parsed?.Season);
    }

    [Fact]
    public void ParseEpisode_ExtrasAreNotEpisodes() =>
        Assert.Null(TvNameParser.ParseEpisode("Season 2 Bloopers - Arrested Development Season 2.mkv"));

    [Theory]
    [InlineData("Celebrity Gogglebox (2019) {tvdb-365180}", "Celebrity Gogglebox", 2019, "365180")]
    [InlineData("Mo (2022)", "Mo", 2022, null)]
    [InlineData("30 Rock", "30 Rock", null, null)]
    [InlineData("Trial & Error", "Trial & Error", null, null)]
    public void ParseSeriesFolder(string folder, string title, int? year, string? tvdb)
    {
        var parsed = TvNameParser.ParseSeriesFolder(folder);

        Assert.Equal(title, parsed.Title);
        Assert.Equal(year, parsed.Year);
        Assert.Equal(tvdb, parsed.Ids.TvdbId);
    }

    [Theory]
    [InlineData("Season 01", 1)]
    [InlineData("Season 12", 12)]
    [InlineData("Specials", 0)]
    [InlineData("Season 1 EXTRAS", 1)]
    [InlineData("Featurettes", null)]
    public void ParseSeasonFolder(string folder, int? season) =>
        Assert.Equal(season, TvNameParser.ParseSeasonFolder(folder));
}
