using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Downloads;

/// <summary>
/// A download, with what the device needs to show and play it with no connection: the title, its markers (for Skip
/// Intro) and the next episode (to fetch that one too). <paramref name="FileName"/> is what the file comes as once
/// it's ready ("….mp4", or a track's "….mp3", "….flac" or "….m4a"), so the device keeps it under a name its player opens.
/// </summary>
public sealed record DownloadDto(
    Guid Id,
    Guid ItemId,
    DownloadQuality Quality,
    DownloadStatus Status,
    double Progress,
    long? SizeBytes,
    string? Error,
    ItemSummaryDto Item,
    IReadOnlyList<MarkerDto> Markers,
    double DurationSeconds,
    Guid? NextId,
    DateTime CreatedAt,
    string? FileName = null);

/// <param name="Available">False for an Original the phone couldn't play: another size will do.</param>
public sealed record DownloadOptionDto(DownloadQuality Quality, long EstimatedBytes, bool Available);

public sealed record DownloadRequest(Guid ItemId, DownloadQuality Quality);

public static class DownloadEndpoints
{
    /// <summary>Downloads for offline watching and listening, each profile's own.</summary>
    public static RouteGroupBuilder MapDownloadEndpoints(this RouteGroupBuilder api)
    {
        // Films and episodes come in sizes; a track (or a whole album, its tracks together) comes one way: as it is,
        // or as AAC when the phone can't play the file.
        api.MapGet("/items/{id:guid}/download-options", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
        {
            var kind = await db.MediaItems.Where(i => i.Id == id).Select(i => (MediaKind?)i.Kind).FirstOrDefaultAsync(ct);
            if (kind is MediaKind.PodcastEpisode)
            {
                // As the feed has it; the size is the feed's word, or a guess from its length at 128 kbit/s.
                var enclosure = await db.PodcastEpisodes.AsNoTracking().FirstOrDefaultAsync(e => e.MediaItemId == id, ct);
                return enclosure is null
                    ? Results.NotFound()
                    : Results.Ok(new List<DownloadOptionDto> { new(DownloadQuality.Original, enclosure.LengthBytes ?? (long)((enclosure.DurationSeconds ?? 3600) * 16_000), true) });
            }

            if (kind is MediaKind.Track or MediaKind.Album)
            {
                var files = await TrackFilesAsync(db, id, kind.Value, ct);
                return files.Count == 0
                    ? Results.NotFound()
                    : Results.Ok(new List<DownloadOptionDto> { new(DownloadQuality.Original, files.Sum(f => DownloadPlanner.EstimateBytes(f, DownloadQuality.Original)), true) });
            }

            var file = kind is MediaKind.Movie or MediaKind.Episode ? await BestFileAsync(db, id, ct) : null;
            return file is null
                ? Results.NotFound()
                : Results.Ok(Enum.GetValues<DownloadQuality>().Select(q => new DownloadOptionDto(q, DownloadPlanner.EstimateBytes(file, q),
                    q != DownloadQuality.Original || DownloadPlanner.CanKeepOriginal(file))).ToList());
        }).WithTags("Downloads");

        // "/downloads" is Coming soon's (what Sonarr and Radarr are fetching).
        var group = api.MapGroup("/offline").WithTags("Downloads");

        group.MapGet("/", async (FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, CancellationToken ct) =>
        {
            var ids = await db.Downloads.Where(d => d.ProfileId == profile.Id && d.Status != DownloadStatus.Removed).OrderBy(d => d.CreatedAt).Select(d => d.Id).ToListAsync(ct);
            var list = new List<DownloadDto>();
            foreach (var id in ids)
            {
                if (await ToDtoAsync(db, playback, profile, id, ct) is { } dto)
                {
                    list.Add(dto);
                }
            }

            return list;
        });

        group.MapPost("/", async (DownloadRequest request, FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.ItemId, ct);
            if (item is null || item.Kind is not (MediaKind.Movie or MediaKind.Episode or MediaKind.Track or MediaKind.PodcastEpisode)
                || (item.Kind == MediaKind.PodcastEpisode
                    ? !await db.PodcastEpisodes.AnyAsync(e => e.MediaItemId == item.Id, ct)
                    : await BestFileAsync(db, item.Id, ct) is null))
            {
                return Results.NotFound();
            }

            // A track or podcast episode has no sizes: it's always the original (or its AAC), whatever the device's setting for films.
            var quality = item.Kind is MediaKind.Track or MediaKind.PodcastEpisode ? DownloadQuality.Original : request.Quality;

            // Asked for again (the app reinstalled, say): the same one, unless it failed or ran out, or a device already
            // has it: its copy is gone then, and another device of the profile's (an iPad beside the phone) needs a new one.
            var existing = await db.Downloads.FirstOrDefaultAsync(d => d.ProfileId == profile.Id && d.MediaItemId == item.Id && d.Quality == quality
                && d.Status != DownloadStatus.Failed && d.Status != DownloadStatus.Expired && d.Status != DownloadStatus.Removed
                && d.Status != DownloadStatus.Received, ct);
            if (existing is null)
            {
                existing = new Download { ProfileId = profile.Id, MediaItemId = item.Id, Quality = quality, Status = DownloadStatus.Queued };
                db.Downloads.Add(existing);
                await db.SaveChangesAsync(ct);
            }

            return Results.Ok(await ToDtoAsync(db, playback, profile, existing.Id, ct));
        });

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, CancellationToken ct) =>
            await ToDtoAsync(db, playback, profile, id, ct) is { } dto ? Results.Ok(dto) : Results.NotFound());

        // The file itself, resumable (ranges), once it's ready.
        group.MapGet("/{id:guid}/file", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var download = await db.Downloads.AsNoTracking().Include(d => d.MediaItem).FirstOrDefaultAsync(d => d.Id == id && d.ProfileId == profile.Id, ct);
            return download is { Status: DownloadStatus.Ready, FilePath: { } path } && File.Exists(path)
                ? Results.File(path, DownloadFiles.ContentType(download), DownloadFiles.Name(download), enableRangeProcessing: true)
                : Results.NotFound();
        });

        // The device has it: the server's copy can go.
        group.MapPost("/{id:guid}/received", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await db.Downloads.FirstOrDefaultAsync(d => d.Id == id && d.ProfileId == profile.Id, ct) is not { } download)
            {
                return Results.NotFound();
            }

            DownloadPreparer.DeleteFile(download);
            download.Status = DownloadStatus.Received;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        // Taken off the device. A podcast episode's is remembered as removed, so the phone isn't handed it again to
        // download by itself (see /podcasts/auto-downloads); anything else is forgotten.
        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (await db.Downloads.Include(d => d.MediaItem).FirstOrDefaultAsync(d => d.Id == id && d.ProfileId == profile.Id, ct) is { } download)
            {
                DownloadPreparer.DeleteFile(download);
                if (download.MediaItem?.Kind == MediaKind.PodcastEpisode)
                {
                    download.Status = DownloadStatus.Removed;
                }
                else
                {
                    db.Downloads.Remove(download);
                }

                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        });

        // Watched with no connection: recorded when the device is back online, unless something newer was since.
        api.MapPost("/items/{id:guid}/offline-progress", async (Guid id, OfflineProgressRequest progress, PlaybackService playback, CancellationToken ct) =>
            await playback.ReportOfflineAsync(id, progress, ct) ? Results.NoContent() : Results.NotFound()).WithTags("Downloads");

        return api;
    }

    private static Task<MediaFile?> BestFileAsync(FlexarrDbContext db, Guid itemId, CancellationToken ct) =>
        db.MediaFiles.AsNoTracking().Include(f => f.Streams)
            .Where(f => f.MediaItemId == itemId && f.ProbeError == null && f.MissingSince == null)
            .OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefaultAsync(ct);

    /// <summary>The file of a track, or the files of an album's tracks (one each), for sizing a download.</summary>
    private static async Task<List<MediaFile>> TrackFilesAsync(FlexarrDbContext db, Guid id, MediaKind kind, CancellationToken ct)
    {
        List<Guid> tracks = kind == MediaKind.Track
            ? [id]
            : await db.MediaItems.Where(i => i.ParentId == id && i.Kind == MediaKind.Track).Select(i => i.Id).ToListAsync(ct);
        var files = new List<MediaFile>();
        foreach (var track in tracks)
        {
            if (await BestFileAsync(db, track, ct) is { } file)
            {
                files.Add(file);
            }
        }

        return files;
    }

    private static async Task<DownloadDto?> ToDtoAsync(FlexarrDbContext db, PlaybackService playback, CurrentProfile profile, Guid id, CancellationToken ct)
    {
        var download = await db.Downloads.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id && d.ProfileId == profile.Id, ct);
        var item = download is null ? null : await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == download.MediaItemId, ct);
        if (download is null || item is null)
        {
            return null; // gone, or in a library this profile can't see
        }

        download.MediaItem = item;

        var summary = await db.MediaItems.Where(i => i.Id == item.Id).Summaries(profile.Id).FirstAsync(ct);
        var file = await db.MediaFiles.AsNoTracking().Include(f => f.Markers)
            .Where(f => f.MediaItemId == item.Id && f.ProbeError == null && f.MissingSince == null)
            .OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefaultAsync(ct);
        var next = await playback.NextEpisodeAsync(item, ct);
        return new DownloadDto(download.Id, item.Id, download.Quality, download.Status, download.Progress, download.SizeBytes, download.Error, summary,
            file?.Markers.OrderBy(m => m.StartSeconds).Select(m => new MarkerDto(m.Type, m.StartSeconds, m.EndSeconds)).ToList() ?? [],
            file?.DurationSeconds ?? summary.DurationSeconds ?? (item.RuntimeMinutes ?? 0) * 60, next?.Id, download.CreatedAt,
            download is { Status: DownloadStatus.Ready, FilePath: not null } ? DownloadFiles.Name(download) : null);
    }
}

