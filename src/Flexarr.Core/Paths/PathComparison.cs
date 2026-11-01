using System.Globalization;
using System.Text;

namespace Flexarr.Core.Paths;

public static class PathComparison
{
    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or inside it (whole segments only). Both are made
    /// absolute with <c>.</c> and <c>..</c> resolved first, so <c>/tv/../../etc</c> isn't under <c>/tv</c>, and
    /// compared in composed Unicode, as macOS may hand back "é" decomposed.
    /// </summary>
    public static bool IsUnder(string path, string root) => IsUnder(path, root, StringComparison.Ordinal);

    /// <summary>
    /// How this machine's file system compares names: ignoring case on macOS (APFS and HFS+ as they're set up out of
    /// the box) and Windows, exactly elsewhere. For telling whether two paths someone typed, or another app reported,
    /// name the same file; not for paths Flexarr itself found, which are always spelled as on disk.
    /// </summary>
    public static StringComparison FileSystemComparison { get; } =
        OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>Compares <see cref="Key"/>s as <see cref="FileSystemComparison"/> does.</summary>
    public static StringComparer FileSystemKeyComparer { get; } =
        FileSystemComparison == StringComparison.OrdinalIgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary><see cref="IsUnder(string, string)"/>, comparing names as <paramref name="comparison"/> says.</summary>
    public static bool IsUnder(string path, string root, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        // Not a usable path (e.g. contains a null character): under nothing.
        if (TryKey(path) is not { } pathKey || TryKey(root) is not { } rootKey)
        {
            return false;
        }

        return pathKey.StartsWith(rootKey, comparison)
            && (pathKey.Length == rootKey.Length || pathKey[rootKey.Length] is '/' or '\\' || rootKey is "/");
    }

    /// <summary>
    /// The form two spellings of the same path share: absolute, <c>.</c>/<c>..</c> resolved, composed Unicode (NFC),
    /// no trailing separator. For comparing only; open files by the path as found on disk. A path that isn't usable
    /// (a control character, broken Unicode) is its own key, unchanged, rather than an exception.
    /// </summary>
    public static string Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return TryKey(path) ?? path;
    }

    /// <summary>
    /// True when the path holds a control character (a null, a line break), a Unicode line or paragraph separator, or a
    /// bidi embedding, override or isolate: no real file's path needs one, path APIs throw on some, and in a log line
    /// they could forge entries or disguise what's written. Paths sent to Flexarr are refused with them.
    /// </summary>
    public static bool HasControlCharacters(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Any(c => char.IsControl(c) || IsSeparator(c) || IsBidiControl(c));
    }

    /// <summary>
    /// <paramref name="text"/> with any control characters, line or paragraph separators and invisible formatting
    /// characters replaced by <c>?</c>, safe to write to a log. Zero-width joiners and non-joiners stay: real names
    /// (emoji, Persian, Indic scripts) use them.
    /// </summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Any(IsUnprintable) ? new string([.. text.Select(c => IsUnprintable(c) ? '?' : c)]) : text;
    }

    private static bool IsSeparator(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator;

    /// <summary>U+202A–U+202E and U+2066–U+2069, which reorder the text around them.</summary>
    private static bool IsBidiControl(char c) => c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';

    private static bool IsUnprintable(char c) => char.IsControl(c) || IsSeparator(c)
        || (char.GetUnicodeCategory(c) == UnicodeCategory.Format && c is not ('\u200C' or '\u200D'));

    private static string? TryKey(string path)
    {
        // Only control characters: a file on disk with a stray formatting character in its name still scans.
        if (path.Any(char.IsControl))
        {
            return null;
        }

        try
        {
            path = Path.GetFullPath(path).Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            return null; // Broken Unicode (a lone surrogate), say.
        }

        return path.Length > 1 ? path.TrimEnd('/', '\\') : path;
    }
}
