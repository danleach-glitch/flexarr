using System.Globalization;
using System.Text;

namespace Flexarr.Core.Localization;

/// <summary>
/// The ICU MessageFormat subset Flexarr's messages use (docs/languages.md): text, {value}, {value, number},
/// {value, plural, …} with # and =N, {value, select, …}, and ICU's apostrophe quoting. scripts/i18n/icu.py is the
/// reference; languages/tests/format-cases.json keeps the two in step.
/// </summary>
public static class MessageFormat
{
    public abstract record Part;

    public sealed record Text(string Value) : Part;

    public sealed record Pound : Part;

    public sealed record Arg(string Name, string Kind, IReadOnlyDictionary<string, IReadOnlyList<Part>> Options) : Part;

    /// <summary>A message's parts. Throws <see cref="FormatException"/> on a message that can't be read.</summary>
    public static IReadOnlyList<Part> Parse(string message)
    {
        var pos = 0;
        var parts = ParseParts(message, ref pos, inPlural: false, nested: false);
        if (pos != message.Length)
        {
            throw new FormatException($"Unexpected '}}' at {pos}");
        }

        return parts;
    }

    /// <summary>The message with its values filled in; a value that isn't given prints as {name}.</summary>
    public static string Format(IReadOnlyList<Part> parts, string language, Func<string, (bool Found, object? Value)> value)
    {
        var culture = CultureFor(language);
        var sb = new StringBuilder();
        Write(sb, parts, language, culture, value, pound: null);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, IReadOnlyList<Part> parts, string language, CultureInfo culture,
        Func<string, (bool Found, object? Value)> value, double? pound)
    {
        foreach (var part in parts)
        {
            switch (part)
            {
                case Text t:
                    sb.Append(t.Value);
                    break;
                case Pound:
                    sb.Append(pound is { } p ? FormatNumber(p, culture) : "#");
                    break;
                case Arg a:
                    var (found, v) = value(a.Name);
                    if (!found)
                    {
                        sb.Append('{').Append(a.Name).Append('}');
                        break;
                    }

                    switch (a.Kind)
                    {
                        case "plural":
                            var n = ToDouble(v);
                            var exact = n % 1 == 0 ? "=" + n.ToString("0", CultureInfo.InvariantCulture) : null;
                            var chosen = exact is not null && a.Options.TryGetValue(exact, out var e)
                                ? e
                                : a.Options.TryGetValue(Plurals.Category(language, n).ToString().ToLowerInvariant(), out var c)
                                    ? c
                                    : a.Options["other"];
                            Write(sb, chosen, language, culture, value, n);
                            break;
                        case "select":
                            var key = Convert.ToString(v, CultureInfo.InvariantCulture) ?? "";
                            Write(sb, a.Options.TryGetValue(key, out var s) ? s : a.Options["other"], language, culture, value, pound);
                            break;
                        default:
                            sb.Append(IsNumber(v) ? FormatNumber(ToDouble(v), culture) : Convert.ToString(v, culture));
                            break;
                    }

                    break;
            }
        }
    }

    private static bool IsNumber(object? v) => v is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static double ToDouble(object? v) => v is null ? 0 : Convert.ToDouble(v, CultureInfo.InvariantCulture);

    private static string FormatNumber(double n, CultureInfo culture) =>
        n % 1 == 0 ? n.ToString("#,0", culture) : n.ToString("#,0.###", culture);

    private static readonly Dictionary<string, CultureInfo> Cultures = new(StringComparer.OrdinalIgnoreCase);

    public static CultureInfo CultureFor(string language)
    {
        lock (Cultures)
        {
            if (!Cultures.TryGetValue(language, out var culture))
            {
                try
                {
                    culture = CultureInfo.GetCultureInfo(language == "en-XA" ? "en" : language);
                }
                catch (CultureNotFoundException)
                {
                    culture = CultureInfo.InvariantCulture;
                }

                Cultures[language] = culture;
            }

            return culture;
        }
    }

    // ---- parsing (the same steps as icu.py) -----------------------------------------------------------------------

    /// <summary>
    /// How deep a value may sit inside others' options. Flexarr's own go two deep; a pack nesting thousands would
    /// overflow the stack while it's checked, which ends the server.
    /// </summary>
    internal const int MaxDepth = 8;

