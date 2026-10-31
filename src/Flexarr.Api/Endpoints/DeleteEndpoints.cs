using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
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
        api.MapGet("/items/{id:guid}/deletion", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var (instance, _) = Manager(item, options.CurrentValue);
            return Results.Ok(new DeletionDto(instance?.Name, instance is null ? await FilesAsync(db, item, ct) : []));
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        api.MapDelete("/items/{id:guid}", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Series or MediaKind.Episode))
            {
                return Results.Problem("Only films, shows and episodes can be deleted.", statusCode: 400);
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
