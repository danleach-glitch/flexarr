using Flexarr.Core.Parsing;

namespace Flexarr.Core.Media;

/// <summary>
/// What makes files of one title different versions of it (see docs/multi-versions.md). Shared by playback, downloads
/// and Library Health, so they agree.
/// </summary>
public static class VersionRules
{
    /// <summary>Lengths this close are one cut: a different frame rate runs a few percent apart.</summary>
    private const double CutTolerance = 0.05;

    /// <summary>And this close, however short the title: an episode's versions can differ by a logo at the start.</summary>
    private const double MinCutToleranceSeconds = 60;

    public static string? ResolutionLabel(int? width, int? height) => (width, height) switch
    {
        (null, null) => null,
        ( >= 3200, _) or (_, >= 1800) => "4K",
        ( >= 1800, _) or (_, >= 1000) => "1080p",
        ( >= 1200, _) or (_, >= 700) => "720p",
        _ => "SD",
    };

    /// <summary>The name of the file's picture: "4K", "1080p HDR10", "4K Dolby Vision".</summary>
    public static string Label(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        string?[] parts = [ResolutionLabel(file.Width, file.Height), RangeLabel(file)];
        return string.Join(' ', parts.Where(p => p is not null));
    }

    private static string? RangeLabel(MediaFile file) => (file.VideoRange, file.Hdr10Plus == true) switch
    {
        (VideoRange.DolbyVision, true) => "Dolby Vision HDR10+",
        (VideoRange.DolbyVision, false) => "Dolby Vision",
        (_, true) => "HDR10+",
        (VideoRange.Hdr10, false) => "HDR10",
        (VideoRange.Hlg, false) => "HLG",
        _ => null,
    };

    public static string? Edition(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return EditionParser.Parse(file.Path);
    }

    /// <summary>
    /// Whether two files are one cut of the title: their edition names agree, and their lengths are close. A length
    /// nobody knows doesn't separate them.
    /// </summary>
    public static bool SameCut(MediaFile a, MediaFile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        if (!string.Equals(Edition(a), Edition(b), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (a.DurationSeconds is not { } first || b.DurationSeconds is not { } second)
        {
            return true;
        }

        return Math.Abs(first - second) <= Math.Max(MinCutToleranceSeconds, Math.Max(first, second) * CutTolerance);
    }

    /// <summary>Whether someone would keep both on purpose. Files alike in resolution, HDR type and cut are duplicates.</summary>
    public static bool Distinct(MediaFile a, MediaFile b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);

        return ResolutionLabel(a.Width, a.Height) != ResolutionLabel(b.Width, b.Height) || a.VideoRange != b.VideoRange || !SameCut(a, b);
    }

    /// <summary>Best quality first: resolution (an ultrawide 3840×1600 counts as 4K), then bitrate.</summary>
    public static IOrderedEnumerable<MediaFile> BestFirst(IEnumerable<MediaFile> files) =>
        files.OrderByDescending(f => Math.Max(f.Height ?? 0, (f.Width ?? 0) * 9 / 16)).ThenByDescending(f => f.Bitrate ?? 0).ThenBy(f => f.Id);
}
