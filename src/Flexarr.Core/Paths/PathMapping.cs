namespace Flexarr.Core.Paths;

/// <summary>
/// Translates a path as another app sees it (e.g. Sonarr in Docker: <c>/data/tv</c>) into the path
/// Flexarr sees (e.g. <c>/Volumes/Media/data/tv</c>). Same idea as *arr "remote path mappings".
/// </summary>
public sealed class PathMapping
{
    public string From { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
}

public static class PathMapper
{
    /// <summary>Applies the mapping with the longest matching <see cref="PathMapping.From"/> prefix, if any.</summary>
    public static string Map(string path, IEnumerable<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mappings);

        var best = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.From) && IsUnder(path, Trim(m.From)))
            .MaxBy(m => Trim(m.From).Length);

        if (best is null)
        {
            return path;
        }

        var from = Trim(best.From);
        return Trim(best.To) + path[from.Length..];
    }

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root, StringComparison.Ordinal)
        && (path.Length == root.Length || path[root.Length] is '/' or '\\');

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}
