using Flexarr.Core.Paths;

namespace Flexarr.Core.Tests;

public class PathComparisonTests
{
    [Theory]
    [InlineData("/Volumes/Media/TV/Show", "/Volumes/Media/TV", true)]
    [InlineData("/Volumes/Media/TV", "/Volumes/Media/TV/", true)]
    [InlineData("/Volumes/Media/TVShows", "/Volumes/Media/TV", false)]   // whole segments only
    [InlineData("/Volumes/Media/TV/../../../Users/dan", "/Volumes/Media/TV", false)]
    [InlineData("/Volumes/Media/TV/..", "/Volumes/Media/TV", false)]
    [InlineData("/Volumes/Media/TV/Show/../Other", "/Volumes/Media/TV", true)]
    [InlineData("/Volumes/Media/Movies/../TV/Show", "/Volumes/Media/TV", true)]
    [InlineData("/Volumes/Media/TV/./Show", "/Volumes/Media/TV", true)]
    [InlineData("/anything", "/", true)]
    public void IsUnder_ResolvesDotSegments(string path, string root, bool expected) =>
        Assert.Equal(expected, PathComparison.IsUnder(path, root));

    [Fact]
    public void IsUnder_IgnoresUnicodeNormalisation()
    {
        var decomposed = "/tv/Léon";
        var composed = decomposed.Normalize(System.Text.NormalizationForm.FormC);

        Assert.True(PathComparison.IsUnder(decomposed + "/Season 01", composed));
        Assert.True(PathComparison.IsUnder(composed, decomposed));
    }

    [Fact]
    public void IsUnder_RejectsUnusablePaths()
    {
        Assert.False(PathComparison.IsUnder("/tv/Show\0/x", "/tv"));
        Assert.False(PathComparison.IsUnder("/tv/Show\n/x", "/tv"));
    }

    [Fact]
    public void Key_OfAnUnusablePath_IsThePathRatherThanAnException()
    {
        Assert.Equal("/tv/Show\0", PathComparison.Key("/tv/Show\0"));
        Assert.Equal("/tv/Show?x", PathComparison.Printable("/tv/Show\nx"));
    }
}
