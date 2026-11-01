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

    [Fact]
    public void AnUnknownTag_MatchesOnlyItself() =>
        Assert.Equal(["klingonese"], BookQueries.LanguageNames("Klingonese"));
}
