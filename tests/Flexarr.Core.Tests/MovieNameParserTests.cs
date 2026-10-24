using Flexarr.Core.Parsing;

namespace Flexarr.Core.Tests;

public class MovieNameParserTests
{
    [Theory]
    [InlineData("The Matrix (1999)", "The Matrix", 1999)]
    [InlineData("The Matrix (1999) [Bluray-1080p].mkv", "The Matrix", 1999)]
    [InlineData("/movies/Blade Runner 2049 (2017)/", "Blade Runner 2049", 2017)]
    [InlineData("Arrival.2016.2160p.UHD.mkv", "Arrival", 2016)]
    [InlineData("Mr. Nobody (2009)", "Mr. Nobody", 2009)]
    [InlineData("1917 (2019)", "1917", 2019)]
    [InlineData("Some Home Video", "Some Home Video", null)]
    public void Parse_ExtractsTitleAndYear(string input, string expectedTitle, int? expectedYear)
    {
        var result = MovieNameParser.Parse(input);

        Assert.Equal(expectedTitle, result.Title);
        Assert.Equal(expectedYear, result.Year);
    }
}
