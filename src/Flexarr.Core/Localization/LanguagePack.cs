using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Core.Localization;

/// <summary>
/// A built language pack, as `make strings` writes it (bundled in Localization/Packs, published at
/// flexarr.io/languages, kept in the data folder): its translations keyed by English, and which codebases use each.
/// </summary>
public sealed class LanguagePack
{
    public const int Format = 1;
    public const int MaxBytes = 2_000_000;

    [JsonPropertyName("format")] public int PackFormat { get; init; } = Format;

    [JsonPropertyName("code")] public required string Code { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("direction")] public string Direction { get; init; } = "ltr";

    [JsonPropertyName("version")] public long Version { get; init; }

    [JsonPropertyName("coverage")] public Dictionary<string, double> Coverage { get; init; } = [];

    [JsonPropertyName("messages")] public Dictionary<string, string> Messages { get; init; } = [];

    /// <summary>Per key, the codebases that use it: w(eb), a(pple), (a)n(droid), s(erver).</summary>
    [JsonPropertyName("scopes")] public Dictionary<string, string> Scopes { get; init; } = [];

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = false };

    /// <summary>
    /// Reads and checks a pack: right format, a sensible code and direction, and every message one this formatter
    /// can read (a message that can't be read is dropped, so it shows in English). Throws on a pack that's unusable.
    /// </summary>
    public static LanguagePack Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > MaxBytes)
        {
            throw new InvalidDataException($"A language pack can't be over {MaxBytes / 1_000_000} MB");
        }

        var pack = JsonSerializer.Deserialize<LanguagePack>(bytes, Json) ?? throw new InvalidDataException("Empty language pack");
        if (pack.PackFormat != Format)
        {
            throw new InvalidDataException($"Language pack format {pack.PackFormat} isn't one this server reads");
        }

        if (!LanguageCode.IsValid(pack.Code) || pack.Direction is not ("ltr" or "rtl") || string.IsNullOrWhiteSpace(pack.Name))
        {
            throw new InvalidDataException("Language pack has a bad code, name or direction");
        }

        var bad = pack.Messages.Where(m => !Usable(m.Key, m.Value)).Select(m => m.Key).ToList();
        foreach (var key in bad)
        {
            pack.Messages.Remove(key);
        }

        return pack;
    }

    /// <summary>A translation the formatter can read, with exactly the English's values.</summary>
    public static bool Usable(string key, string translation)
    {
        try
        {
            var english = Names(MessageFormat.Parse(Loc.English(key)));
            var mine = Names(MessageFormat.Parse(translation));
            return english.SetEquals(mine);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static HashSet<string> Names(IReadOnlyList<MessageFormat.Part> parts, HashSet<string>? into = null)
    {
        into ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in parts)
        {
            if (p is MessageFormat.Arg a)
            {
                into.Add(a.Name);
                foreach (var o in a.Options.Values)
                {
                    Names(o, into);
                }
            }
        }

        return into;
    }

    /// <summary>This pack's strings for one codebase (web, apple, android, server).</summary>
    public LanguagePack Scoped(string? scope)
    {
        var letter = scope switch
        {
            "web" => 'w',
            "apple" => 'a',
            "android" => 'n',
            "server" => 's',
            _ => (char?)null,
        };
        if (letter is null)
        {
            return this;
        }

        return new LanguagePack
        {
            Code = Code,
            Name = Name,
            Direction = Direction,
            Version = Version,
            Coverage = Coverage,
            Messages = Messages.Where(m => Scopes.TryGetValue(m.Key, out var s) && s.Contains(letter.Value, StringComparison.Ordinal))
                .ToDictionary(m => m.Key, m => m.Value, StringComparer.Ordinal),
        };
    }
}

public static class LanguageCode
{
    /// <summary>A BCP 47 code of the shape Flexarr uses: de, pt-BR, zh-Hans, es-419, en-XA.</summary>
    public static bool IsValid(string? code) =>
        code is { Length: >= 2 and <= 16 } && System.Text.RegularExpressions.Regex.IsMatch(code,
            "^[a-z]{2,3}(-[A-Z][a-z]{3})?(-([A-Z]{2}|[0-9]{3}))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(50));

    /// <summary>de-AT, then de: the codes to try for a language, most specific first.</summary>
    public static IEnumerable<string> Fallbacks(string code)
    {
        var c = code;
        while (true)
        {
            yield return c;
            var cut = c.LastIndexOf('-');
            if (cut < 0)
            {
                yield break;
            }

            c = c[..cut];
        }
    }
}
