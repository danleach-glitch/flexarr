using Flexarr.Core.Paths;

namespace Flexarr.Core.Tests;

public class PathMappingDetectorTests
{
    private static readonly string[] Ours =
    [
        "/Volumes/Media/TV/Severance (2022)",
        "/Volumes/Media/TV/Andor (2022)",
        "/Volumes/Media/TV/Slow Horses (2022)",
        "/Volumes/Media/TV/The Bear (2022)",
    ];

    [Fact]
    public void Infer_FindsTheMapping_WhenTheFolderNamesDiffer()
    {
        var theirs = new[] { "/tv/Severance (2022)", "/tv/Andor (2022)", "/tv/Slow Horses (2022)", "/tv/The Bear (2022)" };

        var found = PathMappingDetector.Infer("/tv", theirs, Ours);

        Assert.Equal(new DetectedMapping("/tv", "/Volumes/Media/TV", 4, 4), found);
        Assert.True(found!.Certain);
    }

    [Fact]
    public void Infer_SaysItOfTheRoot_WhenMoreOfThePathIsShared()
    {
        var ours = new[] { "/Volumes/Media/data/tv/Andor (2022)", "/Volumes/Media/data/tv/The Bear (2022)" };
        var theirs = new[] { "/data/tv/Andor (2022)", "/data/tv/The Bear (2022)" };

        // Every segment but the root's is shared, so the bare answer would be / → /Volumes/Media.
        var found = PathMappingDetector.Infer("/data/tv", theirs, ours);

        Assert.Equal("/data/tv", found!.From);
        Assert.Equal("/Volumes/Media/data/tv", found.To);
        Assert.False(found.Certain); // Two titles aren't enough to save it unasked.
    }

    [Fact]
    public void Infer_GoesWithMostTitles_AndIsNotCertainWhenTheyDisagree()
    {
        var theirs = new[] { "/tv/Severance (2022)", "/tv/Andor (2022)", "/tv/Slow Horses (2022)", "/tv/The Bear (2022)" };
        var ours = Ours[..3].Append("/Volumes/Other/The Bear (2022)");

        var found = PathMappingDetector.Infer("/tv", theirs, ours);

        Assert.Equal(new DetectedMapping("/tv", "/Volumes/Media/TV", 3, 4), found);
        Assert.False(found!.Certain);
    }

    [Fact]
    public void Infer_SkipsNamesFoundTwice_AndTitlesOutsideTheRoot()
    {
        var theirs = new[] { "/tv/Severance (2022)", "/anime/Andor (2022)" };
        var ours = new[] { "/Volumes/Media/TV/Severance (2022)", "/Volumes/Old/Severance (2022)", "/Volumes/Media/TV/Andor (2022)" };

        Assert.Null(PathMappingDetector.Infer("/tv", theirs, ours));
    }

    [Fact]
    public void Infer_IsNull_WhenTheTitlesAreAtTheSamePaths()
    {
        var theirs = Ours.ToArray();

        Assert.Null(PathMappingDetector.Infer("/Volumes/Media/TV", theirs, Ours));
    }

    [Fact]
    public void Infer_MatchesNamesInEitherUnicodeForm()
    {
        var theirs = new[] { "/movies/Léon (1994)".Normalize(System.Text.NormalizationForm.FormC) };
        var ours = new[] { "/Volumes/Films/Léon (1994)".Normalize(System.Text.NormalizationForm.FormD) };

        Assert.Equal(new DetectedMapping("/movies", "/Volumes/Films", 1, 1), PathMappingDetector.Infer("/movies", theirs, ours));
    }
}
