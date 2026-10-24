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

        var from = Trim(best.From);
        return Trim(best.To) + path[from.Length..];
    }

    /// <summary>The reverse of <see cref="Map"/>: a path as Flexarr sees it, as the other app sees it.</summary>
    public static string Unmap(string path, IEnumerable<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(mappings);

        var best = mappings
            .Where(m => !string.IsNullOrWhiteSpace(m.To) && PathComparison.IsUnder(path, Trim(m.To)))
            .MaxBy(m => Trim(m.To).Length);

        if (best is null)
        {
            return path;
        }

        var to = Trim(best.To);
        return Trim(best.From) + path[to.Length..];
    }

    private static string Trim(string path) => path.Length > 1 ? path.TrimEnd('/', '\\') : path;
}
