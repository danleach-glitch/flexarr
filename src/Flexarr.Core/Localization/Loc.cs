using System.Collections.Concurrent;
using System.Reflection;

namespace Flexarr.Core.Localization;

/// <summary>
/// The packs this server can show: the ones built into it (Localization/Packs, embedded) and the ones it downloaded,
/// the newer version of each language winning. Swapped whole, so lookups never lock.
/// </summary>
public sealed class LanguageCatalog
{
    private volatile Dictionary<string, LanguagePack> _packs = new Dictionary<string, LanguagePack>(StringComparer.Ordinal);
    private readonly object _write = new();

    public static LanguageCatalog Shared { get; } = WithBundled();

    public IReadOnlyDictionary<string, LanguagePack> Packs => _packs;

    /// <summary>Adds a pack, or replaces that language's when this one is newer. True when it's now the one used.</summary>
    public bool Install(LanguagePack pack)
    {
        lock (_write)
        {
            if (_packs.TryGetValue(pack.Code, out var have) && have.Version >= pack.Version)
            {
                return false;
            }

            _packs = new Dictionary<string, LanguagePack>(_packs, StringComparer.Ordinal) { [pack.Code] = pack };
            Loc.ClearCache(pack.Code);
            return true;
        }
    }

    /// <summary>The pack for a language, trying de-AT then de; null for English or a language with no pack.</summary>
    public LanguagePack? Find(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return null;
        }

        var packs = _packs;
        foreach (var c in LanguageCode.Fallbacks(code))
        {
            if (packs.TryGetValue(c, out var pack))
            {
                return pack;
            }
        }

        return null;
    }

    public static IEnumerable<LanguagePack> ReadBundled()
    {
        var assembly = typeof(LanguageCatalog).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.StartsWith("languages/", StringComparison.Ordinal)))
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            yield return LanguagePack.Read(ms.ToArray());
        }
    }

    private static LanguageCatalog WithBundled()
    {
        var catalog = new LanguageCatalog();
        foreach (var pack in ReadBundled())
        {
            catalog.Install(pack);
        }

        return catalog;
    }
}

/// <summary>
/// The server's own words in the language of whoever it's answering (docs/languages.md, *Writing a string*):
/// <c>Loc.T("Couldn't find that movie any more.")</c>, with values <c>Loc.T("{name} is offline", new { name })</c>.
/// The English is the key, so <c>make strings</c> collects it from the code; a language without that string, or
/// whose translation can't be read, shows the English.
/// </summary>
public static class Loc
{
    private static readonly AsyncLocal<string?> Current = new();
    private static readonly ConcurrentDictionary<(string Language, string Key), IReadOnlyList<MessageFormat.Part>?> Parsed = new();
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> Properties = new();

    /// <summary>The language this request or job is in (a BCP 47 code), or null for English.</summary>
    public static string? Language
    {
        get => Current.Value;
        set => Current.Value = value;
    }

    /// <summary>Runs the rest of this flow in a language, e.g. a push notification in its profile's language.</summary>
    public static IDisposable Use(string? language)
    {
        var before = Current.Value;
        Current.Value = language;
        return new Restore(before);
    }

    public static LanguageCatalog Catalog { get; set; } = LanguageCatalog.Shared;

    /// <summary>Marks a string for <c>make strings</c> without looking it up, for tables looked up later with T.</summary>
    public static string Mark(string english) => english;

    /// <summary>The English a key shows: the key without its context.</summary>
    public static string English(string key)
    {
        var sep = key.IndexOf("::", StringComparison.Ordinal);
        if (sep <= 0)
        {
            return key;
        }

        for (var i = 0; i < sep; i++)
        {
            if (!(char.IsAsciiLetterLower(key[i]) || (key[i] == '-' && i > 0)))
            {
                return key;
            }
        }

        return key[(sep + 2)..];
    }

    public static string T(string english, object? args = null, string? ctx = null) => Translate(Language, english, args, ctx);

    private static string Translate(string? language, string english, object? args, string? ctx)
    {
        var key = ctx is null ? english : $"{ctx}::{english}";
        var pack = Catalog.Find(language);
        var value = Values(args);
        if (pack is not null && pack.Messages.TryGetValue(key, out var translation))
        {
            var parts = Parsed.GetOrAdd((pack.Code, key), static (k, t) => TryParse(t), translation);
            if (parts is not null)
            {
                try
                {
                    return MessageFormat.Format(parts, pack.Code, value);
                }
                catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
                {
                    // Falls through to the English.
                }
            }
        }

        var source = Parsed.GetOrAdd(("en", key), static (k, e) => TryParse(e), english);
        return source is null ? english : MessageFormat.Format(source, "en", value);
    }

    internal static void ClearCache(string language)
    {
        foreach (var k in Parsed.Keys.Where(k => k.Language == language))
        {
            Parsed.TryRemove(k, out _);
        }
    }

    private static IReadOnlyList<MessageFormat.Part>? TryParse(string message)
    {
        try
        {
            return MessageFormat.Parse(message);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Func<string, (bool, object?)> Values(object? args)
    {
        switch (args)
        {
            case null:
                return static _ => (false, null);
            case IReadOnlyDictionary<string, object?> d:
                return name => d.TryGetValue(name, out var v) ? (true, v) : (false, null);
            default:
                var props = Properties.GetOrAdd(args.GetType(), static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance));
                return name =>
                {
                    foreach (var p in props)
                    {
                        if (p.Name == name)
                        {
                            return (true, p.GetValue(args));
                        }
                    }

                    return (false, null);
                };
        }
    }

    private sealed class Restore(string? before) : IDisposable
    {
        public void Dispose() => Current.Value = before;
    }
}
