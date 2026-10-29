using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

public class DiscoverRowTests
{
    [Fact]
    public void PopularShows_KeepToShowsThatStartedInTheLastYear_UpToToday()
    {
        Assert.Equal("firstAirDateGte=2025-10-29&firstAirDateLte=2026-10-29", RequestEndpoints.FilterFor("tv", new DateOnly(2026, 10, 29)));
    }

    [Theory]
    [InlineData("trending")]
    [InlineData("movies")]
    [InlineData("upcoming")]
    public void OtherRows_AreUnfiltered(string id)
    {
        Assert.Equal("", RequestEndpoints.FilterFor(id, new DateOnly(2026, 10, 29)));
    }
}
