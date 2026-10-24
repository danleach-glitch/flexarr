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
            .Where(m => !string.IsNullOrWhiteSpace(m.From) && PathComparison.IsUnder(path, Trim(m.From)))
            .MaxBy(m => Trim(m.From).Length);

        if (best is null)
        {
            return path;
        }

        // The match ignores "//", "." and Unicode spelling, so the raw path can't be cut at From's length: skip From's
        // segments instead, keeping the rest as it was sent (the scanner finds the on-disk spelling).
        var skip = PathComparison.Key(Trim(best.From)).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var rest = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(skip).ToList();
        return rest.Count == 0 ? Trim(best.To) : $"{Trim(best.To).TrimEnd('/')}/{string.Join('/', rest)}";
    }

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}
