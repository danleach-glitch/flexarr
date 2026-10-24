using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

public sealed record ParsedMovieName(string Title, int? Year, ExternalIds Ids);

/// <summary>
/// Parses movie titles from folder or file names following the common
/// "Title (Year)" convention used by Radarr and Plex.
/// </summary>
public static partial class MovieNameParser
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm",
    };

    public static ParsedMovieName Parse(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var stem = Path.GetFileName(name.TrimEnd('/', '\\'));
        var extension = Path.GetExtension(stem);
        if (VideoExtensions.Contains(extension))
        {
            stem = stem[..^extension.Length];
        }

        (stem, var ids) = ExternalIdParser.Extract(stem);

        var match = TitleYear().Match(stem);
        if (match.Success)
        {
            return new ParsedMovieName(Clean(match.Groups["title"].Value), int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture), ids);
        }

        return new ParsedMovieName(Clean(Tags().Replace(stem, string.Empty)), null, ids);
    }

    private static string Clean(string title)
    {
        // Scene-style names use dots/underscores as separators; real titles ("Mr. Nobody") keep them.
        if (!title.Contains(' ', StringComparison.Ordinal))
        {
            title = title.Replace('.', ' ').Replace('_', ' ');
        }

        return Whitespace().Replace(title, " ").Trim(' ', '-', '(', '[', '.', '_');
    }

    [GeneratedRegex(@"^(?<title>.+)[\s._]*[\(\[]?(?<year>(?:19|20)\d{2})[\)\]]?(?:[\s._\[\(-]|$)")]
    private static partial Regex TitleYear();

    [GeneratedRegex(@"[\[\(].*?[\]\)]")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
