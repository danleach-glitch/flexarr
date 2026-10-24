using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

public class LayoutTests
{
    private static HomeRowDto Row(string id) => new(id, id, "poster", []);

    [Fact]
    public void Arrange_FollowsTheSavedOrder_DropsSwitchedOffRows_AndKeepsTheRestInPlace()
    {
        HomeRowDto[] rows = [Row("continue"), Row("recent-a"), Row("top-movies"), Row("genre-Drama"), Row("genre-Crime"), Row("gems"), Row("upcoming")];
        List<RowSetting> layout =
        [
            new() { Id = "upcoming" },
            new() { Id = "genres" },
            new() { Id = "continue" },
            new() { Id = "top-movies", Enabled = false },
        ];

        var ids = LayoutEndpoints.Arrange(rows, layout).Select(r => r.Id);

        // Genre rows move together; rows the layout doesn't mention (a new library, gems) follow in their own order.
        Assert.Equal(["upcoming", "genre-Drama", "genre-Crime", "continue", "recent-a", "gems"], ids);
    }

    [Fact]
    public void Arrange_WithNoLayout_KeepsTheBuiltInOrder()
    {
        HomeRowDto[] rows = [Row("continue"), Row("recent"), Row("top")];

        Assert.Equal(["continue", "recent", "top"], LayoutEndpoints.Arrange(rows, []).Select(r => r.Id));
    }
}
