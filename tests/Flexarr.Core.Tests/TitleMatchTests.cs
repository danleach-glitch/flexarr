using Flexarr.Core.Media;
using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

/// <summary>Picking the right result when Sonarr or Radarr search for a folder's name.</summary>
public class TitleMatchTests
{
    private sealed record Result(string Title, int Year);

    // Sonarr's search for "Shogun" ranks the 1980 show first.
    private static readonly Result[] Shoguns = [new("Shogun", 1980), new("Shōgun", 2024), new("Shotgun Slade", 1959)];

    private static MediaItem Item(string title, int? year = null) => new() { Kind = MediaKind.Series, Title = title, SortTitle = title, Year = year };

    [Theory]
    [InlineData("Shōgun", null, 2024)] // a macOS folder name: decomposed accent
    [InlineData("Shōgun", null, 2024)]
    [InlineData("Shogun", null, 1980)]
    [InlineData("Shogun", 2024, 2024)] // the year decides first
    [InlineData("SHŌGUN", null, 2024)]
    public void PrefersTheSameTitle_AccentsAndAll(string title, int? year, int expected) =>
        Assert.Equal(expected, MetadataRefresher.BestByTitle(Shoguns, Item(title, year), r => r.Title, r => r.Year)!.Year);

    [Fact]
    public void FallsBackToTheSameTitleIgnoringPunctuation() =>
        Assert.Equal("Marvel's Daredevil", MetadataRefresher.BestByTitle(
            [new Result("Daredevil: Born Again", 2025), new Result("Marvel's Daredevil", 2015)], Item("Marvels Daredevil"), r => r.Title, r => r.Year)!.Title);

    [Fact]
    public void OtherwiseTakesTheFirstResultWithinAYear() =>
        Assert.Equal(2015, MetadataRefresher.BestByTitle([new Result("Something Else", 1999), new Result("Another", 2015)], Item("Unknown", 2016), r => r.Title, r => r.Year)!.Year);
}
