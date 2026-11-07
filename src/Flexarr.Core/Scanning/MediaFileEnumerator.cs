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

    /// <summary>
    /// Audible's DRM-protected books. Found so library health can say why they don't play, never probed or played.
    /// </summary>
    public static readonly IReadOnlySet<string> ProtectedBookExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".aax", ".aaxc" };

    /// <summary>What library health says about a protected book file.</summary>
    public const string ProtectedBookError = "Protected by Audible's DRM, so it can't be played";

    private static readonly IReadOnlySet<string> BookExtensions = new HashSet<string>(AudioExtensions.Concat(ProtectedBookExtensions), StringComparer.OrdinalIgnoreCase);

    // Not IgnoreInaccessible: a folder that can't be read would then look empty, its files deleted. Each folder's
    // listing is tried on its own instead, and one that fails is reported as unreadable.
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
    };

    /// <param name="root">The library folder.</param>
    /// <param name="scope">A folder or file under <paramref name="root"/> to limit the walk to; null for everything.</param>
    /// <param name="audio">Find audio files (music libraries) instead of video.</param>
    /// <param name="everything">
    /// Keep samples, trailers and extras folders too: in home videos, "Trailer.mp4" or a folder called Bonus is just
    /// what someone called their own video.
    /// </param>
    /// <param name="books">Audiobooks: audio files and Audible's protected ones.</param>
    /// <param name="unreadable">
    /// Gets the folders that couldn't be listed (permissions, a share having trouble): their files weren't seen, which
    /// doesn't mean they've gone.
    /// </param>
    public static IEnumerable<FileInfo> Enumerate(string root, string? scope = null, bool audio = false, bool everything = false, bool books = false,
        ICollection<string>? unreadable = null)
    {
        scope ??= root;
        var extensions = books ? BookExtensions : audio ? AudioExtensions : VideoExtensions;
        if (File.Exists(scope))
        {
            return IsMedia(scope, extensions) ? [new FileInfo(scope)] : [];
        }

        return Directory.Exists(scope) ? Walk(root, scope, extensions, everything, unreadable) : [];
    }

    private static IEnumerable<FileInfo> Walk(string root, string directory, IReadOnlySet<string> extensions, bool everything, ICollection<string>? unreadable)
    {
        List<string> files, children;
        try
        {
            files = Directory.EnumerateFiles(directory, "*", Options).ToList();
            children = Directory.EnumerateDirectories(directory, "*", Options).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            unreadable?.Add(directory);
            yield break;
        }

        foreach (var file in files)
        {
            if (IsMedia(file, extensions) && (everything || !IsSample(file)))
            {
                yield return new FileInfo(file);
            }
        }

        foreach (var child in children)
        {
            // Depth 1 is the movie/series folder itself — a show can legitimately be called "Extras".
            var depth = Path.GetRelativePath(root, child).Count(c => c == Path.DirectorySeparatorChar) + 1;
            if (!everything && depth > 1 && ExtrasFolder().IsMatch(Path.GetFileName(child)))
            {
                continue;
            }

            foreach (var file in Walk(root, child, extensions, everything, unreadable))
            {
                yield return file;
            }
        }
    }

    /// <summary>
    /// Every folder <see cref="Enumerate"/> would look in under <paramref name="root"/> (the root first), with the
    /// same folders left out; a folder that can't be listed is given, but not what's in it.
    /// </summary>
    public static IEnumerable<DirectoryInfo> Folders(string root, bool everything = false)
    {
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.TryPop(out var directory))
        {
            yield return directory;
            List<string> children;
            try
            {
                children = Directory.EnumerateDirectories(directory.FullName, "*", Options).ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var child in children)
            {
                var depth = Path.GetRelativePath(root, child).Count(c => c == Path.DirectorySeparatorChar) + 1;
                if (everything || depth <= 1 || !ExtrasFolder().IsMatch(Path.GetFileName(child)))
                {
                    pending.Push(new DirectoryInfo(child));
                }
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
