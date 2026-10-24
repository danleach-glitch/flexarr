namespace Flexarr.Core.Paths;

public static class PathComparison
{
    /// <summary>True when <paramref name="path"/> is <paramref name="root"/> or inside it (whole segments only).</summary>
    public static bool IsUnder(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(root);

        root = root.Length > 1 ? root.TrimEnd('/', '\\') : root;
        return path.StartsWith(root, StringComparison.Ordinal)
            && (path.Length == root.Length || path[root.Length] is '/' or '\\' || root is "/");
    }
}
