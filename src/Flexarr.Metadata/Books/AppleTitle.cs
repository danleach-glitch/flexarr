using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Core.Books;

namespace Flexarr.Metadata.Books;

/// <summary>
/// Reads what Apple Books packs into a few fields: its titles carry the series and edition ("This Inevitable Ruin:
/// Dungeon Crawler Carl, Book 7 (Unabridged)"), its one author field every author ("Shirtaloon &amp; Travis Deverell"),
/// and its copyright line the publisher.
/// </summary>
public static partial class AppleTitle
{
    /// <summary>A title taken apart: the book's own title, its subtitle or series, and whether it said it's abridged.</summary>
    public sealed record Parts(string Title, string? Subtitle, IReadOnlyList<SeriesPart> Series, bool? Abridged);

    /// <summary>
    /// "Title: Series, Book 7 (Unabridged)", "Title (Series, Book 2)" or "Title: Subtitle", each with or without the
    /// edition on the end.
    /// </summary>
    public static Parts Parse(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var title = name.Trim();
        bool? abridged = null;
        if (Edition().Match(title) is { Success: true } edition)
        {
            abridged = edition.Groups["kind"].Value.Equals("abridged", StringComparison.OrdinalIgnoreCase);
            title = title[..edition.Index].TrimEnd();
        }

        if (SeriesAfterColon().Match(title) is { Success: true } colon)
        {
            return new(colon.Groups["title"].Value.Trim(), null, [new SeriesPart(colon.Groups["series"].Value.Trim(), colon.Groups["n"].Value)], abridged);
        }

        if (SeriesInBrackets().Match(title) is { Success: true } brackets)
        {
            return new(brackets.Groups["title"].Value.Trim(), null, [new SeriesPart(brackets.Groups["series"].Value.Trim(), brackets.Groups["n"].Value)], abridged);
        }

        if (Subtitle().Match(title) is { Success: true } sub)
        {
            return new(sub.Groups["title"].Value.Trim(), sub.Groups["sub"].Value.Trim(), [], abridged);
        }

        return new(title, null, [], abridged);
    }

    /// <summary>"Shirtaloon &amp; Travis Deverell" → both; "Douglas Preston, Lincoln Child" → both.</summary>
    public static List<string> Authors(string? artist) =>
        artist is null ? [] : [.. artist.Split(['&', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// "© 2021 Audible Studios" → "Audible Studios". Null when there's no line, or it names one of the authors (who own
    /// the words, not the recording).
    /// </summary>
    public static string? Publisher(string? copyright, IReadOnlyCollection<string> authors)
    {
        ArgumentNullException.ThrowIfNull(authors);
        if (copyright is null || Copyright().Match(copyright) is not { Success: true } line)
        {
            return null;
        }

        var holder = line.Groups["holder"].Value.Trim().TrimEnd('.');
        return holder.Length < 2 || authors.Any(a => Fold(a) == Fold(holder)) ? null : holder;
    }

    /// <summary>A large cover in place of the 100-pixel one the search lists ("…/100x100bb.jpg" → "…/2000x2000bb.jpg").</summary>
    public static string? Cover(string? artwork)
    {
        if (artwork is null || !artwork.StartsWith("https://", StringComparison.Ordinal))
        {
            return null;
        }

        return ArtworkSize().Replace(artwork, "/2000x2000bb.jpg");
    }

    /// <summary>
    /// The language a description is written in, by its commonest words (Apple lists none): English, German, French,
    /// Italian, Spanish or Japanese. Null when it's too short to tell.
    /// </summary>
    public static string? LanguageOf(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (text.Count(c => c is >= '぀' and <= 'ヿ') >= 5)
        {
            return "Japanese"; // kana
        }

        var words = Words().Matches(text.ToLowerInvariant()).Select(m => m.Value).ToList();
        var best = Common.Select(l => (l.Language, Hits: words.Count(l.Words.Contains))).OrderByDescending(l => l.Hits).First();
        return best.Hits >= 3 ? best.Language : null;
    }

    private static readonly (string Language, HashSet<string> Words)[] Common =
    [
        ("English", ["the", "and", "of", "to", "is", "in", "his", "her", "with", "that", "he", "she"]),
        ("German", ["der", "die", "und", "das", "ist", "nicht", "mit", "sich", "ein", "eine", "auf", "den"]),
        ("French", ["le", "les", "et", "est", "une", "des", "dans", "qui", "pour", "pas", "du", "au"]),
        ("Italian", ["il", "che", "di", "della", "non", "per", "è", "gli", "sono", "nel", "lo", "suo"]),
        ("Spanish", ["el", "los", "las", "que", "por", "con", "del", "para", "su", "es", "se", "y"]),
    ];

    private static string Fold(string value) => new([.. value.Normalize(NormalizationForm.FormC).ToLowerInvariant().Where(char.IsLetterOrDigit)]);

    [GeneratedRegex(@"\s*[\(\[](?<kind>unabridged|abridged)[\)\]]$", RegexOptions.IgnoreCase)]
    private static partial Regex Edition();

    [GeneratedRegex(@"^(?<title>.+?):\s*(?<series>[^:]+?),?\s+(?:Book|Volume|Vol\.|Bk\.?|Part)\s*(?<n>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesAfterColon();

    [GeneratedRegex(@"^(?<title>.+?)\s*\((?<series>[^()]+?),?\s+(?:Book|Volume|Vol\.|Bk\.?|Part)\s*(?<n>\d+(?:\.\d+)?)\)$", RegexOptions.IgnoreCase)]
    private static partial Regex SeriesInBrackets();

    [GeneratedRegex(@"^(?<title>[^:]{2,}?):\s+(?<sub>.+)$")]
    private static partial Regex Subtitle();

    [GeneratedRegex(@"^\s*(?:©|\(c\)|℗|\(p\))?\s*(?:\d{4}\s*)?(?<holder>.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex Copyright();

    [GeneratedRegex(@"/\d+x\d+bb\.(?:jpg|png|webp)$")]
    private static partial Regex ArtworkSize();

    [GeneratedRegex(@"\p{L}+")]
    private static partial Regex Words();
}
