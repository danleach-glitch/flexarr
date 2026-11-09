using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

/// <summary>
/// Which edition of a film a file says it is: a "{edition-…}" tag (Plex's and Radarr's naming) anywhere in the name, or
/// a well-known edition's name after the film's year. Without a year the words could be the title ("Final Cut.mkv", or
/// an episode called "The Final Cut"), so only the tag counts there.
/// </summary>
public static partial class EditionParser
{
    private static readonly (Regex Pattern, string Name)[] Known =
    [
        (DirectorsCut(), "Director's Cut"),
        (FinalCut(), "Final Cut"),
        (UltimateCut(), "Ultimate Cut"),
        (SpecialEdition(), "Special Edition"),
        (Extended(), "Extended"),
        (Theatrical(), "Theatrical"),
        (Unrated(), "Unrated"),
        (Uncut(), "Uncut"),
        (Imax(), "IMAX"),
    ];

    public static string? Parse(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = Path.GetFileNameWithoutExtension(path.Replace('\\', '/').Split('/')[^1]);
        if (Tag().Match(name) is { Success: true } tag)
        {
            var edition = Separators().Replace(tag.Groups["name"].Value, " ").Trim();
            return edition.Length > 0 ? edition : null;
        }

        if (Episode().IsMatch(name) || Year().Matches(name) is not { Count: > 0 } years)
        {
            return null;
        }

        var after = name[(years[^1].Index + years[^1].Length)..];
        foreach (var (pattern, known) in Known)
        {
            if (pattern.IsMatch(after))
            {
                return known;
            }
        }

        return null;
    }

    [GeneratedRegex(@"\{edition-(?<name>[^}]*)\}", RegexOptions.IgnoreCase)]
    private static partial Regex Tag();

    [GeneratedRegex(@"[\s._]+")]
    private static partial Regex Separators();

    [GeneratedRegex(@"\bS\d{1,3}E\d{1,4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex Episode();

    /// <summary>"(2019)", "[2019]" or ".2019." but not a resolution or a title's number run into another word.</summary>
    [GeneratedRegex(@"(?<![\dA-Za-z])(?:19|20)\d{2}(?![\dA-Za-z])")]
    private static partial Regex Year();

    [GeneratedRegex(@"\bdirector'?s?[\s._-]*cut\b", RegexOptions.IgnoreCase)]
    private static partial Regex DirectorsCut();

    [GeneratedRegex(@"\bfinal[\s._-]*cut\b", RegexOptions.IgnoreCase)]
    private static partial Regex FinalCut();

    [GeneratedRegex(@"\bultimate[\s._-]*(cut|edition)\b", RegexOptions.IgnoreCase)]
    private static partial Regex UltimateCut();

    [GeneratedRegex(@"\bspecial[\s._-]*edition\b", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialEdition();

    [GeneratedRegex(@"\bextended\b", RegexOptions.IgnoreCase)]
    private static partial Regex Extended();

    [GeneratedRegex(@"\btheatrical\b", RegexOptions.IgnoreCase)]
    private static partial Regex Theatrical();

    [GeneratedRegex(@"\bunrated\b", RegexOptions.IgnoreCase)]
    private static partial Regex Unrated();

    [GeneratedRegex(@"\buncut\b", RegexOptions.IgnoreCase)]
    private static partial Regex Uncut();

    [GeneratedRegex(@"\bimax\b", RegexOptions.IgnoreCase)]
    private static partial Regex Imax();
}
