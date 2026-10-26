using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Flexarr.Core.LiveTv;

/// <summary>
/// Channel logos from the community collection github.com/tv-logo/tv-logos: current designs, at their own proportions,
/// one PNG per channel per country (<c>countries/united-kingdom/channel-4-uk.png</c>). Matched by the channel's name,
/// in the country its group or name starts with ("UK| News", "UK: E4"), then the international and US folders.
/// </summary>
public sealed partial class CommunityLogos
{
    public const string Root = "https://raw.githubusercontent.com/tv-logo/tv-logos/main/";

    // Folder → (file name without .png → path), and each folder's own suffix ("uk", "us", "int").
    private readonly Dictionary<string, Dictionary<string, string>> _byFolder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _folderBySuffix = new(StringComparer.Ordinal);

    /// <param name="paths">The collection's PNGs, as paths in the repository.</param>
    public CommunityLogos(IEnumerable<string> paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var suffixes = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var parts = path.Split('/');
            if (parts is not ["countries", var folder, var file] || !file.EndsWith(".png", StringComparison.Ordinal))
            {
                continue;
            }

            var name = file[..^4];
            if (!_byFolder.TryGetValue(folder, out var files))
            {
                _byFolder[folder] = files = new Dictionary<string, string>(StringComparer.Ordinal);
            }

            files[name] = path;
            var dash = name.LastIndexOf('-');
            if (dash > 0)
            {
                var counts = suffixes.TryGetValue(folder, out var c) ? c : suffixes[folder] = new Dictionary<string, int>(StringComparer.Ordinal);
                var suffix = name[(dash + 1)..];
                counts[suffix] = counts.GetValueOrDefault(suffix) + 1;
            }
        }

