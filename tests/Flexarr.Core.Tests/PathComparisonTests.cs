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

    [Theory]
    [InlineData("/tv/Show\u2028x")]   // line separator
    [InlineData("/tv/Show\u2029x")]   // paragraph separator
    [InlineData("/tv/Show\u202Ex")]   // right-to-left override
    [InlineData("/tv/Show\u2067x")]   // right-to-left isolate
    public void UnicodeLineAndBidiControls_AreRefused_AndNotLogged(string path)
    {
        Assert.True(PathComparison.HasControlCharacters(path));
        Assert.Equal("/tv/Show?x", PathComparison.Printable(path));
    }

    [Fact]
    public void Printable_HidesFormattingCharacters_ButKeepsJoiners()
    {
        Assert.Equal("/tv/A?B?C", PathComparison.Printable("/tv/A\u200EB\uFEFFC"));
        Assert.Equal("/music/\U0001F469\u200D\U0001F4BB/\u0645\u06CC\u200C\u062E\u0648\u0627\u0645", PathComparison.Printable("/music/\U0001F469\u200D\U0001F4BB/\u0645\u06CC\u200C\u062E\u0648\u0627\u0645"));
        Assert.False(PathComparison.HasControlCharacters("/music/\U0001F469\u200D\U0001F4BB"));
    }

    [Fact]
    public void AFileOnDisk_WithAFormattingCharacter_StillCompares() =>
        Assert.True(PathComparison.IsUnder("/tv/Show\u200E/S01E01.mkv", "/tv"));
}
