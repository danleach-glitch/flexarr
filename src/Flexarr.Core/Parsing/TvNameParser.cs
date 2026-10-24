using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

public sealed record ParsedSeriesName(string Title, int? Year, ExternalIds Ids);

public sealed record ParsedEpisode(int Season, int Episode, int? EpisodeEnd, string? Title, DateOnly? AirDate);

/// <summary>Parses series folder names and episode file names (Sonarr naming, scene naming, specials).</summary>
public static partial class TvNameParser
{
    public static ParsedSeriesName ParseSeriesFolder(string folderName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderName);

        var (name, ids) = ExternalIdParser.Extract(Path.GetFileName(folderName.TrimEnd('/', '\\')));
        var match = TrailingYear().Match(name);
        return match.Success
            ? new ParsedSeriesName(match.Groups["title"].Value.Trim(), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture), ids)
            : new ParsedSeriesName(name, null, ids);
    }

    /// <summary>Season number implied by a folder name: "Season 02" → 2, "Specials" → 0.</summary>
    public static int? ParseSeasonFolder(string folderName)
    {
        ArgumentNullException.ThrowIfNull(folderName);

        if (folderName.Equals("Specials", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        var match = SeasonFolder().Match(folderName);
        return match.Success ? int.Parse(match.Groups["season"].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <param name="fileName">The episode file name.</param>
    /// <param name="seasonFromFolder">Season implied by the containing folder, used when the name has no season.</param>
    /// <param name="seriesTitle">Series title, used to find a bare episode number after it ("Show 03 Title").</param>
    public static ParsedEpisode? ParseEpisode(string fileName, int? seasonFromFolder = null, string? seriesTitle = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var name = Path.GetFileNameWithoutExtension(fileName);

        var match = SeasonEpisode().Match(name);
        if (match.Success)
        {
            var ends = match.Groups["end"].Captures;
            return new ParsedEpisode(
                Int(match.Groups["season"].Value),
                Int(match.Groups["episode"].Value),
                ends.Count > 0 ? Int(ends[^1].Value) : null,
                EpisodeTitle(name[(match.Index + match.Length)..]),
                null);
        }

        match = CrossEpisode().Match(name);
        if (match.Success)
        {
            return new ParsedEpisode(Int(match.Groups["season"].Value), Int(match.Groups["episode"].Value), null,
                EpisodeTitle(name[(match.Index + match.Length)..]), null);
        }

        match = Daily().Match(name);
        if (match.Success && DateOnly.TryParseExact(
                $"{match.Groups["y"].Value}-{match.Groups["m"].Value}-{match.Groups["d"].Value}",
                "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var airDate))
        {
            return new ParsedEpisode(seasonFromFolder ?? airDate.Year, 0, null, EpisodeTitle(name[(match.Index + match.Length)..]), airDate);
        }

        // "Only Fools And Horses 03 Thicker Than Water 1983 DVDRip" inside a season/Specials folder.
        if (seasonFromFolder is { } season)
        {
            var rest = StripPrefix(name, seriesTitle);
            match = LeadingNumber().Match(rest);
            if (match.Success)
            {
                return new ParsedEpisode(season, Int(match.Groups["episode"].Value), null, EpisodeTitle(rest[(match.Index + match.Length)..]), null);
            }
        }

        return null;
    }

    private static int Int(string value) => int.Parse(value, CultureInfo.InvariantCulture);

    /// <summary>Sonarr names put the title between " - " and the first "[quality]" tag.</summary>
    private static string? EpisodeTitle(string afterEpisode)
    {
        var title = afterEpisode.TrimStart(' ', '-', '.', '_');
        var bracket = title.IndexOf('[', StringComparison.Ordinal);
        if (bracket >= 0)
        {
            title = title[..bracket];
        }

        title = ReleaseJunk().Replace(title, string.Empty).Trim(' ', '-', '.', '_');
        return title.Length == 0 || !title.Contains(' ', StringComparison.Ordinal) && title.Contains('.', StringComparison.Ordinal)
            ? null
            : title;
    }

    private static string StripPrefix(string name, string? seriesTitle)
    {
        if (seriesTitle is null)
        {
            return name;
        }

        var normalized = Separators().Replace(name, " ");
        return normalized.StartsWith(seriesTitle, StringComparison.OrdinalIgnoreCase) ? normalized[seriesTitle.Length..] : normalized;
    }

    [GeneratedRegex(@"\bS(?<season>\d{1,2})[\s._-]?E(?<episode>\d{1,3})(?:[-\s]?E?(?<end>\d{1,3})(?=\b|E))*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisode();

    [GeneratedRegex(@"\b(?<season>\d{1,2})x(?<episode>\d{2,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex CrossEpisode();

    [GeneratedRegex(@"\b(?<y>(?:19|20)\d{2})[-._ ](?<m>\d{2})[-._ ](?<d>\d{2})\b")]
    private static partial Regex Daily();

    [GeneratedRegex(@"^\s*[-.]?\s*(?<episode>\d{1,3})(?=[\s._-])")]
    private static partial Regex LeadingNumber();

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<year>(?:19|20)\d{2})\)$")]
    private static partial Regex TrailingYear();

    [GeneratedRegex(@"^(?:Season|Series|S)[\s._-]*(?<season>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\b(?:(?:19|20)\d{2}\s+)?(?:DVDRip|BDRip|BluRay|WEB-?DL|WEBRip|HDTV|HEVC|x26[45]|H\.?26[45]|\d{3,4}p)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex ReleaseJunk();

    [GeneratedRegex(@"[._]+")]
    private static partial Regex Separators();
}
