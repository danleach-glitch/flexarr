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

    /// <summary>
    /// A label for a track: the language, plus the stream title when it says something useful
    /// ("SDH", "Commentary") rather than naming the source ("iTunes", "English").
    /// </summary>
    public static string Label(string? code, string? title, bool forced)
    {
        var language = Name(code) ?? "Unknown";
        var useful = title is { Length: > 0 }
            && !title.Equals(language, StringComparison.OrdinalIgnoreCase)
            && !title.Equals(code, StringComparison.OrdinalIgnoreCase)
            && !UselessTitles.Contains(title.Trim());
        var label = useful ? $"{language} ({title!.Trim()})" : language;
        return forced && !label.Contains("forced", StringComparison.OrdinalIgnoreCase) ? $"{label} (forced)" : label;
    }

    private static readonly HashSet<string> UselessTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "iTunes", "Netflix", "Amazon", "Disney+", "HBO", "Hulu", "Apple TV+", "Full", "Subtitles", "Subtitle", "Default", "Stereo", "Surround",
    };
}
