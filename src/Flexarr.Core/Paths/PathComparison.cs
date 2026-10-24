using System.Text;

namespace Flexarr.Core.Paths;

public static class PathComparison
{
    /// <summary>
    /// True when <paramref name="path"/> is <paramref name="root"/> or inside it (whole segments only). Both are made
    /// absolute with <c>.</c> and <c>..</c> resolved first, so <c>/tv/../../etc</c> isn't under <c>/tv</c>, and
    /// compared in composed Unicode, as macOS may hand back "é" decomposed.
    /// </summary>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        // Not a usable path (e.g. contains a null character): under nothing.
        if (TryKey(path) is not { } pathKey || TryKey(root) is not { } rootKey)
        {
            return false;
        }

        return pathKey.StartsWith(rootKey, StringComparison.Ordinal)
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
    /// True when the path holds a control character (a null, a line break): no real file's path does, path APIs throw
    /// on some, and in a log line they could forge entries. Paths sent to Flexarr are refused with them.
    /// </summary>
    public static bool HasControlCharacters(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return path.Any(char.IsControl);
    }

    /// <summary><paramref name="text"/> with any control characters replaced by <c>?</c>, safe to write to a log.</summary>
    public static string Printable(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return HasControlCharacters(text) ? new string([.. text.Select(c => char.IsControl(c) ? '?' : c)]) : text;
    }

    private static string? TryKey(string path)
    {
        if (HasControlCharacters(path))
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
