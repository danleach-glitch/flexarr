using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>What deleting a title will do: Radarr or Sonarr delete it, or (when neither manages it) Flexarr deletes these files.</summary>
public sealed record DeletionDto(string? ManagedBy, IReadOnlyList<string> Files);

/// <summary>
/// Deleting a film, show or episode for good. Its files go through Radarr or Sonarr when they manage it (they own the
/// files, and would download them again otherwise), and only once they've confirmed does it leave Flexarr. A title
/// neither manages, and every audiobook, has its files deleted by Flexarr itself, then any folders that leaves empty,
/// never above the library's own folders.
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

            // Books are always Flexarr's to delete: no app manages them. An album Lidarr's artist has is Lidarr's, matched or not.
            var instance = item.Kind == MediaKind.Audiobook ? null : Manager(item, options.CurrentValue).Instance
                ?? (item.Kind == MediaKind.Album ? LidarrOfArtist(item, options.CurrentValue)?.Lidarr : null);
            return Results.Ok(new DeletionDto(instance?.Name, instance is null ? await FilesAsync(db, item, ct) : []));
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        api.MapDelete("/items/{id:guid}", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs, Books.BookFileConverter converter,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Series or MediaKind.Episode or MediaKind.Audiobook or MediaKind.Album or MediaKind.Artist))
            {
                return Results.Problem("Only movies, shows, episodes, audiobooks, albums and artists can be deleted.", statusCode: 400);
            }

            if (item.Kind == MediaKind.Audiobook)
            {
                var fileIds = await db.MediaFiles.Where(f => f.MediaItemId == item.Id).Select(f => f.Id).ToListAsync(ct);
                var result = await DeleteFromDiskAsync(db, item, logs.CreateLogger("Flexarr.Delete"), ct);
                converter.RemoveCopies(fileIds); // Its converted copies too; harmless if the delete failed, they're made again.
                return result;
            }

            var (instance, managedId) = Manager(item, options.CurrentValue);
            var keepArtist = false;
            // An album Lidarr's artist has but that was never matched to Lidarr's copy: found by title, so Lidarr (which
            // may still monitor it) deletes it rather than Flexarr behind its back.
            if (instance is null && item.Kind == MediaKind.Album && LidarrOfArtist(item, options.CurrentValue) is { } owner)
            {
                try
                {
                    // The one of that title (the nearest in year, for a name two albums share: Live, Greatest Hits).
                    var key = MusicNames.Normalise(item.Title);
                    var match = (await arr.GetArtistAlbumsAsync(owner.Lidarr, owner.ArtistId, ct)).Where(a => MusicNames.Normalise(a.Title) == key)
                        .OrderBy(a => item.Year is { } y && a.ReleaseDate is { } d ? Math.Abs(d.Year - y) : 0).FirstOrDefault();
                    if (match is null)
                    {
                        return Results.Problem($"{owner.Lidarr.Name} has this artist but not this album under that name. Refresh the artist's details, then try again. Nothing was deleted.", statusCode: 409);
                    }

                    (instance, managedId) = (owner.Lidarr, match.Id);
                }
                catch (HttpRequestException ex)
                {
                    return Results.Problem($"{owner.Lidarr.Name} didn't answer ({(int?)ex.StatusCode ?? 0}). Nothing was deleted.", statusCode: 502);
                }
            }

            if (instance is null || managedId is not { } arrId)
            {
                return await DeleteFromDiskAsync(db, item, logs.CreateLogger("Flexarr.Delete"), ct);
            }

            // Lidarr has nothing of it to delete (it never imported these files, or no longer has the artist): Flexarr
            // deletes them itself once Lidarr has stopped wanting them.
            var fromDisk = false;
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
                    case MediaKind.Artist:
                        // Lidarr deletes the artist's whole folder: only when that folder is this library's.
                        LidarrArtist lidarrArtist;
                        try
                        {
                            lidarrArtist = await arr.GetArtistAsync(instance, arrId, ct);
                        }
                        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                        {
                            fromDisk = true; // Lidarr no longer has them
                            break;
                        }

                        var roots = await db.LibraryFolders.Where(f => f.LibraryId == item.LibraryId).Select(f => f.Path).ToListAsync(ct);
                        if (lidarrArtist.Path is not { } lidarrPath || !roots.Any(r => PathComparison.IsUnder(PathMapper.Map(lidarrPath, options.CurrentValue.PathMappings), r)))
                        {
                            return Results.Problem($"{instance.Name} keeps this artist's files outside this library, so Flexarr won't delete them. Nothing was deleted.", statusCode: 409);
                        }

                        // And that folder must be this artist's: matched to the wrong one of two same-named artists,
                        // Lidarr would delete the other one's. Their album folders show where they really are.
                        var artistFolder = PathMapper.Map(lidarrPath, options.CurrentValue.PathMappings);
                        var albumFolders = await db.MediaItems.Where(i => i.ParentId == item.Id && i.Kind == MediaKind.Album && i.Path != null).Select(i => i.Path!).ToListAsync(ct);
                        if (albumFolders.Count > 0 && !albumFolders.Any(f => PathComparison.IsUnder(f, artistFolder)))
                        {
                            return Results.Problem($"{instance.Name}'s folder for this artist doesn't hold their albums here, so it may be another artist's. Check Fix match, then try again. Nothing was deleted.", statusCode: 409);
                        }

                        await arr.DeleteAsync(instance, $"artist/{arrId}?deleteFiles=true&addImportListExclusion=false", ct);
                        break;
                    case MediaKind.Album:
                        // Unmonitored rather than removed, or Lidarr would download it again (or add it back on its
                        // next refresh); it stays requestable.
                        await arr.SetAlbumsMonitoredAsync(instance, [arrId], monitored: false, ct);
                        try
                        {
                            // Only Lidarr's files in this album's folder: never another copy elsewhere.
                            var files = (await arr.GetAlbumTrackFilesAsync(instance, arrId, ct))
                                .Where(f => item.Path is null || (f.Path is { } p && PathComparison.IsUnder(PathMapper.Map(p, options.CurrentValue.PathMappings), item.Path)))
                                .Select(f => f.Id).ToList();
                            if (files.Count > 0)
                            {
                                await arr.DeleteTrackFilesAsync(instance, files, ct);
                            }
                            else
                            {
                                fromDisk = true;
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            // Unmonitored already, so it won't download again; its files stay until this is tried again.
                            return Results.Problem($"{instance.Name} stopped wanting the album but didn't delete its files ({(int?)ex.StatusCode ?? 0}). Try again.", statusCode: 502);
                        }

                        // Only the artist's last album asks whether they're followed (and keeps them if so).
                        if (item.ParentId is { } parentId && !await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.ParentId == parentId && i.Id != item.Id, ct))
                        {
                            keepArtist = await FollowedAsync(arr, instance, item.Parent, cache, ct);
                        }

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

            if (fromDisk)
            {
                return await DeleteFromDiskAsync(db, item, logs.CreateLogger("Flexarr.Delete"), ct, keepArtist);
            }

            await RemoveFromLibraryAsync(db, item, keepArtist, ct);
            if (item.Kind == MediaKind.Artist)
            {
                await CloseArtistRequestsAsync(db, item, ct);
                MusicRequests.MusicMatcher.Forget(cache, instance);
                MusicRequests.MusicRequested.Forget(cache);
            }

            LogDeleted(logs.CreateLogger("Flexarr.Delete"), item.Kind, Name(item), instance.Name);
            return Results.NoContent();
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <summary>The Radarr or Sonarr that manages the title (episodes through their show), and its id there.</summary>
    private static (ArrInstance? Instance, int? ArrId) Manager(MediaItem item, FlexarrOptions options)
    {
        var source = item.Kind == MediaKind.Episode ? item.Parent?.Parent?.MetadataSource ?? item.MetadataSource : item.MetadataSource;
        var kind = item.Kind switch { MediaKind.Movie => ArrKind.Radarr, MediaKind.Album or MediaKind.Artist => ArrKind.Lidarr, _ => ArrKind.Sonarr };
        var instance = options.Arr.FirstOrDefault(a => a.Name == source && a.Kind == kind);
        return instance is not null && item.ArrId is { } arrId ? (instance, arrId) : (null, null);
    }

    /// <summary>The video files on disk for a film or episode, or every episode of a show.</summary>
    private static Task<List<string>> FilesAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct) =>
        db.MediaFiles.Where(f => f.MediaItemId == item.Id || f.MediaItem!.ParentId == item.Id || f.MediaItem.Parent!.ParentId == item.Id)
            .Select(f => f.Path).Distinct().OrderBy(p => p).ToListAsync(ct);

    private static async Task<IResult> DeleteFromDiskAsync(FlexarrDbContext db, MediaItem item, ILogger logger, CancellationToken ct, bool keepArtist = false)
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

        await RemoveFromLibraryAsync(db, item, keepArtist, ct);
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

    /// <summary>Whether Lidarr follows the album's artist, so an album delete that leaves them empty keeps them (as a placeholder).</summary>
    private static async Task<bool> FollowedAsync(ArrClient arr, ArrInstance lidarr, MediaItem? artist, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        if (artist?.ArrId is not { } artistId || artist.MetadataSource != lidarr.Name)
        {
            return false;
        }

        return (await MusicRequests.MusicMatcher.LidarrArtistsAsync(arr, lidarr, cache, ct)).FirstOrDefault(a => a.Id == artistId) is { } found
            && MusicRequests.MusicMatcher.IsFollowed(found);
    }

    /// <summary>The Lidarr that manages an album's artist, and the artist's id there.</summary>
    private static (ArrInstance Lidarr, int ArtistId)? LidarrOfArtist(MediaItem album, FlexarrOptions options) =>
        album.Parent is { ArrId: { } artistId, MetadataSource: { } source } && options.Arr.FirstOrDefault(a => a.Kind == ArrKind.Lidarr && a.Name == source) is { } lidarr
            ? (lidarr, artistId)
            : null;

    /// <summary>A deleted artist's follows and album requests are over: nothing more of theirs is coming.</summary>
    private static async Task CloseArtistRequestsAsync(FlexarrDbContext db, MediaItem artist, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var mbid = artist.MusicBrainzId;
        // Ids compared only when the artist has one: a null would match every request without one.
        await db.MediaRequests.Where(r => r.FulfilledAt == null
                && ((r.Kind == MediaKind.Artist && ((mbid != null && r.MusicBrainzId == mbid) || r.Title == artist.Title))
                    || (r.Kind == MediaKind.Album && ((mbid != null && r.ArtistMusicBrainzId == mbid) || r.ArtistName == artist.Title))))
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.FulfilledAt, now), ct);
    }

    /// <param name="keepArtist">An album's artist, left with no albums, stays as a placeholder (they're followed).</param>
    private static async Task RemoveFromLibraryAsync(FlexarrDbContext db, MediaItem item, bool keepArtist, CancellationToken ct)
    {
        // Seasons and episodes go with their show (cascade); an emptied season goes too. Emptied for good, that is:
        // episodes briefly missing from disk (mid-copy, say) still count, or they'd go too, with their watch history.
        await db.MediaItems.Where(i => i.Id == item.Id).ExecuteDeleteAsync(ct);
        if (item.Kind == MediaKind.Episode && item.ParentId is { } seasonId
            && !await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.ParentId == seasonId && i.LibraryId == item.LibraryId, ct))
        {
            await db.MediaItems.Where(i => i.Id == seasonId).ExecuteDeleteAsync(ct);
        }

        if (item.Kind is MediaKind.Album or MediaKind.Artist)
        {
            if (item.Kind == MediaKind.Album && item.ParentId is { } artistId
                && !await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.ParentId == artistId && i.LibraryId == item.LibraryId, ct))
            {
                var emptied = db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == artistId);
                await (keepArtist ? emptied.ExecuteUpdateAsync(u => u.SetProperty(i => i.Placeholder, true), ct) : emptied.ExecuteDeleteAsync(ct));
            }

            MusicRequests.MusicMatcher.ForgetLibrary();
        }
    }

    private static string? Name(MediaItem item) => item.Kind == MediaKind.Episode ? $"{item.Parent?.Parent?.Title} {item.Title}" : item.Title;

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Kind} {Title} and its files through {Instance}")]
    private static partial void LogDeleted(ILogger logger, MediaKind kind, string? title, string instance);

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Kind} {Title} and its {Count} files from disk (Sonarr and Radarr don't manage it)")]
    private static partial void LogDeletedFromDisk(ILogger logger, MediaKind kind, string? title, int count);
}
