using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Endpoints;

public sealed record HealthFileDto(Guid Id, string Path, long Size, string? Resolution, string? VideoCodec, long? Bitrate, bool Keep);

public sealed record DuplicateDto(ItemSummaryDto Item, string? SeriesTitle, IReadOnlyList<HealthFileDto> Files, long ReclaimableBytes);

public sealed record UnreadableDto(Guid FileId, string Path, string Error, ItemSummaryDto? Item);

/// <summary>
/// The share of films and episodes with nothing wrong (0–100), and how many titles each kind of problem touches.
/// <see cref="ArtworkNeedsConnection"/>: films and shows without artwork that no Radarr or Sonarr is connected to fill
/// in; they're left out of the score and <see cref="MissingArtwork"/>.
/// </summary>
/// <see cref="AlbumsWithoutCover"/>, <see cref="DuplicateAlbums"/>, <see cref="AlbumsWithRepeatedTracks"/>, <see cref="SplitArtists"/>: music's
/// checks (albums count as titles in the score). Absent from servers before 0.59.
public sealed record HealthScoreDto(int Score, int Titles, int WithProblems, int Duplicates, int Unreadable, int StandardDefinition, int MissingArtwork, int FileProblems,
    int ArtworkNeedsConnection = 0, int AlbumsWithoutCover = 0, int DuplicateAlbums = 0, int AlbumsWithRepeatedTracks = 0, int SplitArtists = 0);

/// <summary>
/// Albums that look like one release more than once: the same MusicBrainz release or Lidarr album. Merge makes them one.
/// </summary>
public sealed record DuplicateAlbumsDto(IReadOnlyList<ItemSummaryDto> Albums, string? Artist);

/// <summary>An album with the same disc and track number more than once (<see cref="Repeated"/> tracks too many): two copies in one folder, usually.</summary>
public sealed record RepeatedTracksDto(ItemSummaryDto Album, string? Artist, int Repeated);

/// <summary>One artist under two or more spellings ("Beyoncé", "Beyonce"), or sharing a MusicBrainz id. Merge makes them one.</summary>
public sealed record SplitArtistDto(IReadOnlyList<ItemSummaryDto> Artists);

/// <summary>Merge: everything of <see cref="Merge"/>'s moves to <see cref="Keep"/>, and they go. Files are left alone.</summary>
public sealed record MergeRequest(Guid Keep, IReadOnlyList<Guid> Merge);

public sealed record LibraryHealthDto(
    HealthScoreDto Score,
    IReadOnlyList<DuplicateDto> Duplicates,
    long ReclaimableBytes,
    IReadOnlyList<UnreadableDto> Unreadable,
    IReadOnlyList<ItemSummaryDto> MissingArtwork,
    IReadOnlyList<ItemSummaryDto> MatchedByTitle,
    IReadOnlyList<ItemSummaryDto> StandardDefinition,
    int StandardDefinitionTotal,
    IReadOnlyDictionary<Guid, DownloadStateDto> Downloading,
    IReadOnlyList<HeldFolderDto> Held,
    // Music's checks. Absent from servers before 0.59.
    IReadOnlyList<ItemSummaryDto>? AlbumsWithoutCover = null,
    IReadOnlyList<DuplicateAlbumsDto>? DuplicateAlbums = null,
    IReadOnlyList<RepeatedTracksDto>? AlbumsWithRepeatedTracks = null,
    IReadOnlyList<SplitArtistDto>? SplitArtists = null);

/// <summary>
/// A library folder whose scans keep its files rather than hide the ones they can't find, as it looked unmounted or
/// cleared out. <see cref="CanConfirm"/>: the removal can be confirmed (not for a folder that isn't there at all).
/// </summary>
public sealed record HeldFolderDto(int FolderId, string Library, string Path, FolderHold Reason, int Missing, int Known, DateTime Since, int Scans, bool Confirmed, bool CanConfirm,
    LibraryType? LibraryType = null);

/// <summary>What Library health showed for a held folder when its removal was confirmed.</summary>
public sealed record HeldConfirmation(DateTime Since, int Missing, int Known);

public sealed record SkippedFileDto(string Path, string Reason);

