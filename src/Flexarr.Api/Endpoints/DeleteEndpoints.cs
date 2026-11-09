using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Endpoints;

/// <summary>What deleting a title will do: Radarr or Sonarr delete it, or (when neither manages it) Flexarr deletes these files.</summary>
/// <param name="ManagedBy">The app that deletes it, if one does.</param>
/// <param name="Files">What Flexarr deletes itself: all its files if no app manages it, or a managed episode's extra files.</param>
/// <param name="Copies">Each copy (a copy in a library hidden from a profile that hasn't unlocked has no library or files) of a linked title, all of which go when the delete asks for allCopies. Absent from servers before 0.64, and for a title with one copy.</param>
public sealed record DeletionDto(string? ManagedBy, IReadOnlyList<string> Files, IReadOnlyList<DeletionCopyDto>? Copies = null);

/// <summary>One copy of a title being deleted: which library it's in, who deletes it, and the files Flexarr deletes itself.</summary>
public sealed record DeletionCopyDto(Guid ItemId, string Library, string? ManagedBy, IReadOnlyList<string> Files);

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
        api.MapGet("/items/{id:guid}/deletion", async (Guid id, FlexarrDbContext db, Profiles.CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            // Books are always Flexarr's to delete: no app manages them. An album Lidarr's artist has is Lidarr's, matched or not.
            var instance = item.Kind == MediaKind.Audiobook ? null : Manager(item, options.CurrentValue).Instance
                ?? (item.Kind == MediaKind.Album ? LidarrOfArtist(item, options.CurrentValue)?.Lidarr : null);
            return Results.Ok(LooksForOwner(item, options.CurrentValue)
                ? await PlanCopiesAsync(db, item, profile, options.CurrentValue, arr, ct)
                : new DeletionDto(instance?.Name, instance is null ? await FilesAsync(db, item, ct) : await ExtrasAsync(db, item, instance, Manager(item, options.CurrentValue).ArrId, options.CurrentValue, arr, ct)));
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        // Deletes the item named, and every copy of it only when asked to with ?allCopies=true (added in 0.64): a caller
        // that doesn't know about copies mustn't take a copy in another library with it.
        api.MapDelete("/items/{id:guid}", async (Guid id, bool? allCopies, FlexarrDbContext db, Profiles.CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs, Books.BookFileConverter converter,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            return LooksForOwner(item, options.CurrentValue)
                ? await DeleteCopiesAsync(db, item, allCopies == true, profile, options, arr, logs, converter, cache, ct)
                : await DeleteOneAsync(db, item, Manager(item, options.CurrentValue), options, arr, logs, converter, cache, ct);
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <summary>
    /// Whether to find the app holding a title's files by where they are rather than trust its record: for linked titles,
    /// and with two of its app, where the record can name the wrong one. (A copy left over from a delete that half failed
    /// is no longer linked, and must still go to the right one.)
    /// </summary>
    private static bool LooksForOwner(MediaItem item, FlexarrOptions options) => item.Kind switch
    {
        MediaKind.Movie => item.LinkGroupId is not null || options.Arr.Count(a => a.Kind == ArrKind.Radarr) > 1,
        MediaKind.Series or MediaKind.Episode => item.LinkGroupId is not null || options.Arr.Count(a => a.Kind == ArrKind.Sonarr) > 1,
        _ => false,
    };

    /// <summary>
    /// Deletes a title, or with <paramref name="allCopies"/> every copy of a linked one, in every library, whoever can see
    /// them. Each goes through whichever Radarr or Sonarr holds its files, or from disk if none does. One that fails
    /// doesn't stop the others, and is reported.
    /// </summary>
    private static async Task<IResult> DeleteCopiesAsync(FlexarrDbContext db, MediaItem item, bool allCopies, Profiles.CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs,
        Books.BookFileConverter converter, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        var copies = allCopies ? await CopiesAsync(db, item, ct) : [item];
        if (!profile.IsAdmin && (await HiddenAsync(db, copies, ct)).Count > 0)
        {
            return Problems.Of("One copy is in a library hidden from this profile. Unlock first. Nothing was deleted.", status: StatusCodes.Status403Forbidden);
        }

        var libraries = await db.Libraries.AsNoTracking().IgnoreQueryFilters().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var failures = new List<string>();
        var status = StatusCodes.Status502BadGateway;
        var done = 0;
        foreach (var copy in copies)
        {
            // The one asked about goes last, and only if the rest went: with it still here the page stays good, and
            // deleting again finishes the job.
            if (copy.Id == item.Id && failures.Count > 0)
            {
                continue;
            }

            var library = libraries.GetValueOrDefault(copy.LibraryId, "");
            try
            {
                var owner = await OwnerAsync(db, copy, options.CurrentValue, arr, ct);
                if (await DeleteOneAsync(db, copy, owner, options, arr, logs, converter, cache, ct) is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult problem)
                {
                    if (copies.Count == 1)
                    {
                        return problem;
                    }

                    failures.Add($"{library}: {problem.ProblemDetails.Detail}");
                    status = problem.StatusCode;
                }
                else
                {
                    done++;
                }
            }
            catch (HttpRequestException ex)
            {
                // Which app holds it couldn't be asked, and deleting it behind that app's back would only bring it back.
                if (copies.Count == 1)
                {
                    return Problems.Of("Couldn’t check which app holds it ({status}). Nothing was deleted.", new { status = (int?)ex.StatusCode ?? 0 }, status: status);
                }

                failures.Add($"{library}: {Loc.T("Couldn’t check which app holds it ({status}).", new { status = (int?)ex.StatusCode ?? 0 })}");
            }
        }

        // What's left, if anything, has no copies to be linked to now.
        await Flexarr.Core.Scanning.TitleLinker.LinkAsync(db, ct);
        return failures.Count == 0 ? Results.NoContent()
            : Problems.Of("Deleted {done} of {total} copies. {failures}", new { done, total = copies.Count, failures = string.Join(" ", failures) }, status: status);
    }

    /// <summary>What deleting a linked title will do, for each copy.</summary>
    private static async Task<DeletionDto> PlanCopiesAsync(FlexarrDbContext db, MediaItem item, Profiles.CurrentProfile profile, FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var libraries = await db.Libraries.AsNoTracking().IgnoreQueryFilters().ToDictionaryAsync(l => l.Id, l => l.Name, ct);
        var plans = new List<DeletionCopyDto>();
        var own = new DeletionDto(null, []);
        var copies = await CopiesAsync(db, item, ct);
        // Named only once unlocked: the main profile's PIN is what lifts the profile's own hidden libraries.
        var unnamed = profile.IsAdmin ? [] : await HiddenAsync(db, copies, ct);
        foreach (var copy in copies)
        {
            if (unnamed.Contains(copy.Id))
            {
                plans.Add(new DeletionCopyDto(copy.Id, "", null, []));
                continue;
            }

            ArrInstance? instance;
            int? arrId;
            try
            {
                (instance, arrId) = await OwnerAsync(db, copy, options, arr, ct);
            }
            catch (HttpRequestException)
            {
                // Can't ask now: what's on record will do for a preview. The delete asks again, and says if it can't.
                (instance, arrId) = Manager(copy, options);
            }

            var files = instance is null ? await FilesAsync(db, copy, ct) : await ExtrasAsync(db, copy, instance, arrId, options, arr, ct);
            plans.Add(new DeletionCopyDto(copy.Id, libraries.GetValueOrDefault(copy.LibraryId, ""), instance?.Name, files));
            if (copy.Id == item.Id)
            {
                own = new DeletionDto(instance?.Name, files);
            }
        }

        return plans.Count > 1 ? own with { Copies = plans } : own;
    }

    /// <summary>Those of <paramref name="copies"/> the current profile can't see.</summary>
    private static async Task<HashSet<Guid>> HiddenAsync(FlexarrDbContext db, List<MediaItem> copies, CancellationToken ct)
    {
        var ids = copies.Select(c => c.Id).ToList();
        var visible = await db.MediaItems.Where(i => ids.Contains(i.Id)).Select(i => i.Id).ToListAsync(ct);
        return [.. ids.Except(visible)];
    }

    /// <summary>A title's copies, including those in libraries hidden from whoever's asking: the item itself last.</summary>
    private static async Task<List<MediaItem>> CopiesAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct)
    {
        if (item.LinkGroupId is not { } group)
        {
            return [item];
        }

        var copies = await db.MediaItems.AsNoTracking().IgnoreQueryFilters().Include(i => i.Parent).ThenInclude(p => p!.Parent)
            .Where(i => i.LinkGroupId == group).ToListAsync(ct);
        return [.. copies.OrderBy(c => c.Id == item.Id)];
    }

    /// <summary>
    /// The Radarr or Sonarr that really holds a copy's files, found by where they are. A copy's own record can name the
    /// wrong one: metadata falls back on the TMDB id, which both instances of a 4K and an HD setup have.
    /// </summary>
    private static async Task<(ArrInstance? Instance, int? ArrId)> OwnerAsync(FlexarrDbContext db, MediaItem copy, FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var path = (await db.MediaFiles.AsNoTracking().IgnoreQueryFilters()
            .Where(f => f.MediaItemId == copy.Id || f.MediaItem!.ParentId == copy.Id || f.MediaItem.Parent!.ParentId == copy.Id)
            .Select(f => f.Path).FirstOrDefaultAsync(ct)) ?? copy.Path;
        if (path is null)
        {
            return Manager(copy, options);
        }

        bool Holds(string? folder) => folder is not null && PathComparison.IsUnder(path, PathMapper.Map(folder, options.PathMappings));
        if (copy.Kind == MediaKind.Movie)
        {
            foreach (var radarr in options.Arr.Where(a => a.Kind == ArrKind.Radarr))
            {
                if ((await arr.GetMoviesAsync(radarr, ct)).FirstOrDefault(m => Holds(m.Path)) is { } movie)
                {
                    return (radarr, movie.Id);
                }
            }

            return (null, null);
        }

        foreach (var sonarr in options.Arr.Where(a => a.Kind == ArrKind.Sonarr))
        {
            if ((await arr.GetSeriesAsync(sonarr, ct)).FirstOrDefault(s => Holds(s.Path)) is not { } series)
            {
                continue;
            }

            if (copy.Kind == MediaKind.Series)
            {
                return (sonarr, series.Id);
            }

            var episode = (await arr.GetEpisodesAsync(sonarr, series.Id, ct)).FirstOrDefault(e => e.SeasonNumber == copy.SeasonNumber && e.EpisodeNumber == copy.EpisodeNumber);
            return episode is null ? (null, null) : (sonarr, episode.Id);
        }

        return (null, null);
    }

    /// <summary>The files of a managed episode its Sonarr doesn't track, for the dialog to list: Flexarr deletes those itself.</summary>
    private static async Task<List<string>> ExtrasAsync(FlexarrDbContext db, MediaItem item, ArrInstance instance, int? arrId, FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        if (item.Kind != MediaKind.Episode || arrId is not { } id)
        {
            return [];
        }

        var files = await FilesAsync(db, item, ct);
        if (files.Count < 2)
        {
            return [];
        }

        try
        {
            var episode = await arr.GetEpisodeAsync(instance, id, ct);
            var tracked = episode.EpisodeFileId > 0 ? (await arr.GetEpisodeFileAsync(instance, episode.EpisodeFileId, ct)).Path : null;
            var key = tracked is null ? null : PathComparison.Key(PathMapper.Map(tracked, options.PathMappings));
            return [.. files.Where(f => PathComparison.Key(f) != key)];
        }
        catch (HttpRequestException)
        {
            return []; // Only a preview: the delete still removes them.
        }
    }

    /// <summary>Deletes what's still on disk of an item its app has just deleted, inside the library's folders only.</summary>
    /// <returns>A problem if a file wouldn't go, otherwise null.</returns>
    private static async Task<IResult?> DeleteLeftoversAsync(FlexarrDbContext db, MediaItem item, CancellationToken ct)
    {
        var roots = await db.LibraryFolders.Where(f => f.LibraryId == item.LibraryId).Select(f => f.Path).ToListAsync(ct);
        var files = (await FilesAsync(db, item, ct)).Where(f => roots.Any(r => PathComparison.IsUnder(f, r) && PathComparison.Key(f) != PathComparison.Key(r))).ToList();
        foreach (var file in files)
        {
            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problems.Of("Couldn’t delete {file}: {message}", new { file = Path.GetFileName(file), message = ex.Message }, status: 500);
            }
        }

        foreach (var folder in files.Select(Path.GetDirectoryName).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            RemoveEmptyFolders(folder, roots);
        }

        return null;
    }

    /// <summary>Deletes one title and its files.</summary>
    /// <param name="owner">The app holding its files, if one does.</param>
    private static async Task<IResult> DeleteOneAsync(FlexarrDbContext db, MediaItem item, (ArrInstance? Instance, int? ArrId) owner, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs,
        Books.BookFileConverter converter, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct)
    {
        if (item.Kind is not (MediaKind.Movie or MediaKind.Series or MediaKind.Episode or MediaKind.Audiobook or MediaKind.Album or MediaKind.Artist))
        {
            return Problems.Of("Only movies, shows, episodes, audiobooks, albums and artists can be deleted.", status: 400);
        }

        if (item.Kind == MediaKind.Audiobook)
        {
            var fileIds = await db.MediaFiles.Where(f => f.MediaItemId == item.Id).Select(f => f.Id).ToListAsync(ct);
            var result = await DeleteFromDiskAsync(db, item, logs.CreateLogger("Flexarr.Delete"), ct);
            converter.RemoveCopies(fileIds); // Its converted copies too; harmless if the delete failed, they're made again.
            return result;
        }

        var (instance, managedId) = owner;
        var keepArtist = false;
        // An album Lidarr's artist has but that was never matched to Lidarr's copy: found by title, so Lidarr (which
        // may still monitor it) deletes it rather than Flexarr behind its back.
        if (instance is null && item.Kind == MediaKind.Album && LidarrOfArtist(item, options.CurrentValue) is { } lidarr)
        {
            try
            {
                // The one of that title (the nearest in year, for a name two albums share: Live, Greatest Hits).
                var key = MusicNames.Normalise(item.Title);
                var match = (await arr.GetArtistAlbumsAsync(lidarr.Lidarr, lidarr.ArtistId, ct)).Where(a => MusicNames.Normalise(a.Title) == key)
                    .OrderBy(a => item.Year is { } y && a.ReleaseDate is { } d ? Math.Abs(d.Year - y) : 0).FirstOrDefault();
                if (match is null)
                {
                    return Problems.Of("{name} has this artist but not this album under that name. Refresh the artist’s details, then try again. Nothing was deleted.", new { name = lidarr.Lidarr.Name }, status: 409);
                }

                (instance, managedId) = (lidarr.Lidarr, match.Id);
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("{name} didn’t answer ({status}). Nothing was deleted.", new { name = lidarr.Lidarr.Name, status = (int?)ex.StatusCode ?? 0 }, status: 502);
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
                        return Problems.Of("{name} keeps this artist’s files outside this library, so Flexarr won’t delete them. Nothing was deleted.", new { name = instance.Name }, status: 409);
                    }

                    // And that folder must be this artist's: matched to the wrong one of two same-named artists,
                    // Lidarr would delete the other one's. Their album folders show where they really are.
                    var artistFolder = PathMapper.Map(lidarrPath, options.CurrentValue.PathMappings);
                    var albumFolders = await db.MediaItems.Where(i => i.ParentId == item.Id && i.Kind == MediaKind.Album && i.Path != null).Select(i => i.Path!).ToListAsync(ct);
                    if (albumFolders.Count > 0 && !albumFolders.Any(f => PathComparison.IsUnder(f, artistFolder)))
                    {
                        return Problems.Of("{name}’s folder for this artist doesn’t hold their albums here, so it may be another artist’s. Check Fix match, then try again. Nothing was deleted.", new { name = instance.Name }, status: 409);
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
                        return Problems.Of("{name} stopped wanting the album but didn’t delete its files ({status}). Try again.", new { name = instance.Name, status = (int?)ex.StatusCode ?? 0 }, status: 502);
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

                    // Sonarr tracks one file for an episode: any others would only turn up again in the next scan.
                    if (await DeleteLeftoversAsync(db, item, ct) is { } failed)
                    {
                        return failed;
                    }

                    break;
            }
        }
        catch (HttpRequestException ex)
        {
            return Problems.Of("{name} didn’t delete it ({status}). Nothing was removed from Flexarr.", new { name = instance.Name, status = (int?)ex.StatusCode ?? 0 }, status: 502);
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
        // Ignoring the profile's filter: every copy goes, in libraries hidden from it too. Files already gone from disk don't count.
        db.MediaFiles.IgnoreQueryFilters().Where(f => f.MediaItem!.MissingSince == null && (f.MediaItemId == item.Id || f.MediaItem.ParentId == item.Id || f.MediaItem.Parent!.ParentId == item.Id))
            .Select(f => f.Path).Distinct().OrderBy(p => p).ToListAsync(ct);

    private static async Task<IResult> DeleteFromDiskAsync(FlexarrDbContext db, MediaItem item, ILogger logger, CancellationToken ct, bool keepArtist = false)
    {
        var files = await FilesAsync(db, item, ct);
        var roots = await db.LibraryFolders.Where(f => f.LibraryId == item.LibraryId).Select(f => f.Path).ToListAsync(ct);
        if (files.FirstOrDefault(f => !roots.Any(r => PathComparison.IsUnder(f, r) && PathComparison.Key(f) != PathComparison.Key(r))) is { } outside)
        {
            return Problems.Of("{outside} isn’t inside the library’s folders, so Flexarr won’t delete it. Nothing was deleted.", new { outside }, status: 409);
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
                return Problems.Of("Couldn’t delete {file}: {message}", new { file = Path.GetFileName(file), message = ex.Message }, status: 500);
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
        await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == item.Id).ExecuteDeleteAsync(ct);
        if (item.Kind == MediaKind.Episode && item.ParentId is { } seasonId
            && !await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.ParentId == seasonId && i.LibraryId == item.LibraryId, ct))
        {
            await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == seasonId).ExecuteDeleteAsync(ct);
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
