using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>What deleting a title will do: Radarr or Sonarr delete it, or (when neither manages it) Flexarr deletes these files.</summary>
public sealed record DeletionDto(string? ManagedBy, IReadOnlyList<string> Files);

/// <summary>
/// Deleting a film, show or episode for good. Its files go through Radarr or Sonarr when they manage it (they own the
/// files, and would download them again otherwise), and only once they've confirmed does it leave Flexarr. A title
/// neither manages has its video files deleted by Flexarr itself, then any folders that leaves empty, never above the
/// library's own folders.
/// </summary>
public static partial class DeleteEndpoints
{
    public static RouteGroupBuilder MapDeleteEndpoints(this RouteGroupBuilder api)
    {
        // Asked before confirming, so the dialog can say who deletes it and, when it's Flexarr, list the files.
        api.MapGet("/items/{id:guid}/deletion", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var instance = item.Kind == MediaKind.Audiobook
                ? await BookDeleterAsync(db, item, options.CurrentValue, arr, ct)
                : Manager(item, options.CurrentValue).Instance;
            return Results.Ok(new DeletionDto(instance?.Name, instance is null ? await FilesAsync(db, item, ct) : []));
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        api.MapDelete("/items/{id:guid}", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs, Books.BookFileConverter converter, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Series or MediaKind.Episode or MediaKind.Audiobook))
            {
                return Results.Problem("Only films, shows, episodes and audiobooks can be deleted.", statusCode: 400);
            }

            if (item.Kind == MediaKind.Audiobook)
            {
                var fileIds = await db.MediaFiles.Where(f => f.MediaItemId == item.Id).Select(f => f.Id).ToListAsync(ct);
                var result = await DeleteBookAsync(db, item, options.CurrentValue, arr, logs.CreateLogger("Flexarr.Delete"), ct);
                converter.RemoveCopies(fileIds); // Its converted copies too; harmless if the delete failed, they're made again.
                return result;
            }

            var (instance, managedId) = Manager(item, options.CurrentValue);
            if (instance is null || managedId is not { } arrId)
            {
                return await DeleteFromDiskAsync(db, item, logs.CreateLogger("Flexarr.Delete"), ct);
            }

            try
            {
                switch (item.Kind)
                {
                    case MediaKind.Movie:
                        await arr.DeleteAsync(instance, $"movie/{arrId}?deleteFiles=true&addImportExclusion=false", ct);
                        break;
                    case MediaKind.Series:
                        await arr.DeleteAsync(instance, $"series/{arrId}?deleteFiles=true&addImportListExclusion=false", ct);
                        break;
                    default:
                        // Unmonitor first, or Sonarr would download the episode again.
                        var episode = await arr.GetEpisodeAsync(instance, arrId, ct);
                        await arr.SetEpisodesMonitoredAsync(instance, [arrId], monitored: false, ct);
                        if (episode.EpisodeFileId > 0)
                        {
                            await arr.DeleteAsync(instance, $"episodefile/{episode.EpisodeFileId}", ct);
                        }

                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{instance.Name} didn't delete it ({(int?)ex.StatusCode ?? 0}). Nothing was removed from Flexarr.", statusCode: 502);
            }

            await RemoveFromLibraryAsync(db, item, ct);
            LogDeleted(logs.CreateLogger("Flexarr.Delete"), item.Kind, Name(item), instance.Name);
            return Results.NoContent();
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <summary>The Readarr-compatible app that manages a book, and the book's id there.</summary>
    private static async Task<(ArrInstance? Instance, string? BookId)> BookManagerAsync(FlexarrDbContext db, MediaItem book, FlexarrOptions options, CancellationToken ct)
    {
        // The app the id came from, not MetadataSource: Fix match points that at the online provider chosen.
        var link = await db.BookDetails.Where(d => d.MediaItemId == book.Id).Select(d => new { d.ArrBookId, d.ArrInstance }).FirstOrDefaultAsync(ct);
        var instance = link?.ArrInstance is { } name
            ? options.Arr.FirstOrDefault(a => a.Kind == ArrKind.Readarr && string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase))
            : null;
        return instance is not null && link?.ArrBookId is { } arrId ? (instance, arrId) : (null, null);
    }

    /// <summary>
    /// The app that would delete a book's files: its app, when that has the book's own files. Otherwise
    /// (no app, an app that has no files for it, or a link to some other book there) Flexarr deletes it: null. An app
    /// that doesn't answer is still named: deleting would fail there rather than go behind its back.
    /// </summary>
    private static async Task<ArrInstance?> BookDeleterAsync(FlexarrDbContext db, MediaItem book, FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var (instance, bookId) = await BookManagerAsync(db, book, options, ct);
        if (instance is null || bookId is null)
        {
            return null;
        }

        try
        {
            return (await OwnBookFilesThereAsync(db, book, instance, bookId, options, arr, ct)).Count > 0 ? instance : null;
        }
        catch (HttpRequestException)
        {
            return instance;
        }
    }

    /// <summary>
    /// The app's files for its book that are this book's: one of its own files, as Flexarr sees the app's paths. Not
    /// anything inside its folder: a folder holding one book's file beside other books' folders (Author/One.m4b,
    /// Author/Two/…) is the first book's folder, and the rest aren't its. A book linked only by title and author can be
    /// another edition entirely (an ebook in an ebook app, when ours is the audiobook); its files aren't ours either.
    /// </summary>
    private static async Task<List<ReadarrBookFile>> OwnBookFilesThereAsync(FlexarrDbContext db, MediaItem book, ArrInstance instance, string bookId,
        FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var ours = await db.MediaFiles.Where(f => f.MediaItemId == book.Id).Select(f => f.Path).ToListAsync(ct);
        var keys = ours.Select(PathComparison.Key).ToHashSet(StringComparer.Ordinal);
        var theirs = await arr.GetBookFilesForBookAsync(instance, bookId, ct);
        return [.. theirs.Where(f => f.Id is not null && f.Path is { Length: > 0 } && keys.Contains(PathComparison.Key(PathMapper.Map(f.Path, options.PathMappings))))];
    }

    /// <summary>
    /// A book: unmonitored in its app first, so it isn't fetched again, then its files deleted there. Only the app's
    /// files that are the book's own count: a book no app manages, whose app knows the book but not its files
    /// (imported by hand, say), or that's linked to some other book there (by title, to the ebook, say) has its files
    /// deleted by Flexarr and is left alone in the app. Left on disk, the next scan would only bring it back.
    /// </summary>
    private static async Task<IResult> DeleteBookAsync(FlexarrDbContext db, MediaItem book, FlexarrOptions options, ArrClient arr, ILogger logger, CancellationToken ct)
    {
        var (instance, bookId) = await BookManagerAsync(db, book, options, ct);
        if (instance is null || bookId is null)
        {
            return await DeleteFromDiskAsync(db, book, logger, ct);
        }

        try
        {
            var files = await OwnBookFilesThereAsync(db, book, instance, bookId, options, arr, ct);
            if (files.Count == 0)
            {
                return await DeleteFromDiskAsync(db, book, logger, ct);
            }

            await arr.SetBookMonitoredAsync(instance, bookId, monitored: false, ct);
            foreach (var file in files)
            {
                try
                {
                    await arr.DeleteAsync(instance, $"bookfile/{Uri.EscapeDataString(file.Id!)}", ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    // Already gone from the app: as good as deleted.
                }
            }
        }
        catch (HttpRequestException ex)
        {
            return Results.Problem($"{instance.Name} didn't delete it ({(int?)ex.StatusCode ?? 0}). Nothing was removed from Flexarr.", statusCode: 502);
        }

        await RemoveFromLibraryAsync(db, book, ct);
        LogDeleted(logger, book.Kind, Name(book), instance.Name);
        return Results.NoContent();
    }

    /// <summary>The Radarr or Sonarr that manages the title (episodes through their show), and its id there.</summary>
    private static (ArrInstance? Instance, int? ArrId) Manager(MediaItem item, FlexarrOptions options)
    {
        var source = item.Kind == MediaKind.Episode ? item.Parent?.Parent?.MetadataSource ?? item.MetadataSource : item.MetadataSource;
        var kind = item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr;
        var instance = options.Arr.FirstOrDefault(a => a.Name == source && a.Kind == kind);
        return instance is not null && item.ArrId is { } arrId ? (instance, arrId) : (null, null);
    }

    /// <summary>The video files on disk for a film or episode, or every episode of a show.</summary>
    private static Task<List<string>> FilesAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct) =>
        db.MediaFiles.Where(f => f.MediaItemId == item.Id || f.MediaItem!.ParentId == item.Id || f.MediaItem.Parent!.ParentId == item.Id)
            .Select(f => f.Path).Distinct().OrderBy(p => p).ToListAsync(ct);

    private static async Task<IResult> DeleteFromDiskAsync(FlexarrDbContext db, MediaItem item, ILogger logger, CancellationToken ct)
    {
        var files = await FilesAsync(db, item, ct);
        var roots = await db.LibraryFolders.Where(f => f.LibraryId == item.LibraryId).Select(f => f.Path).ToListAsync(ct);
        if (files.FirstOrDefault(f => !roots.Any(r => PathComparison.IsUnder(f, r) && PathComparison.Key(f) != PathComparison.Key(r))) is { } outside)
        {
            return Results.Problem($"{outside} isn't inside the library's folders, so Flexarr won't delete it. Nothing was deleted.", statusCode: 409);
        }

        foreach (var file in files)
        {
            try
            {
                File.Delete(file); // Already gone is fine.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Files deleted before this one are gone; the next scan drops them.
                return Results.Problem($"Couldn't delete {Path.GetFileName(file)}: {ex.Message}", statusCode: 500);
            }
        }

        foreach (var folder in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            RemoveEmptyFolders(folder, roots);
        }

        await RemoveFromLibraryAsync(db, item, ct);
        LogDeletedFromDisk(logger, item.Kind, Name(item), files.Count);
        return Results.NoContent();
    }

    /// <summary>Removes a folder left empty (but for Finder's .DS_Store), then its parents, stopping at the library's folders.</summary>
    private static void RemoveEmptyFolders(string folder, List<string> roots)
    {
        for (var dir = folder; dir is not null && roots.Any(r => PathComparison.IsUnder(dir, r) && PathComparison.Key(dir) != PathComparison.Key(r));
            dir = Path.GetDirectoryName(dir))
        {
            try
            {
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                var entries = Directory.GetFileSystemEntries(dir);
                if (entries.Any(e => Path.GetFileName(e) != ".DS_Store"))
                {
                    return;
                }

                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // A folder that won't go is left; the files are what mattered.
            }
        }
    }

    private static async Task RemoveFromLibraryAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct)
    {
        // Seasons and episodes go with their show (cascade); an emptied season goes too. Emptied for good, that is:
        // episodes briefly missing from disk (mid-copy, say) still count, or they'd go too, with their watch history.
        await db.MediaItems.Where(i => i.Id == item.Id).ExecuteDeleteAsync(ct);
        if (item.Kind == MediaKind.Episode && item.ParentId is { } seasonId
            && !await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.ParentId == seasonId && i.LibraryId == item.LibraryId, ct))
        {
            await db.MediaItems.Where(i => i.Id == seasonId).ExecuteDeleteAsync(ct);
        }
    }

    private static string? Name(MediaItem item) => item.Kind == MediaKind.Episode ? $"{item.Parent?.Parent?.Title} {item.Title}" : item.Title;

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Kind} {Title} and its files through {Instance}")]
    private static partial void LogDeleted(ILogger logger, MediaKind kind, string? title, string instance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Kind} {Title} and its {Count} files from disk (Sonarr and Radarr don't manage it)")]
    private static partial void LogDeletedFromDisk(ILogger logger, MediaKind kind, string? title, int count);
}