        // A folder's suffix is the one most of its files end with ("united-kingdom" → "uk").
        foreach (var (folder, counts) in suffixes)
        {
            var suffix = counts.MaxBy(c => c.Value).Key;
            _folderBySuffix.TryAdd(suffix, folder);
        }
    }

    public int Count => _byFolder.Values.Sum(f => f.Count);

    /// <summary>
    /// The country most of a line-up's channels name (in their group or name), as the collection's suffix ("uk");
    /// null when none do.
    /// </summary>
    public static string? HomeCountry(IEnumerable<(string Name, string? Group)> channels) =>
        channels.Select(c => CountryCode(c.Group) ?? CountryCode(c.Name)).OfType<string>()
            .GroupBy(c => c, StringComparer.Ordinal).MaxBy(g => g.Count())?.Key;

    /// <summary>The logo for a channel, as an address under <see cref="Root"/>; null when the collection has none.</summary>
    /// <param name="homeCountry">Where a channel that names no country is looked for first (<see cref="HomeCountry"/>).</param>
    public string? Find(string channelName, string? group, string? homeCountry = null)
    {
        ArgumentNullException.ThrowIfNull(channelName);
        var country = CountryCode(group) ?? CountryCode(channelName) ?? Suffix(homeCountry);
        var folders = new List<(string Folder, string Suffix)>();
        if (country is not null && _folderBySuffix.TryGetValue(country, out var home))
        {
            folders.Add((home, country));
        }

        foreach (var fallback in new[] { "int", "us", "uk" })
        {
            if (fallback != country && _folderBySuffix.TryGetValue(fallback, out var folder))
            {
                folders.Add((folder, fallback));
            }
        }

        var slugs = Slugs(channelName).ToList();
        foreach (var (folder, suffix) in folders)
        {
            var files = _byFolder[folder];
            foreach (var slug in slugs)
            {
                if (files.TryGetValue($"{slug}-{suffix}", out var path))
                {
                    return Root + path;
                }
            }
        }

        return null;
    }

    public static bool IsCommunity(Uri url)
    {
        ArgumentNullException.ThrowIfNull(url);
        return url.Scheme == Uri.UriSchemeHttps && url.AbsoluteUri.StartsWith(Root, StringComparison.Ordinal) && !url.AbsolutePath.Contains("..", StringComparison.Ordinal);
    }

    /// <summary>
    /// "UK" from "UK| News", "UK: E4" or a group called just "UK", as the collection's suffix ("gb" and the like
    /// mapped to theirs).
    /// </summary>
    internal static string? CountryCode(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var m = CountryPrefix().Match(text);
        return m.Success ? Suffix(m.Groups[1].Value) : CountryOnly().IsMatch(text) ? Suffix(text.Trim()) : null;
    }

    private static string? Suffix(string? code) => code?.ToLowerInvariant() switch
    {
        null or "" => null,
        "gb" => "uk",
        "usa" => "us",
        "uae" => "ae",
        var other => other,
    };

    /// <summary>
    /// The forms the collection might file a channel under, best first: "UK: ITV 1 HD ◉" gives itv-1, itv1; "BBC 1"
    /// also bbc-one; "E4" e-4; "More4" more-4 and 4-more; "+1" plus. Quality marks (HD, 4K, HEVC, RAW) are dropped,
    /// and then one trailing word ("BBC One London" → bbc-one), for regional and language variants.
    /// </summary>
    internal static IEnumerable<string> Slugs(string channelName)
    {
        var name = WithoutAccents(CountryPrefix().Replace(channelName, ""));
        // "ITVBe" is filed as itv-be: a capital after a run of capitals, or after a small letter, starts a word.
        name = CamelCase().Replace(name, " ");
        name = name.Replace("+1", " plus ", StringComparison.Ordinal).Replace("+", " plus ", StringComparison.Ordinal)
            .Replace("&", " and ", StringComparison.Ordinal);
        var whole = NonWord().Replace(name.ToLowerInvariant(), " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => !QualityWords.Contains(w))
            .ToList();
        if (whole.Count == 0)
        {
            yield break;
        }

        // Numbers split off ("itv1" → itv-1, "5usa" → 5-usa), and kept whole ("f1").
        var split = whole.SelectMany(SplitDigits).ToList();
        var lists = new List<List<string>> { split, whole };
        if (split.Count > 1)
        {
            lists.Add(split[..^1]);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var forms = lists.SelectMany(Forms).Where(f => f.Length > 0 && seen.Add(f)).ToList();
        foreach (var form in forms)
        {
            yield return form;
        }

        // Some are only filed in an HD or a horizontal version (sky-sports-f1-hd, msnbc-hz).
        foreach (var form in forms)
        {
            yield return form + "-hd";
        }

        foreach (var form in forms)
        {
            yield return form + "-hz";
        }

        static IEnumerable<string> Forms(List<string> list)
        {
            yield return string.Join('-', list);
            yield return string.Join('-', list.Select(NumberWord));
            if (list is [var word, var digits] && digits.All(char.IsAsciiDigit) && !word.All(char.IsAsciiDigit))
            {
                yield return $"{digits}-{word}";
            }

            yield return string.Concat(list);
        }
    }

    /// <summary>"Télé" → "Tele": the collection's file names are plain ASCII.</summary>
    private static string WithoutAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var kept = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark))
        {
            kept.Append(c);
        }

        return kept.ToString().Normalize(NormalizationForm.FormC);
    }

    private static readonly HashSet<string> QualityWords = new(StringComparer.Ordinal)
    {
        "hd", "fhd", "uhd", "sd", "4k", "8k", "hevc", "h265", "h264", "raw", "vip", "60fps", "50fps", "1080p", "720p", "backup",
    };

    private static string NumberWord(string word) => word switch
    {
        "1" => "one",
        "2" => "two",
        "3" => "three",
        "4" => "four",
        _ => word,
    };

    /// <summary>"itv1" → itv, 1; "more4" → more, 4; "5usa" → 5, usa.</summary>
    private static IEnumerable<string> SplitDigits(string word) =>
        LettersThenDigits().Match(word) is { Success: true } a ? [a.Groups[1].Value, a.Groups[2].Value]
        : DigitsThenLetters().Match(word) is { Success: true } b ? [b.Groups[1].Value, b.Groups[2].Value]
        : [word];

    [GeneratedRegex(@"^\s*([A-Za-z]{2,3})\s*[|:]\s*")]
    private static partial Regex CountryPrefix();

    [GeneratedRegex(@"[^\p{L}\p{N}]+")]
    private static partial Regex NonWord();

    [GeneratedRegex(@"^(\p{L}+)(\d+)$")]
    private static partial Regex LettersThenDigits();

    [GeneratedRegex(@"^(\d+)(\p{L}{2,})$")]
    private static partial Regex DigitsThenLetters();

    [GeneratedRegex(@"(?<=\p{Lu})(?=\p{Lu}\p{Ll})|(?<=\p{Ll})(?=\p{Lu})")]
    private static partial Regex CamelCase();

    [GeneratedRegex(@"^\s*[A-Za-z]{2,3}\s*$")]
    private static partial Regex CountryOnly();
}
