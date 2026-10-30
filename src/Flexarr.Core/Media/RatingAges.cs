using System.Globalization;

namespace Flexarr.Core.Media;

/// <summary>A parent's say over one title for profiles with an age limit, whatever its rating.</summary>
public enum KidsOverride
{
    None,

    /// <summary>Shown to every profile with an age limit, rated or not.</summary>
    Allow,

    /// <summary>Hidden from every profile with an age limit, whatever its rating.</summary>
    Block,
}

/// <summary>
/// Turns a content rating from any country into the age it's suitable from, on the UK's steps (U, PG, 12, 15, 18), so
/// one age limit works across a library whose ratings came from the US, the UK, Germany or Australia. A rating means
/// an age, rounded up to the next step so nothing slips under a limit (Germany's 16 is 18); a few have a UK
/// equivalent of their own (PG-13 is 12, TV-14 is 15, R is 18).
/// </summary>
public static class RatingAges
{
    public const int Universal = 0;
    public const int ParentalGuidance = 8;
    public const int Twelve = 12;
    public const int Fifteen = 15;
    public const int Eighteen = 18;

    /// <summary>The steps an age limit can be set to, youngest first.</summary>
    public static IReadOnlyList<int> Steps { get; } = [Universal, ParentalGuidance, Twelve, Fifteen, Eighteen];

    /// <summary>A step's name, as the UK writes it.</summary>
    public static string Label(int age) => age switch
    {
        Universal => "U",
        ParentalGuidance => "PG",
        _ => age.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// The age an item of <paramref name="kind"/> rated <paramref name="rating"/> is suitable from; null when it has no
    /// rating Flexarr knows (unrated titles are hidden from limited profiles unless a parent allows them). A podcast
    /// without the explicit flag is clean, so it's suitable for everyone.
    /// </summary>
    public static int? For(MediaKind kind, string? rating) =>
        Of(rating) ?? (kind is MediaKind.Podcast or MediaKind.PodcastEpisode && string.IsNullOrWhiteSpace(rating) ? Universal : null);

    /// <summary>The age <paramref name="rating"/> is suitable from, on the UK's steps; null for no rating or one not known.</summary>
    public static int? Of(string? rating)
    {
        if (string.IsNullOrWhiteSpace(rating))
        {
            return null;
        }

        var r = rating.Trim().ToUpperInvariant();
        // "UK:15", "gb/12A", "Rated PG-13", "FSK 16": the country or system in front doesn't matter here.
        if (r.LastIndexOfAny([':', '/']) is var cut and >= 0)
        {
            r = r[(cut + 1)..].Trim();
        }

        foreach (var prefix in (string[])["RATED ", "FSK ", "FSK", "BBFC ", "KIJKWIJZER "])
        {
            if (r.StartsWith(prefix, StringComparison.Ordinal))
            {
                r = r[prefix.Length..].Trim();
            }
        }

        if (Named.TryGetValue(r, out var named))
        {
            return named;
        }

        // A rating with its reasons ("R for 2 scenes of violence", as .nfo files often have it): the rating's the first word.
        if (r.IndexOfAny([' ', ',', ';']) is var space and > 0)
        {
            return Short(r[..space]);
        }

        return Short(r);
    }

    /// <summary>A rating of one short word: named, or an age written as a number.</summary>
    private static int? Short(string r)
    {
        if (Named.TryGetValue(r, out var named))
        {
            return named;
        }

        // Ages written as numbers: "15", "-16" (Spain, Brazil), "16+" and "+16", "MA15+", "R18+", "12A". Only in words this
        // short, so a number in a sentence isn't taken for an age.
        var digits = new string(r.Where(char.IsAsciiDigit).ToArray());
        if (r.Length > 6 || digits.Length is 0 or > 2 || !int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var age))
        {
            return null;
        }

        return StepFor(age);
    }

    /// <summary>The youngest step at or above <paramref name="age"/>: a 16 needs an 18 limit, not a 15.</summary>
    public static int StepFor(int age) => Steps.FirstOrDefault(step => step >= age, Eighteen);

    private static readonly Dictionary<string, int?> Named = new(StringComparer.Ordinal)
    {
        // UK
        ["U"] = Universal,
        ["UC"] = Universal,
        ["PG"] = ParentalGuidance,
        ["R18"] = Eighteen,
        // US films
        ["G"] = Universal,
        ["PG-13"] = Twelve,
        ["R"] = Eighteen,
        ["NC-17"] = Eighteen,
        ["X"] = Eighteen,
        // US TV
        ["TV-Y"] = Universal,
        ["TV-G"] = Universal,
        ["TV-Y7"] = ParentalGuidance,
        ["TV-Y7-FV"] = ParentalGuidance,
        ["TV-PG"] = ParentalGuidance,
        ["TV-14"] = Fifteen,
        ["TV-MA"] = Eighteen,
        // Australia and New Zealand (MA15+, R18+ and the like are read as numbers)
        ["M"] = Fifteen,
        ["R13"] = Fifteen,
        ["R16"] = Eighteen,
        // Everyone, in other countries' words
        ["ALL"] = Universal,
        ["L"] = Universal,
        ["AL"] = Universal,
        ["TP"] = Universal,
        ["0"] = Universal,
        ["0+"] = Universal,
        ["BTL"] = Universal,
        ["APTA"] = Universal,
        // Podcasts and music
        ["EXPLICIT"] = Eighteen,
        ["CLEAN"] = Universal,
        // Said to have no rating
        ["NR"] = null,
        ["NOT RATED"] = null,
        ["UNRATED"] = null,
        ["E"] = null,
        ["EXEMPT"] = null,
        ["APPROVED"] = null,
        ["PASSED"] = null,
    };

    /// <summary>
    /// Changes when the table of ratings or how they're read does, so ages worked out with an older one are done again.
    /// After the table, which it's made from. Bump the "2" when how ratings are read changes.
    /// </summary>
    public static string Version { get; } = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
        "2|" + string.Join(';', Named.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => $"{n.Key}={n.Value}")))))[..12];
}