    private static List<Part> ParseParts(string s, ref int pos, bool inPlural, bool nested, int depth = 0)
    {
        var parts = new List<Part>();
        var text = new StringBuilder();
        void Flush()
        {
            if (text.Length > 0)
            {
                parts.Add(new Text(text.ToString()));
                text.Clear();
            }
        }

        while (pos < s.Length)
        {
            var c = s[pos];
            if (c == '\'')
            {
                if (pos + 1 < s.Length && s[pos + 1] == '\'')
                {
                    text.Append('\'');
                    pos += 2;
                    continue;
                }

                if (pos + 1 < s.Length && (s[pos + 1] is '{' or '}' || (inPlural && s[pos + 1] == '#')))
                {
                    var end = pos + 1;
                    while (true)
                    {
                        if (end >= s.Length)
                        {
                            throw new FormatException("Unterminated quote");
                        }

                        if (s[end] == '\'')
                        {
                            if (end + 1 < s.Length && s[end + 1] == '\'')
                            {
                                text.Append('\'');
                                end += 2;
                                continue;
                            }

                            break;
                        }

                        text.Append(s[end]);
                        end++;
                    }

                    pos = end + 1;
                    continue;
                }

                text.Append('\'');
                pos++;
            }
            else if (c == '{')
            {
                Flush();
                pos++;
                parts.Add(ParseArg(s, ref pos, depth + 1));
            }
            else if (c == '}')
            {
                if (!nested)
                {
                    throw new FormatException($"Unexpected '}}' at {pos}");
                }

                break;
            }
            else if (c == '#' && inPlural)
            {
                Flush();
                parts.Add(new Pound());
                pos++;
            }
            else
            {
                text.Append(c);
                pos++;
            }
        }

        Flush();
        return parts;
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && char.IsWhiteSpace(s[pos]))
        {
            pos++;
        }
    }

    private static string Word(string s, ref int pos)
    {
        var start = pos;
        while (pos < s.Length && (char.IsLetterOrDigit(s[pos]) || s[pos] is '_' or '-' or '='))
        {
            pos++;
        }

        return s[start..pos];
    }

    private static bool IsName(string w) => w.Length > 0 && (char.IsAsciiLetter(w[0]) || w[0] == '_') && w.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '_');

    private static bool IsSelector(string w) =>
        w.Length > 1 && w[0] == '=' ? w[1..].All(char.IsAsciiDigit) : w.Length > 0 && (char.IsAsciiLetter(w[0]) || w[0] == '_') && w.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    private static Arg ParseArg(string s, ref int pos, int depth)
    {
        if (depth > MaxDepth)
        {
            throw new FormatException($"Values nested more than {MaxDepth} deep");
        }

        SkipSpace(s, ref pos);
        var name = Word(s, ref pos);
        if (!IsName(name))
        {
            throw new FormatException($"Bad value name '{name}'");
        }

        SkipSpace(s, ref pos);
        if (pos < s.Length && s[pos] == '}')
        {
            pos++;
            return new Arg(name, "simple", new Dictionary<string, IReadOnlyList<Part>>());
        }

        if (pos >= s.Length || s[pos] != ',')
        {
            throw new FormatException($"Expected ',' or '}}' after {{{name}");
        }

        pos++;
        SkipSpace(s, ref pos);
        var kind = Word(s, ref pos);
        SkipSpace(s, ref pos);
        if (kind == "number")
        {
            if (pos < s.Length && s[pos] == '}')
            {
                pos++;
                return new Arg(name, "number", new Dictionary<string, IReadOnlyList<Part>>());
            }

            throw new FormatException($"{{{name}, number}} takes no style");
        }

        if (kind is not ("plural" or "select"))
        {
            throw new FormatException($"Unsupported type '{kind}' in {{{name}}}");
        }

        if (pos >= s.Length || s[pos] != ',')
        {
            throw new FormatException($"Expected ',' after {{{name}, {kind}");
        }

        pos++;
        var options = new Dictionary<string, IReadOnlyList<Part>>(StringComparer.Ordinal);
        while (true)
        {
            SkipSpace(s, ref pos);
            if (pos < s.Length && s[pos] == '}')
            {
                pos++;
                break;
            }

            var sel = Word(s, ref pos);
            if (!IsSelector(sel) || (kind == "select" && sel.StartsWith('=')))
            {
                throw new FormatException($"Bad selector '{sel}' in {{{name}}}");
            }

            if (options.ContainsKey(sel))
            {
                throw new FormatException($"Selector '{sel}' twice in {{{name}}}");
            }

            SkipSpace(s, ref pos);
            if (pos >= s.Length || s[pos] != '{')
            {
                throw new FormatException($"Expected '{{' after '{sel}' in {{{name}}}");
            }

            pos++;
            var sub = ParseParts(s, ref pos, kind == "plural", nested: true, depth);
            if (pos >= s.Length || s[pos] != '}')
            {
                throw new FormatException($"Unclosed option '{sel}' in {{{name}}}");
            }

            pos++;
            options[sel] = sub;
        }

        if (!options.ContainsKey("other"))
        {
            throw new FormatException($"{{{name}, {kind}}} needs an 'other'");
        }

        return new Arg(name, kind, options);
    }
}
