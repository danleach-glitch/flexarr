using System.Text.RegularExpressions;

namespace Flexarr.Core.Scanning;

/// <summary>Finds playable video files under a library folder, skipping extras, samples and hidden files.</summary>
public static partial class MediaFileEnumerator
{
    public static readonly IReadOnlySet<string> VideoExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".m4v", ".avi", ".mov", ".wmv", ".ts", ".m2ts", ".webm", ".mpg", ".mpeg",
    };

    public static readonly IReadOnlySet<string> AudioExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".m4a", ".m4b", ".aac", ".ogg", ".opus", ".wav", ".aiff", ".aif", ".wma", ".alac", ".ape", ".wv",
    };

    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <param name="root">The library folder.</param>
    /// <param name="scope">A folder or file under <paramref name="root"/> to limit the walk to; null for everything.</param>
    /// <param name="audio">Find audio files (music libraries) instead of video.</param>
    /// <param name="everything">
    /// Keep samples, trailers and extras folders too: in home videos, "Trailer.mp4" or a folder called Bonus is just
    /// what someone called their own video.
    /// </param>
    public static IEnumerable<FileInfo> Enumerate(string root, string? scope = null, bool audio = false, bool everything = false)
    {
        scope ??= root;
        var extensions = audio ? AudioExtensions : VideoExtensions;
        if (File.Exists(scope))
        {
            return IsMedia(scope, extensions) ? [new FileInfo(scope)] : [];
        }

        return Directory.Exists(scope) ? Walk(root, scope, extensions, everything) : [];
    }

    private static IEnumerable<FileInfo> Walk(string root, string directory, IReadOnlySet<string> extensions, bool everything)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", Options))
        {
            if (IsMedia(file, extensions) && (everything || !IsSample(file)))
            {
                yield return new FileInfo(file);
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory, "*", Options))
        {
            // Depth 1 is the movie/series folder itself — a show can legitimately be called "Extras".
            var depth = Path.GetRelativePath(root, child).Count(c => c == Path.DirectorySeparatorChar) + 1;
            if (!everything && depth > 1 && ExtrasFolder().IsMatch(Path.GetFileName(child)))
            {
                continue;
            }

            foreach (var file in Walk(root, child, extensions, everything))
            {
                yield return file;
            }
        }
    }

    private static bool IsMedia(string path, IReadOnlySet<string> extensions) =>
        extensions.Contains(Path.GetExtension(path)) && !Path.GetFileName(path).StartsWith("._", StringComparison.Ordinal);

    private static bool IsSample(string path) => SampleFile().IsMatch(Path.GetFileNameWithoutExtension(path));

    [GeneratedRegex(@"\b(?:extras?|featurettes?|behind[ ._-]the[ ._-]scenes|deleted[ ._-]scenes|interviews?|trailers?|samples?|shorts|bonus|other)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ExtrasFolder();

    [GeneratedRegex(@"(?:^|[ ._-])(?:sample|trailer)$", RegexOptions.IgnoreCase)]
    private static partial Regex SampleFile();
}
