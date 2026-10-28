using System.Globalization;

namespace Flexarr.Streaming;

/// <summary>Display names for the ISO 639 codes found in media files ("eng", "fre", "pt-BR").</summary>
public static class Languages
{
    // ISO 639-2/B "bibliographic" codes that .NET doesn't resolve, plus common three-letter codes.
    private static readonly Dictionary<string, string> ThreeLetter = new(StringComparer.OrdinalIgnoreCase)
    {
        ["eng"] = "English", ["fre"] = "French", ["fra"] = "French", ["ger"] = "German", ["deu"] = "German",
        ["spa"] = "Spanish", ["ita"] = "Italian", ["por"] = "Portuguese", ["dut"] = "Dutch", ["nld"] = "Dutch",
        ["swe"] = "Swedish", ["nor"] = "Norwegian", ["nob"] = "Norwegian Bokmål", ["dan"] = "Danish", ["fin"] = "Finnish",
        ["ice"] = "Icelandic", ["isl"] = "Icelandic", ["pol"] = "Polish", ["cze"] = "Czech", ["ces"] = "Czech",
        ["slo"] = "Slovak", ["slk"] = "Slovak", ["hun"] = "Hungarian", ["rum"] = "Romanian", ["ron"] = "Romanian",
        ["gre"] = "Greek", ["ell"] = "Greek", ["tur"] = "Turkish", ["rus"] = "Russian", ["ukr"] = "Ukrainian",
        ["bul"] = "Bulgarian", ["hrv"] = "Croatian", ["srp"] = "Serbian", ["slv"] = "Slovenian", ["est"] = "Estonian",
        ["lav"] = "Latvian", ["lit"] = "Lithuanian", ["ara"] = "Arabic", ["heb"] = "Hebrew", ["per"] = "Persian",
        ["fas"] = "Persian", ["hin"] = "Hindi", ["tam"] = "Tamil", ["tel"] = "Telugu", ["tha"] = "Thai",
        ["vie"] = "Vietnamese", ["ind"] = "Indonesian", ["may"] = "Malay", ["msa"] = "Malay", ["chi"] = "Chinese",
        ["zho"] = "Chinese", ["jpn"] = "Japanese", ["kor"] = "Korean", ["fil"] = "Filipino", ["cat"] = "Catalan",
        ["baq"] = "Basque", ["eus"] = "Basque", ["glg"] = "Galician", ["wel"] = "Welsh", ["cym"] = "Welsh",
        ["gle"] = "Irish", ["und"] = "Unknown", ["zxx"] = "No language",
    };

    public static string? Name(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return null;
        }

        if (ThreeLetter.TryGetValue(code, out var name))
        {
            return name;
        }

        try
        {
            var culture = CultureInfo.GetCultureInfo(code);
            return culture.EnglishName.StartsWith("Unknown", StringComparison.Ordinal) ? code : culture.EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    // ISO 639-2/B codes whose two-letter tag .NET can't find from the code itself.
    private static readonly Dictionary<string, string> Bibliographic = new(StringComparer.OrdinalIgnoreCase)
    {
        ["fre"] = "fr", ["ger"] = "de", ["dut"] = "nl", ["ice"] = "is", ["cze"] = "cs", ["slo"] = "sk", ["rum"] = "ro",
        ["gre"] = "el", ["per"] = "fa", ["may"] = "ms", ["chi"] = "zh", ["baq"] = "eu", ["wel"] = "cy", ["arm"] = "hy",
        ["geo"] = "ka", ["mac"] = "mk", ["alb"] = "sq", ["bur"] = "my", ["tib"] = "bo", ["mao"] = "mi",
    };

    /// <summary>
    /// The BCP-47 tag for a file's language code, as HLS playlists want it: the two-letter code where there is one
    /// ("eng" is "en", "fre" is "fr"), else the code as it was. A tag with a region ("pt-BR") is one already.
    /// </summary>
    public static string Tag(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return "und";
        }

        code = code.Trim();
        if (code.Length != 3 || code.Equals("und", StringComparison.OrdinalIgnoreCase) || code.Equals("zxx", StringComparison.OrdinalIgnoreCase))
        {
            return code;
        }

        if (Bibliographic.TryGetValue(code, out var tag))
        {
            return tag;
        }

        try
        {
            var two = CultureInfo.GetCultureInfo(code).TwoLetterISOLanguageName;
            return two.Length == 2 ? two : code;
        }
        catch (CultureNotFoundException)
        {
            return code;
        }
    }

    /// <summary>
    /// A label for a track: the language, plus whatever the stream title adds ("SDH", "Commentary"), with the
    /// noise removed: source names ("iTunes"), the language repeated, and nested brackets.
    /// </summary>
    public static string Label(string? code, string? title, bool forced)
    {
        var language = Name(code) ?? "Unknown";
        var extra = Clean(title, language, code);
        var label = extra is null ? language : $"{language} ({extra})";
        return forced && !label.Contains("forced", StringComparison.OrdinalIgnoreCase) ? $"{label} (forced)" : label;
    }

    private static string? Clean(string? title, string language, string? code)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var words = title.Replace('(', ' ').Replace(')', ' ').Replace('[', ' ').Replace(']', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(w => !UselessTitles.Contains(w) && !w.Equals(language, StringComparison.OrdinalIgnoreCase) && !w.Equals(code, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var cleaned = string.Join(' ', words).Trim(' ', '-', ',');
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static readonly HashSet<string> UselessTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "iTunes", "Netflix", "Amazon", "AMZN", "Disney+", "HBO", "Max", "Hulu", "Apple", "TV+", "Full", "Subtitles", "Subtitle", "Subs",
        "Default", "Stereo", "Surround", "SRT", "PGS", "v2", "v3",
    };
}
