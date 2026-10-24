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

        try
        {
            path = Key(path);
            root = Key(root);
        }
        catch (ArgumentException)
        {
            return false; // Not a usable path (e.g. contains a null character).
        }

        return path.StartsWith(root, StringComparison.Ordinal)
            && (path.Length == root.Length || path[root.Length] is '/' or '\\' || root is "/");
    }

    /// <summary>
    /// The form two spellings of the same path share: absolute, <c>.</c>/<c>..</c> resolved, composed Unicode (NFC),
    /// no trailing separator. For comparing only; open files by the path as found on disk.
    /// </summary>
    public static string Key(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        path = Path.GetFullPath(path).Normalize(NormalizationForm.FormC);
        return path.Length > 1 ? path.TrimEnd('/', '\\') : path;
    }
}
