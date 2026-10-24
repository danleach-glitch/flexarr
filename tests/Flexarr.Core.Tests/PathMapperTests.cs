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
    public void Map_RewritesMatchingPrefix(string input, string expected) =>
        Assert.Equal(expected, PathMapper.Map(input, Mappings));

    [Theory]
    [InlineData("/Volumes/Media/data/tv/Shōgun", "/data/tv/Shōgun")]
    [InlineData("/Volumes/TV/30 Rock", "/data/tv/30 Rock")]
    [InlineData("/Volumes/Media/database", "/Volumes/Media/database")] // only whole path segments match
    [InlineData("/other/path", "/other/path")]
    public void Unmap_ReversesTheMapping(string input, string expected) =>
        Assert.Equal(expected, PathMapper.Unmap(input, Mappings));
}
