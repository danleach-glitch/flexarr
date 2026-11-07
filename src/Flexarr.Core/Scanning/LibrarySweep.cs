using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Scanning;

/// <summary>
/// A light look at a library folder between its full scans: only the folders whose time says something was added,
/// removed or renamed in them since, and only those are scanned. A folder's time changes when what's directly in it
/// does, so a new episode changes its season's folder and a new series the library folder itself; what's new or gone
/// there is told apart by the files the library knows. A file changed where it is (retagged, say) doesn't change its
/// folder's time: the daily full scan finds that, and anything a network share's times don't show.
/// </summary>
public static class LibrarySweep
{
    /// <summary>
    /// The folders and files under <paramref name="root"/> to scan for what changed since <paramref name="since"/>:
    /// each changed folder below the root (none inside another), and the root's own entries that are new or gone.
    /// Nothing when the root isn't there: a full scan works out whether that's a drive not mounted.
    /// </summary>
    public static async Task<List<string>> ChangedAsync(FlexarrDbContext db, Library library, string root, DateTime since, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(library);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var changed = new List<string>();
        var rootChanged = false;
        foreach (var folder in MediaFileEnumerator.Folders(root, everything: library.Type == LibraryType.HomeVideos))
        {
            ct.ThrowIfCancellationRequested();
            if (!Changed(folder, since))
            {
                continue;
            }

            if (PathComparison.Key(folder.FullName) == PathComparison.Key(root))
            {
                rootChanged = true;
            }
            else
            {
                changed.Add(folder.FullName);
            }
        }

        if (rootChanged)
        {
            changed.AddRange(await NewOrGoneAsync(db, library, root, ct).ConfigureAwait(false));
        }

        // A folder inside another being scanned is scanned with it.
        return changed.Distinct(StringComparer.Ordinal)
            .Where(c => !changed.Any(o => o != c && PathComparison.IsUnder(c, o)))
            .ToList();
    }

    /// <summary>A folder that changed since, or one whose time can't be read (looked at rather than missed).</summary>
    private static bool Changed(DirectoryInfo folder, DateTime since)
    {
        try
        {
            return folder.LastWriteTimeUtc >= since;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// The root's entries the library doesn't know (a series folder moved in keeps its own older time) and the ones it
    /// knows that have gone.
    /// </summary>
    private static async Task<List<string>> NewOrGoneAsync(FlexarrDbContext db, Library library, string root, CancellationToken ct)
    {
        var prefix = root.TrimEnd('/') + "/";
        var known = new Dictionary<string, string>(StringComparer.Ordinal); // key → the entry as the library spells it
        await foreach (var path in db.MediaFiles.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.MediaItem!.LibraryId == library.Id && f.MissingSince == null && f.Path.StartsWith(prefix))
            .Select(f => f.Path).AsAsyncEnumerable().WithCancellation(ct).ConfigureAwait(false))
        {
            var entry = prefix + path[prefix.Length..].Split('/', 2)[0];
            known.TryAdd(PathComparison.Key(entry), entry);
        }

        List<string> entries;
        try
        {
            entries = Directory.EnumerateFileSystemEntries(root, "*", new EnumerationOptions { AttributesToSkip = FileAttributes.Hidden | FileAttributes.System }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [root]; // Can't tell: the full scan of it says it can't be read, and removes nothing.
        }

        var onDisk = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            onDisk.TryAdd(PathComparison.Key(entry), entry);
        }

        var music = library.Type == LibraryType.Music;
        var books = library.Type == LibraryType.Audiobooks;
        var everything = library.Type == LibraryType.HomeVideos;
        var added = onDisk.Where(e => !known.ContainsKey(e.Key))
            .Select(e => e.Value)
            .Where(e => Directory.Exists(e) || MediaFileEnumerator.Enumerate(root, e, audio: music, everything: everything, books: books).Any());
        var gone = known.Where(k => !onDisk.ContainsKey(k.Key)).Select(k => k.Value);
        return [.. added, .. gone];
    }
}
