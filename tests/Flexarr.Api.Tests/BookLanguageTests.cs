using Flexarr.Api.Books;

namespace Flexarr.Api.Tests;

public class BookLanguageTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("eng")]
    [InlineData("English")]
    public void OneLanguage_MatchesItsCodesAndNames(string asked)
    {
        var names = BookQueries.LanguageNames(asked);

        Assert.Contains("en", names);
        Assert.Contains("eng", names);
        Assert.Contains("english", names);
    }

    [Theory]
    [InlineData("ger", "de", "deu", "ger", "german", "deutsch")]
    [InlineData("deu", "de", "deu", "ger", "german", "deutsch")]
    [InlineData("German", "de", "deu", "ger", "german", "deutsch")]
    [InlineData("FRE", "fr", "fra", "fre", "french", "français")]
    [InlineData("chi", "zh", "zho", "chi")]
    [InlineData("wel", "cy", "cym", "wel", "welsh")]
    public void OlderBibliographicCodes_AreTheSameLanguage(string asked, params string[] expected)
    {
        var names = BookQueries.LanguageNames(asked);

        foreach (var name in expected)
        {
            Assert.Contains(name, names);
        }
    }

    [Fact]
    public void AnUnknownTag_MatchesOnlyItself() =>
        Assert.Equal(["klingonese"], BookQueries.LanguageNames("Klingonese"));
}
