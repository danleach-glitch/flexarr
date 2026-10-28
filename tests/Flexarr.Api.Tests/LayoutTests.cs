using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Libraries;

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

    private static readonly Library A = new() { Name = "A", Type = LibraryType.TvShows }, B = new() { Name = "B", Type = LibraryType.TvShows };

    [Fact]
    public void ForCombining_PutsTheCombinedRowWhereTheLibrariesRowsWere_WithTheirSwitch()
    {
        List<RowSetting> layout = [new() { Id = "continue" }, new() { Id = $"recent-{A.Id}", Enabled = false }, new() { Id = $"recent-{B.Id}", Enabled = false }];

        var combined = LayoutEndpoints.ForCombining(layout, [A, B], combine: true);

        Assert.Equal(["continue", "recent-shows", $"recent-{A.Id}", $"recent-{B.Id}"], combined.Select(r => r.Id));
        Assert.False(combined[1].Enabled);
    }

    [Fact]
    public void ForCombining_Off_PutsEachLibrarysRowWhereTheCombinedOneWas()
    {
        List<RowSetting> layout = [new() { Id = "upcoming" }, new() { Id = "recent-shows" }, new() { Id = "continue" }];

        var own = LayoutEndpoints.ForCombining(layout, [A, B], combine: false);

        Assert.Equal(["upcoming", $"recent-{A.Id}", $"recent-{B.Id}", "recent-shows", "continue"], own.Select(r => r.Id));
        Assert.Same(layout[0], own[0]);
    }
}