public sealed record DuplicateRemovalDto(int Removed, long FreedBytes, IReadOnlyList<SkippedFileDto> Skipped);

public sealed record FileProblemReportDto(FileProblemDto File, ItemSummaryDto? Item);

public sealed record HealthCheckReportDto(bool Running, int Checked, int Total, DateTime? FinishedAt, IReadOnlyList<FileProblemReportDto> Problems);

public static partial class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        var libraryHealth = async (FlexarrDbContext db, CurrentProfile profile, HealthCheck check, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            CancellationToken ct) =>
        {
            // Duplicates: more than one file for one movie or episode. The best copy (resolution, then bitrate) is kept.
            var files = await db.MediaFiles.AsNoTracking()
                .Where(f => f.MediaItem!.Files.Count > 1 && (f.MediaItem.Kind == MediaKind.Movie || f.MediaItem.Kind == MediaKind.Episode))
                .Select(f => new { f.Id, f.MediaItemId, f.Path, f.Size, f.Width, f.Height, f.VideoCodec, f.Bitrate })
                .ToListAsync(ct);
            var groups = files.GroupBy(f => f.MediaItemId).ToList();
            var ids = groups.Select(g => g.Key).ToList();
            var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

            var duplicates = groups.Select(g =>
                {
                    var ranked = g.OrderByDescending(f => Math.Max(f.Height ?? 0, (f.Width ?? 0) * 9 / 16)).ThenByDescending(f => f.Bitrate ?? 0).ThenByDescending(f => f.Size).ToList();
                    var list = ranked.Select((f, i) => new HealthFileDto(f.Id, f.Path, f.Size, Resolution.Label(f.Width, f.Height), f.VideoCodec, f.Bitrate, i == 0)).ToList();
                    var item = items[g.Key];
                    return new DuplicateDto(item, item.SeriesTitle, list, list.Where(f => !f.Keep).Sum(f => f.Size));
                })
                .OrderByDescending(d => d.ReclaimableBytes)
                .ToList();

            var unreadable = await db.MediaFiles.AsNoTracking().Where(f => f.ProbeError != null)
                .Select(f => new { f.Id, f.Path, f.ProbeError, f.MediaItemId }).ToListAsync(ct);
            var unreadableItems = await db.MediaItems.Where(i => unreadable.Select(u => u.MediaItemId).Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

            var missing = await db.MediaItems.Where(i => i.ParentId == null && i.PosterUrl == null).OrderBy(i => i.SortTitle).Take(100).Summaries(profile.Id).ToListAsync(ct);
            var music = await MusicAsync(db, profile.Id, ct);

            // Films and shows Sonarr and Radarr don't manage, so their details came from searching for the folder's
            // name (in them, or on TMDB or TVmaze): worth a look, as a search can pick the wrong one (a remake, an older
            // show of the same name).
            var byTitle = await db.MediaItems
                .Where(i => i.ParentId == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series)
                    && (i.MetadataSource == MetadataRefresher.RadarrLookup || i.MetadataSource == MetadataRefresher.SonarrLookup
                        || i.MetadataSource == MetadataRefresher.TmdbSearch || i.MetadataSource == MetadataRefresher.TvMazeSearch) && !i.MatchLocked)
                .OrderBy(i => i.SortTitle).Take(100).Summaries(profile.Id).ToListAsync(ct);

            // Films and shows whose best copy is standard definition: candidates for an upgrade search.
            var sd = db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.Files.Any() && i.Files.Max(f => f.Height) < 700 && i.Files.Max(f => f.Width) < 1200);
            // The score counts each film or episode once, however many problems it has.
            var titles = await db.MediaItems.CountAsync(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode || i.Kind == MediaKind.Album, ct);
            var sdIds = await sd.Select(i => i.Id).ToListAsync(ct);
            // Film artwork comes from Radarr or TMDB (with a key): with neither, it isn't something to fix here. Show
            // artwork can always come from TVmaze. (The score is of films and episodes, so artists without a photo
            // don't count either.)
            var findsFilms = options.CurrentValue.Arr.Any(a => a.Kind == ArrKind.Radarr) || options.CurrentValue.Tmdb?.ApiKey is { Length: > 0 };
            var artlessVideo = await db.MediaItems.Where(i => i.ParentId == null && i.PosterUrl == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series))
                .Select(i => new { i.Id, i.Kind }).ToListAsync(ct);
            var artless = artlessVideo.Where(i => i.Kind == MediaKind.Series || findsFilms).Select(i => i.Id).ToList();
            var fileProblems = check.Status.Problems.Select(p => p.ItemId).Distinct().ToList();
            var troubled = new HashSet<Guid>([.. ids, .. unreadable.Select(u => u.MediaItemId), .. sdIds, .. artless, .. fileProblems, .. music.Troubled]);
            var score = new HealthScoreDto(
                titles == 0 ? 100 : (int)Math.Floor(100.0 * Math.Max(0, titles - troubled.Count) / titles),
                titles, troubled.Count, ids.Count, unreadable.Select(u => u.MediaItemId).Distinct().Count(), sdIds.Count, artless.Count, fileProblems.Count,
                artlessVideo.Count - artless.Count, music.CoverlessCount, music.Duplicates.Count, music.Repeated.Count, music.Split.Count);

            // Titles Sonarr or Radarr are already downloading, so the page offers no second search for them.
            var queue = await UpcomingEndpoints.QueueAsync(options.CurrentValue, arr, ct);
            var fixable = unreadable.Select(u => u.MediaItemId).Concat(sdIds).Concat(fileProblems).Distinct().ToList();
            var downloading = new Dictionary<Guid, DownloadStateDto>();
            foreach (var i in await db.MediaItems.Where(i => fixable.Contains(i.Id) && i.ArrId != null).Select(i => new { i.Id, i.Kind, i.ArrId }).ToListAsync(ct))
            {
                if (queue.TryGetValue($"{(i.Kind == MediaKind.Movie ? "radarr" : "sonarr")}-{i.ArrId}", out var state))
                {
                    downloading[i.Id] = state;
                }
            }

            return new LibraryHealthDto(
                score,
                duplicates,
                duplicates.Sum(d => d.ReclaimableBytes),
                unreadable.Select(u => new UnreadableDto(u.Id, u.Path, u.ProbeError!, unreadableItems.GetValueOrDefault(u.MediaItemId))).ToList(),
                missing,
                byTitle,
                await sd.OrderBy(i => i.SortTitle).Take(60).Summaries(profile.Id).ToListAsync(ct),
                await sd.CountAsync(ct),
                downloading,
                await HeldAsync(db, ct),
                music.Coverless,
                music.Duplicates,
                music.Repeated,
                music.Split);
        };
        api.MapLibraryHealthEndpoints(libraryHealth);

        // "Confirm removal" for a held folder: its files really are gone, so the next scan (queued now) hides them, and
        // they're removed after the usual grace period. Only for the result shown, which the request repeats: if a scan
        // has found something different since, it's refused; if the next scan does, it's held again.
        api.MapPost("/library-health/held/{folderId:int}/confirm", async (int folderId, HeldConfirmation shown, FlexarrDbContext db, ILibraryScanQueue queue, CancellationToken ct) =>
        {
            var folder = await db.LibraryFolders.AsNoTracking().FirstOrDefaultAsync(f => f.Id == folderId, ct);
            if (folder is null)
            {
                return Results.NotFound();
            }

            if (folder.Hold is not (FolderHold.FolderEmpty or FolderHold.MostFilesGone))
            {
                return folder.Hold is null
                    ? Problems.Of("This folder isn’t being held.", status: 409)
                    : Problems.Of("The folder isn’t there. Reconnect its drive, or take the folder out of the library in Settings.", status: 409);
            }

            // In one statement, so a scan recording new numbers in between can't slip under this confirmation.
            var since = folder.HeldSince;
            var same = folder.HeldMissing == shown.Missing && folder.HeldKnown == shown.Known && since is not null
                && Math.Abs((since.Value - shown.Since).TotalMilliseconds) < 1
                && await db.LibraryFolders.Where(f => f.Id == folderId && f.Hold == folder.Hold && f.HeldSince == since
                        && f.HeldMissing == shown.Missing && f.HeldKnown == shown.Known)
                    .ExecuteUpdateAsync(u => u.SetProperty(f => f.RemovalConfirmed, true), ct) == 1;
            if (!same)
            {
                return Problems.Of("A scan has found something different since this was shown, so nothing was confirmed. Check the new numbers and confirm again if they’re right.", status: 409);
            }

            await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, "Removal confirmed in Library health"), ct);
            return Results.Accepted();
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Merge, for one artist under two spellings: the others' albums move to the one kept, and the names they were
        // filed under now mean it, so new tracks land there too. Files are left alone.
        api.MapPost("/library-health/merge-artists", async (MergeRequest request, FlexarrDbContext db, CancellationToken ct) =>
            await MergeAsync(db, request, MediaKind.Artist, ct)).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Merge, for one album twice: the others' tracks move to the one kept, and their folders and names now mean it.
        api.MapPost("/library-health/merge-albums", async (MergeRequest request, FlexarrDbContext db, CancellationToken ct) =>
            await MergeAsync(db, request, MediaKind.Album, ct)).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // The Health check button: look at every file on disk (in the background), then report what's wrong.
        api.MapPost("/library-health/check", (HealthCheck check) =>
        {
            check.Start();
            return Results.Accepted("/api/v1/library-health/check");
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapGet("/library-health/check", async (HealthCheck check, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var status = check.Status;
            var ids = status.Problems.Select(p => p.ItemId).Distinct().ToList();
            var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);
            return new HealthCheckReportDto(status.Running, status.Checked, status.Total, status.FinishedAt,
                status.Problems.Select(p => new FileProblemReportDto(p.Localized(), items.GetValueOrDefault(p.ItemId))).ToList());
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Removes a film's or episode's spare copies from disk. Kept: the best copy, and whichever file Sonarr or Radarr
        // track (asked first; if they can't say, nothing is deleted).
        api.MapPost("/items/{id:guid}/remove-duplicates", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Include(i => i.Files).Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Episode) || item.Files.Count < 2)
            {
                return Results.Ok(new DuplicateRemovalDto(0, 0, []));
            }

            var (trackedPath, problem) = await TrackedFileAsync(item, options.CurrentValue, arr, ct);
            if (problem is not null)
            {
                return problem;
            }

            // Copies often tie (the same file twice); then the one Sonarr or Radarr track is the one kept.
            var keep = new HashSet<string>(StringComparer.Ordinal) { Normalize(Rank(item.Files, trackedPath)[0].Path) };
            if (trackedPath is not null)
            {
                keep.Add(trackedPath);
            }

            var removed = 0;
            long freed = 0;
            var skipped = new List<SkippedFileDto>();
            foreach (var file in item.Files.Where(f => !keep.Contains(Normalize(f.Path))).ToList())
            {
                try
                {
                    File.Delete(file.Path); // A file already gone is fine: it's dropped from the library either way.
                    db.MediaFiles.Remove(file);
                    removed++;
                    freed += file.Size;
                    LogRemoved(logs.CreateLogger("Flexarr.Health"), file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new SkippedFileDto(file.Path, ex.Message));
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new DuplicateRemovalDto(removed, freed, skipped));
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Deletes one copy of a film or episode, chosen in Library health. Never the copy Sonarr or Radarr use, or the last one.
        api.MapDelete("/library-health/files/{fileId:guid}", async (Guid fileId, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var file = await db.MediaFiles.FirstOrDefaultAsync(f => f.Id == fileId, ct);
            var item = file is null ? null
                : await db.MediaItems.Include(i => i.Files).Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == file.MediaItemId, ct);
            if (file is null || item is null)
            {
                return Results.NotFound();
            }

            if (item.Files.Count < 2)
            {
                return Problems.Of("This is the only copy. To remove the title, use Delete on its page.", status: 409);
            }

            var (trackedPath, problem) = await TrackedFileAsync(item, options.CurrentValue, arr, ct);
            if (problem is not null)
            {
                return problem;
            }

            if (trackedPath == Normalize(file.Path))
            {
                return Problems.Of("Sonarr or Radarr use this copy. Replace it there, or delete the other copy.", status: 409);
            }

            try
            {
                File.Delete(file.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Problems.Of("Couldn’t delete it: {message}", new { message = ex.Message }, status: 500);
            }

            db.MediaFiles.Remove(file);
            await db.SaveChangesAsync(ct);
            LogRemoved(logs.CreateLogger("Flexarr.Health"), file.Path);
            return Results.Ok(new DuplicateRemovalDto(1, file.Size, []));
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Asks Radarr or Sonarr to search for this film, episode, season or series again (e.g. to upgrade quality).
        api.MapPost("/items/{id:guid}/search-again", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var instance = options.CurrentValue.Arr.FirstOrDefault(a => a.Name == (item.Kind == MediaKind.Season ? item.Parent?.MetadataSource : item.MetadataSource)
                && a.Kind == (item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr));
            object? command = item.Kind switch
            {
                MediaKind.Movie when item.ArrId is { } movieId => new { name = "MoviesSearch", movieIds = new[] { movieId } },
                MediaKind.Episode when item.ArrId is { } episodeId => new { name = "EpisodeSearch", episodeIds = new[] { episodeId } },
                MediaKind.Season when item.Parent?.ArrId is { } seriesId => new { name = "SeasonSearch", seriesId, seasonNumber = item.SeasonNumber },
                MediaKind.Series when item.ArrId is { } seriesId => new { name = "SeriesSearch", seriesId },
                _ => null,
            };
            if (instance is null || command is null)
            {
                return Problems.Of("This title isn’t managed by Sonarr or Radarr, so there’s nothing to search with.", status: 409);
            }

            await arr.SendCommandAsync(instance, command, ct);
            return Results.Accepted();
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    private sealed record MusicHealth(List<ItemSummaryDto> Coverless, int CoverlessCount, List<DuplicateAlbumsDto> Duplicates, List<RepeatedTracksDto> Repeated,
        List<SplitArtistDto> Split, HashSet<Guid> Troubled);

    /// <summary>
    /// Music's checks: albums without a cover (anywhere: a file beside them, art in their tracks, Lidarr or a match),
    /// one release twice (same MusicBrainz release or Lidarr album, in one library), an album with tracks twice, and
    /// one artist under two spellings. The score counts the albums of all but the last (an artist isn't a title).
    /// </summary>
    private static async Task<MusicHealth> MusicAsync(FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        var coverlessQuery = db.MediaItems.Where(i => i.Kind == MediaKind.Album && i.PosterUrl == null);
        var coverlessIds = await coverlessQuery.Select(i => i.Id).ToListAsync(ct);
        var coverless = await coverlessQuery.OrderBy(i => i.SortTitle).Take(100).Summaries(profileId).ToListAsync(ct);

        var albums = await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Album)
            .Select(i => new { i.Id, i.LibraryId, i.MusicBrainzId, i.ArrId, i.MetadataSource, Artist = i.Parent!.Title }).ToListAsync(ct);
        var sameRelease = albums.Where(a => a.MusicBrainzId != null).GroupBy(a => (a.LibraryId, Key: "mb:" + a.MusicBrainzId!.ToLowerInvariant()))
            .Concat(albums.Where(a => a.ArrId != null).GroupBy(a => (a.LibraryId, Key: $"arr:{a.MetadataSource}:{a.ArrId}")))
            .Where(g => g.Count() > 1).Select(g => g.Select(a => a.Id).Order().ToList())
            .DistinctBy(ids => string.Join(',', ids)).ToList();
        var groupIds = sameRelease.SelectMany(g => g).Distinct().ToList();
        var groupItems = await db.MediaItems.Where(i => groupIds.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        var artistOf = albums.ToDictionary(a => a.Id, a => a.Artist);
        var duplicates = sameRelease.Where(g => g.All(groupItems.ContainsKey))
            .Select(g => new DuplicateAlbumsDto(g.Select(id => groupItems[id]).ToList(), artistOf.GetValueOrDefault(g[0])))
            .OrderBy(d => d.Artist).ThenBy(d => d.Albums[0].Title).Take(100).ToList();

        // The same disc and track twice: more tracks than numbers.
        var repeatedCounts = await db.MediaItems.Where(i => i.Kind == MediaKind.Track && i.TrackNumber != null && i.ParentId != null)
            .GroupBy(i => new { Album = i.ParentId!.Value, Disc = i.DiscNumber ?? 1, i.TrackNumber })
            .Where(g => g.Count() > 1).Select(g => new { g.Key.Album, Extra = g.Count() - 1 }).ToListAsync(ct);
        var repeatedByAlbum = repeatedCounts.GroupBy(r => r.Album).ToDictionary(g => g.Key, g => g.Sum(r => r.Extra));
        var repeatedIds = repeatedByAlbum.Keys.ToList();
        var repeatedItems = await db.MediaItems.Where(i => repeatedIds.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        var repeated = repeatedByAlbum.Where(r => repeatedItems.ContainsKey(r.Key))
            .Select(r => new RepeatedTracksDto(repeatedItems[r.Key], artistOf.GetValueOrDefault(r.Key), r.Value))
            .OrderByDescending(r => r.Repeated).Take(100).ToList();

        // One artist, two spellings (or one MusicBrainz id). Placeholders are left out: they're followed, not filed.
        var artists = await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Artist && !i.Placeholder)
            .Select(i => new { i.Id, i.LibraryId, i.Title, i.MusicBrainzId }).ToListAsync(ct);
        var splitGroups = artists.GroupBy(a => (a.LibraryId, Key: "n:" + (MusicNames.Normalise(a.Title) is { Length: > 0 } loose ? loose : MusicKeys.Fold(a.Title))))
            .Concat(artists.Where(a => a.MusicBrainzId != null).GroupBy(a => (a.LibraryId, Key: "mb:" + a.MusicBrainzId!.ToLowerInvariant())))
            .Where(g => g.Count() > 1).Select(g => g.Select(a => a.Id).Order().ToList())
            .DistinctBy(ids => string.Join(',', ids)).ToList();
        var splitIds = splitGroups.SelectMany(g => g).Distinct().ToList();
        var splitItems = await db.MediaItems.Where(i => splitIds.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        var split = splitGroups.Where(g => g.All(splitItems.ContainsKey)).Select(g => new SplitArtistDto(g.Select(id => splitItems[id]).ToList()))
            .OrderBy(s => s.Artists[0].Title).Take(100).ToList();

        return new MusicHealth(coverless, coverlessIds.Count, duplicates, repeated, split, [.. coverlessIds, .. groupIds, .. repeatedIds]);
    }

    private static async Task<IResult> MergeAsync(FlexarrDbContext db, MergeRequest request, MediaKind kind, CancellationToken ct)
    {
        var others = request.Merge.Where(m => m != request.Keep).Distinct().ToList();
        if (others.Count == 0 || others.Count > 20)
        {
            return Problems.Field("Choose one to keep and up to 20 to merge into it.", field: "merge");
        }

        var items = await db.MediaItems.Where(i => (i.Id == request.Keep || others.Contains(i.Id)) && i.Kind == kind).ToListAsync(ct);
        var keep = items.FirstOrDefault(i => i.Id == request.Keep);
        if (keep is null || items.Count != others.Count + 1)
        {
            return Results.NotFound();
        }

        if (items.Any(i => i.LibraryId != keep.LibraryId))
        {
            return kind == MediaKind.Artist
                ? Problems.Of("Only artists in the same library can be merged.", status: 409)
                : Problems.Of("Only albums in the same library can be merged.", status: 409);
        }

        var otherIds = others.ToList();
        // An artist's albums before the merge, with whose they were: the same album under both spellings is merged after.
        var albumsBefore = kind != MediaKind.Artist ? [] : await db.MediaItems.AsNoTracking()
            .Where(i => i.Kind == MediaKind.Album && i.ParentId != null && (i.ParentId == keep.Id || otherIds.Contains(i.ParentId.Value)))
            .Select(i => new AlbumBefore(i.Id, i.ParentId!.Value, i.Title, i.Year, i.Children.Count)).ToListAsync(ct);
        var albumIds = albumsBefore.Select(a => (Guid?)a.Id).ToList();
        var trackPlaces = (await db.MediaItems.AsNoTracking().Where(t => albumIds.Contains(t.ParentId))
                .Select(t => new { Album = t.ParentId!.Value, t.DiscNumber, t.TrackNumber }).ToListAsync(ct))
            .ToLookup(t => t.Album, t => t.TrackNumber is { } n ? (t.DiscNumber ?? 1, n) : ((int, int)?)null);
        var aliases = new List<string>();
        foreach (var other in items.Where(i => i.Id != keep.Id))
        {
            // Whatever the scanner would have filed tracks under for this one now means the one kept.
            if (kind == MediaKind.Artist)
            {
                aliases.Add(MusicKeys.ArtistKey(other.Title));
                if (other.OriginalTitle is { } tagged)
                {
                    aliases.Add(MusicKeys.ArtistKey(tagged));
                }
            }
            else
            {
                if (other.ParentId is { } artistId)
                {
                    aliases.Add(MusicKeys.AlbumKey(artistId, other.Title));
                    if (other.OriginalTitle is { } tagged)
                    {
                        aliases.Add(MusicKeys.AlbumKey(artistId, tagged));
                    }
                }

                if (other.Path is { } folder && (keep.Path is null || PathComparison.Key(folder) != PathComparison.Key(keep.Path)))
                {
                    aliases.Add(MusicKeys.PathKey(folder));
                }
            }

            keep.MusicBrainzId ??= other.MusicBrainzId;
            keep.PosterUrl ??= other.PosterUrl;
            keep.Year ??= other.Year;
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Children move over, and aliases that meant a merged one now mean the one kept.
        await db.MediaItems.Where(i => i.ParentId != null && otherIds.Contains(i.ParentId.Value)).ExecuteUpdateAsync(u => u.SetProperty(i => i.ParentId, keep.Id), ct);
        await db.MusicAliases.Where(a => otherIds.Contains(a.ItemId)).ExecuteUpdateAsync(u => u.SetProperty(a => a.ItemId, keep.Id), ct);
        var existing = await db.MusicAliases.Where(a => a.LibraryId == keep.LibraryId).Select(a => a.Key).ToListAsync(ct);
        db.MusicAliases.AddRange(aliases.Distinct().Except(existing).Select(key => new MusicAlias { LibraryId = keep.LibraryId, Key = key, ItemId = keep.Id }));
        db.MediaItems.RemoveRange(items.Where(i => i.Id != keep.Id));
        keep.Placeholder = false;
        keep.MetadataUpdatedAt = null; // Refreshed again with everything it now has.
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        if (kind == MediaKind.Artist)
        {
            await MergeSameAlbumsAsync(db, keep.Id, albumsBefore, trackPlaces, ct);
        }

        return Results.NoContent();
    }

    private sealed record AlbumBefore(Guid Id, Guid ArtistId, string Title, int? Year, int Tracks);

    /// <summary>
    /// After an artist merge, the albums both spellings had (one title, case, accents and punctuation aside, and no
    /// two years) become one, as merging them by hand would: the kept artist's copy stays, or else the fullest. Titles
    /// one spelling has twice (two "Live" albums), and two copies with a track in the same place (a full MP3 and a full
    /// FLAC: merged, every track would show twice), are left for Library health to show.
    /// </summary>
    private static async Task MergeSameAlbumsAsync(FlexarrDbContext db, Guid keptArtist, List<AlbumBefore> albums, ILookup<Guid, (int Disc, int Track)?> trackPlaces,
        CancellationToken ct)
    {
        foreach (var same in albums.GroupBy(a => MusicNames.Normalise(a.Title)).Where(g => g.Key.Length > 0))
        {
            var group = same.ToList();
            if (group.Select(a => a.ArtistId).Distinct().Count() < 2 || group.GroupBy(a => a.ArtistId).Any(g => g.Count() > 1)
                || group.Where(a => a.Year is not null).Select(a => a.Year).Distinct().Count() > 1
                || Overlap(group.Select(a => trackPlaces[a.Id])))
            {
                continue;
            }

            var keep = group.OrderByDescending(a => a.ArtistId == keptArtist).ThenByDescending(a => a.Tracks).First();
            db.ChangeTracker.Clear();
            await MergeAsync(db, new MergeRequest(keep.Id, group.Select(a => a.Id).ToList()), MediaKind.Album, ct);
        }
    }

    /// <summary>Whether two albums' tracks share a disc and track number; a track without a number could be anywhere.</summary>
    private static bool Overlap(IEnumerable<IEnumerable<(int Disc, int Track)?>> albums)
    {
        var seen = new HashSet<(int, int)>();
        foreach (var places in albums)
        {
            var list = places.ToList();
            if (list.Any(p => p is null))
            {
                return true;
            }

            if (list.Any(p => seen.Contains(p!.Value)))
            {
                return true;
            }

            seen.UnionWith(list.Select(p => p!.Value));
        }

        return false;
    }

    private static async Task<List<HeldFolderDto>> HeldAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var held = await db.LibraryFolders.Where(f => f.Hold != null).OrderBy(f => f.Path).ToListAsync(ct);
        var ids = held.Select(f => f.LibraryId).ToList();
        var libraries = await db.Libraries.IgnoreQueryFilters().Where(l => ids.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => new { l.Name, l.Type }, ct);
        return held.Select(f => new HeldFolderDto(f.Id, libraries.GetValueOrDefault(f.LibraryId)?.Name ?? "", f.Path, f.Hold!.Value, f.HeldMissing, f.HeldKnown,
            f.HeldSince ?? DateTime.UtcNow, f.HeldScans, f.RemovalConfirmed, f.Hold != FolderHold.FolderMissing, libraries.GetValueOrDefault(f.LibraryId)?.Type)).ToList();
    }

    /// <summary>
    /// The local path of the file Sonarr or Radarr track for a film or episode (null if none), or the problem to return
    /// when they can't say, in which case nothing may be deleted.
    /// </summary>
    private static async Task<(string? Path, IResult? Problem)> TrackedFileAsync(MediaItem item, FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var kind = item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr;
        var source = item.Kind == MediaKind.Episode ? item.Parent?.Parent?.MetadataSource ?? item.MetadataSource : item.MetadataSource;
        var instance = options.Arr.FirstOrDefault(a => a.Name == source && a.Kind == kind);
        if (instance is null || item.ArrId is not { } arrId)
        {
            return (null, Problems.Of("{kind} doesn’t manage {title}, so Flexarr can’t tell which copy it uses. Remove the spare in the Finder.", new { kind, title = item.Title }, status: 409));
        }

        try
        {
            var tracked = item.Kind == MediaKind.Movie
                ? (await arr.GetMovieAsync(instance, arrId, ct)).MovieFile?.Path
                : await arr.GetEpisodeAsync(instance, arrId, ct) is { EpisodeFileId: > 0 } episode ? (await arr.GetEpisodeFileAsync(instance, episode.EpisodeFileId, ct)).Path : null;
            return (tracked is null ? null : Normalize(PathMapper.Map(tracked, options.PathMappings)), null);
        }
        catch (HttpRequestException ex)
        {
            return (null, Problems.Of("{name} didn’t say which copy it uses ({status}), so nothing was deleted.", new { name = instance.Name, status = (int?)ex.StatusCode ?? 0 }, status: 502));
        }
    }

    /// <summary>
    /// Best copy first: highest resolution, then bitrate. Ties go to the file Sonarr or Radarr track, then the bigger one.
    /// </summary>
    private static List<MediaFile> Rank(IEnumerable<MediaFile> files, string? trackedPath) =>
        files.OrderByDescending(f => Math.Max(f.Height ?? 0, (f.Width ?? 0) * 9 / 16)).ThenByDescending(f => f.Bitrate ?? 0)
            .ThenByDescending(f => trackedPath is not null && Normalize(f.Path) == trackedPath)
            .ThenByDescending(f => f.Size).ToList();

    private static string Normalize(string path) => path.Normalize(System.Text.NormalizationForm.FormC);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed spare copy {Path}")]
    private static partial void LogRemoved(ILogger logger, string path);
}
