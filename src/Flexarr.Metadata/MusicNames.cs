using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flexarr.Metadata;

/// <summary>
/// Artist names and album titles reduced to what makes them the same across Deezer, MusicBrainz (through Lidarr) and
/// the files on disk: case, accents, punctuation, a leading "The", "&amp;" for "and", and edition labels on albums.
/// </summary>
public static partial class MusicNames
{
    /// <summary>An artist's name for comparing: "The Beatles", "beatles" and "Beatles!" are the same.</summary>
    public static string Normalise(string? name)
    {
        var plain = Plain(name);
        return plain.StartsWith("the ", StringComparison.Ordinal) ? plain[4..] : plain;
    }

    /// <summary>
    /// An album's title for comparing, without the edition: "Rumours (Super Deluxe)", "Rumours - 2004 Remaster" and
    /// "Rumours [Expanded Edition]" are all "rumours". A title that is nothing but brackets is kept as it is.
    /// </summary>
    public static string NormaliseTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return "";
        }

        var trimmed = EditionSuffix().Replace(Bracketed().Replace(title, m => Edition().IsMatch(m.Value) ? "" : m.Value), "");
        var plain = Plain(trimmed);
        return plain.Length > 0 ? plain : Plain(title);
    }

    private static string Plain(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "";
        }

        var decomposed = text.Replace("&", " and ", StringComparison.Ordinal).Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }

        return Spaces().Replace(builder.ToString(), " ").Trim();
    }

    /// <summary>A part in brackets: "(Deluxe Edition)", "[2011 Remaster]".</summary>
    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex Bracketed();

    /// <summary>
    /// Words that make a bracketed part, or a part after " - ", an edition rather than part of the name. Not
    /// "version": "(Taylor's Version)" is a different recording, not a pressing.
    /// </summary>
    [GeneratedRegex(@"\b(deluxe|edition|remaster(ed)?|expanded|anniversary|bonus|reissue|mono|stereo|explicit|clean|super)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Edition();

    [GeneratedRegex(@"\s+-\s+[^-]*\b(deluxe|edition|remaster(ed)?|expanded|anniversary|bonus|reissue)\b.*$", RegexOptions.IgnoreCase)]
    private static partial Regex EditionSuffix();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