/// <summary>
/// What a download's file is called and typed when it's fetched. Films and episodes are always MP4s (an original MOV
/// or M4V plays as one); a track keeps its own kind of file, which the phone's player opens by its extension.
/// </summary>
public static class DownloadFiles
{
    private static readonly Dictionary<string, string> AudioTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mp3"] = "audio/mpeg",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".aiff"] = "audio/aiff",
        [".aac"] = "audio/aac",
    };

    /// <summary>
    /// ".mp4", or a track's or podcast episode's ".mp3", ".flac", ".m4a", ".aac", ".wav" or ".aiff" (an MP4 or MOV holding
    /// only sound is an M4A).
    /// </summary>
    public static string Extension(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (download.MediaItem?.Kind is not (MediaKind.Track or MediaKind.PodcastEpisode))
        {
            return ".mp4";
        }

        var extension = Path.GetExtension(download.FilePath ?? "").ToLowerInvariant();
        return extension switch
        {
            ".aif" => ".aiff",
            ".mp4" or ".mov" or ".m4b" => ".m4a",
            _ when AudioTypes.ContainsKey(extension) => extension,
            _ => ".m4a",
        };
    }

    public static string Name(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        return $"{download.Id:N}{Extension(download)}";
    }

    public static string ContentType(Download download) => AudioTypes.TryGetValue(Extension(download), out var type) ? type : "video/mp4";
}
