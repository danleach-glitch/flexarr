using System.Text;

namespace Flexarr.Core.Paths;

/// <summary>
/// A mapping worked out from titles both sides have: <paramref name="Agreeing"/> of the <paramref name="Compared"/> titles
/// whose folders were paired up are explained by it.
/// </summary>
public sealed record DetectedMapping(string From, string To, int Agreeing, int Compared)
{
    /// <summary>Enough titles, nearly all agreeing: safe to save without asking.</summary>
    public bool Certain => Agreeing >= 3 && Agreeing * 10 >= Compared * 9;
}

public static class PathMappingDetector
{
    /// <summary>
    /// The mapping for one of another app's root folders, from the folders it and Flexarr keep the same titles in: Sonarr's
    /// <c>/tv/Severance (2022)</c> and Flexarr's <c>/Volumes/Media/TV/Severance (2022)</c> share their last segment, so
    /// what's left of each (<c>/tv</c>, <c>/Volumes/Media/TV</c>) is the mapping. Titles are paired by folder name, only
    /// where the name is unique on both sides, and the explanation most of them agree on wins. Null when nothing pairs
    /// up, or when most titles are already at the same path on both sides.
    /// </summary>
    /// <param name="root">The other app's root folder, as it sees it.</param>
    /// <param name="theirs">The other app's title folders (series, films, artists), as it sees them.</param>
    /// <param name="ours">Flexarr's folders for the same kind of titles.</param>
    public static DetectedMapping? Infer(string root, IEnumerable<string> theirs, IEnumerable<string> ours)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(theirs);
        ArgumentNullException.ThrowIfNull(ours);

        var rootSegments = Segments(root);
        var theirByName = UniqueByName(theirs.Where(t => PathComparison.IsUnder(t, root)));
        var ourByName = UniqueByName(ours);

        var votes = new Dictionary<(string From, string To), int>();
        var compared = 0;
        foreach (var (name, their) in theirByName)
        {
            if (!ourByName.TryGetValue(name, out var our))
            {
                continue;
            }

            var shared = 0;
            while (shared < their.Length && shared < our.Length && their[^(shared + 1)] == our[^(shared + 1)])
            {
                shared++;
            }

            compared++;
            var key = (Join(their[..^shared]), Join(our[..^shared]));
            votes[key] = votes.GetValueOrDefault(key) + 1;
        }

        if (votes.Count == 0)
        {
            return null;
        }

        var ((from, to), agreeing) = votes.MaxBy(v => v.Value);
        if (from == to)
        {
            return null;
        }

        // Said of the root where it can be (/data/tv → /Volumes/Media/data/tv rather than / → /Volumes/Media), so the
        // mapping reads as the folder the app shows.
        var fromSegments = Segments(from);
        if (fromSegments.Length <= rootSegments.Length && PathComparison.IsUnder(root, from))
        {
            to = Join([.. Segments(to), .. rootSegments[fromSegments.Length..]]);
            from = Join(rootSegments);
        }

        return new DetectedMapping(from, to, agreeing, compared);
    }

    /// <summary>Each folder's segments by its last one, leaving out names that turn up more than once.</summary>
    private static Dictionary<string, string[]> UniqueByName(IEnumerable<string> folders)
    {
        var byName = new Dictionary<string, string[]?>(StringComparer.Ordinal);
        foreach (var segments in folders.Select(Segments).Where(s => s.Length > 0))
        {
            byName[segments[^1]] = byName.ContainsKey(segments[^1]) ? null : segments;
        }

        return byName.Where(e => e.Value is not null).ToDictionary(e => e.Key, e => e.Value!, StringComparer.Ordinal);
    }

    private static string[] Segments(string path) =>
        PathComparison.Key(path).Normalize(NormalizationForm.FormC).Split('/', StringSplitOptions.RemoveEmptyEntries);

    private static string Join(IEnumerable<string> segments) => "/" + string.Join('/', segments);
}
