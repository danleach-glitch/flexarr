using System.Text.RegularExpressions;

namespace Flexarr.Core.Scanning;

/// <summary>Finds playable video files under a library folder, skipping extras, samples and hidden files.</summary>
public static partial class MediaFileEnumerator
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg",
    };

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <param name="root">The library folder.</param>
    /// <param name="scope">A folder or file under <paramref name="root"/> to limit the walk to; null for everything.</param>
    public static IEnumerable<FileInfo> Enumerate(string root, string? scope = null)
    {
        scope ??= root;
        if (File.Exists(scope))
        {
            return IsVideo(scope) ? [new FileInfo(scope)] : [];
        }

        return Directory.Exists(scope) ? Walk(root, scope) : [];
    }

    private static IEnumerable<FileInfo> Walk(string root, string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", Options))
        {
            if (IsVideo(file) && !IsSample(file))
            {
                yield return new FileInfo(file);
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", Options))
        {
            // Depth 1 is the movie/series folder itself — a show can legitimately be called "Extras".
            var depth = Path.GetRelativePath(root, child).Count(c => c == Path.DirectorySeparatorChar) + 1;
            if (depth > 1 && ExtrasFolder().IsMatch(Path.GetFileName(child)))
            {
                continue;
            }

            foreach (var file in Walk(root, child))
            {
                yield return file;
            }
        }
    }

    private static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

    private static bool IsSample(string path) => SampleFile().IsMatch(Path.GetFileNameWithoutExtension(path));

    [GeneratedRegex(@"\b(?:extras?|featurettes?|behind[ ._-]the[ ._-]scenes|deleted[ ._-]scenes|interviews?|trailers?|samples?|shorts|bonus|other)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExtrasFolder();

    [GeneratedRegex(@"(?:^|[ ._-])(?:sample|trailer)$", RegexOptions.IgnoreCase)]
    private static partial Regex SampleFile();
}
