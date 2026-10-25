using System.Globalization;
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

    // Titles that really contain dots keep them; only scene-style names are un-dotted.
    [Theory]
    [InlineData("Arrested Development (2003) - S03E09 - S.O.B.s [HDTV-720p][AC3 5.1][x264].mkv", "S.O.B.s")]
    [InlineData("Breaking Bad (2008) - S03E03 - I.F.T [WEBDL-1080p][EAC3 5.1][x264].mkv", "I.F.T")]
    [InlineData("House of Lies (2012) - S02E04 - Damonschildren.org [SDTV][AAC 2.0][x264].mkv", "Damonschildren.org")]
    public void ParseEpisode_KeepsDotsThatBelongInTheTitle(string file, string title) =>
        Assert.Equal(title, TvNameParser.ParseEpisode(file)?.Title);

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

    [Fact]
    public void SplitTrailingYear_TakesTheDisambiguatingYearOffAShowsName()
    {
        Assert.Equal(("Archer", 2009), TvNameParser.SplitTrailingYear("Archer (2009)"));
        Assert.Equal(("Dark Matter", 2024), TvNameParser.SplitTrailingYear("Dark Matter (2024) "));
        Assert.Equal(("Archer", (int?)null), TvNameParser.SplitTrailingYear("Archer"));
        Assert.Equal(("1899", (int?)null), TvNameParser.SplitTrailingYear("1899"));
        Assert.Equal(("Catch-22 (miniseries)", (int?)null), TvNameParser.SplitTrailingYear("Catch-22 (miniseries)"));
    }

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
