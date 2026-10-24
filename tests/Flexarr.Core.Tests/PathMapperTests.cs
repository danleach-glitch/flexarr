using Flexarr.Core.Paths;

namespace Flexarr.Core.Tests;

public class PathMapperTests
{
    private static readonly PathMapping[] Mappings =
    [
        new() { From = "/data", To = "/Volumes/Media/data" },
        new() { From = "/data/tv/", To = "/Volumes/TV" },
    ];

    [Theory]
    [InlineData("/data/movies/Dune (2021)", "/Volumes/Media/data/movies/Dune (2021)")]
    [InlineData("/data", "/Volumes/Media/data")]
    [InlineData("/data/tv/30 Rock", "/Volumes/TV/30 Rock")] // longest prefix wins
    [InlineData("/database/x", "/database/x")]               // only whole path segments match
    [InlineData("/other/path", "/other/path")]
    [InlineData("/data//tv/./30 Rock", "/Volumes/TV/30 Rock")]   // matched normalised, so mapped normalised
    [InlineData("/data/tv/../movies/Dune", "/Volumes/Media/data/movies/Dune")]
    public void Map_RewritesMatchingPrefix(string input, string expected) =>
        Assert.Equal(expected, PathMapper.Map(input, Mappings));

    [Fact]
    public void Map_KeepsTheRestsSpelling_WhenTheRootMatchedInAnotherUnicodeForm()
    {
        var decomposed = "/dátá".Normalize(System.Text.NormalizationForm.FormD);
        var mappings = new[] { new PathMapping { From = "/dátá".Normalize(System.Text.NormalizationForm.FormC), To = "/Volumes/Media" } };

        // Cutting the raw path at From's length would land mid-name: decomposed, it is longer.
        Assert.Equal("/Volumes/Media/Léon", PathMapper.Map($"{decomposed}/Léon", mappings));
    }

    [Fact]
    public void Map_LeavesUnusablePathsAlone() =>
        Assert.Equal("/data/x\0y", PathMapper.Map("/data/x\0y", Mappings));
}
