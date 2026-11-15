using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Scanning;

/// <summary>
/// What happens to a library's titles when a folder is taken out of it. Nothing is deleted there and then: a folder
/// swapped for another holding the same files (the share mounted somewhere new, say) has its files' and titles' paths
/// moved across, so they keep everything (plays, playlists, progress, bookmarks, fix-match and edits). Files of a
/// folder taken out with nowhere to go are marked missing, and wait out <see cref="LibraryScanner.MissingGracePeriod"/>
/// as any missing file does: put back in time, they come back as they were, and a film or show found again elsewhere
/// takes its history with it (<see cref="WatchHistoryCarryOver"/>).
/// </summary>
public static class LibraryFolderChanges
{
    /// <param name="db">The database.</param>
    /// <param name="libraryId">The library.</param>
    /// <param name="removed">Folders taken out of it.</param>
    /// <param name="added">Folders put in.</param>
    /// <param name="folders">All its folders now: files under one of them stay as they are.</param>
    /// <param name="ct">Stops it.</param>
    /// <returns>How many files were moved to an added folder.</returns>
    public static async Task<int> ApplyAsync(FlexarrDbContext db, Guid libraryId, IReadOnlyCollection<string> removed, IReadOnlyCollection<string> added,
        IReadOnlyCollection<string> folders, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(removed);
        ArgumentNullException.ThrowIfNull(added);
        ArgumentNullException.ThrowIfNull(folders);
        var now = DateTime.UtcNow;
        var moved = 0;
        foreach (var folder in removed)
        {
            var prefix = folder.TrimEnd('/') + "/";
            // StartsWith is a LIKE, which ignores case: the exact test is made here.
            var files = (await db.MediaFiles.IgnoreQueryFilters()
                    .Where(f => f.MediaItem!.LibraryId == libraryId && (f.Path == folder || f.Path.StartsWith(prefix)))
                    .ToListAsync(ct).ConfigureAwait(false))
                .Where(f => PathComparison.IsUnder(f.Path, folder) && !folders.Any(r => PathComparison.IsUnder(f.Path, r)))
                .ToList();
            if (files.Count == 0)
            {
                continue;
            }

            // The new folder holding most of these files, at the same places under it.
            var to = added.Select(a => (Folder: a, Found: files.Count(f => File.Exists(Moved(f.Path, folder, a)))))
                .Where(t => t.Found > 0).OrderByDescending(t => t.Found).Select(t => t.Folder).FirstOrDefault();
            var taken = to is null ? [] : (await db.MediaFiles.IgnoreQueryFilters().Where(f => f.Path.StartsWith(to.TrimEnd('/') + "/"))
                .Select(f => f.Path).ToListAsync(ct).ConfigureAwait(false)).ToHashSet(StringComparer.Ordinal);
            foreach (var file in files)
            {
                if (to is not null && Moved(file.Path, folder, to) is var path && File.Exists(path) && taken.Add(path))
                {
                    file.Path = path;
                    file.MissingSince = null;
                    moved++;
                }
                else
                {
                    file.MissingSince ??= now;
                }
            }

            if (to is not null)
            {
                await MoveItemsAsync(db, libraryId, folder, to, folders, ct).ConfigureAwait(false);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await LibraryScanner.MarkMissingItemsAsync(db, libraryId, ct).ConfigureAwait(false);
        return moved;
    }

    /// <summary>The titles' own paths and local artwork under <paramref name="from"/>, moved to where they are under <paramref name="to"/>.</summary>
    private static async Task MoveItemsAsync(FlexarrDbContext db, Guid libraryId, string from, string to, IReadOnlyCollection<string> folders, CancellationToken ct)
    {
        var prefix = from.TrimEnd('/') + "/";
        var items = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.LibraryId == libraryId && ((i.Path != null && (i.Path == from || i.Path.StartsWith(prefix)))
                || (i.PosterUrl != null && i.PosterUrl.Contains(prefix)) || (i.BackdropUrl != null && i.BackdropUrl.Contains(prefix))
                || (i.LogoUrl != null && i.LogoUrl.Contains(prefix)) || (i.ThumbUrl != null && i.ThumbUrl.Contains(prefix))))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            if (item.Path is { } path && Movable(path) is { } moved && (Directory.Exists(moved) || File.Exists(moved)))
            {
                item.Path = moved;
            }

            item.PosterUrl = Artwork(item.PosterUrl);
            item.BackdropUrl = Artwork(item.BackdropUrl);
            item.LogoUrl = Artwork(item.LogoUrl);
            item.ThumbUrl = Artwork(item.ThumbUrl);
        }

        string? Movable(string path) => PathComparison.IsUnder(path, from) && !folders.Any(r => PathComparison.IsUnder(path, r)) ? Moved(path, from, to) : null;

        string? Artwork(string? url)
        {
            foreach (var scheme in new[] { "file://", "embedded://" })
            {
                if (url is not null && url.StartsWith(scheme, StringComparison.Ordinal) && Movable(url[scheme.Length..]) is { } moved && File.Exists(moved))
                {
                    return scheme + moved;
                }
            }

            return url;
        }
    }

    /// <summary><paramref name="path"/>, under <paramref name="from"/>, at the same place under <paramref name="to"/>.</summary>
    internal static string Moved(string path, string from, string to)
    {
        // Matched ignoring Unicode spelling, so the path can't be cut at the folder's length: its names are skipped instead.
        var skip = PathComparison.Key(from).Split('/', StringSplitOptions.RemoveEmptyEntries).Length;
        var rest = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries).Skip(skip).ToList();
        var root = to.Length > 1 ? to.TrimEnd('/') : to;
        return rest.Count == 0 ? root : $"{root.TrimEnd('/')}/{string.Join('/', rest)}";
    }
}
