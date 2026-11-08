using System.Collections.Concurrent;
using System.Text.Json;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Lidarr grabs any release that looks like the album, then refuses it at import when tracks are missing (a bonus disc,
/// a single), and never tries another: it counts as a finished download, so its own redownload doesn't start. For
/// albums asked for in Flexarr, this blocklists such a release and searches again. A download with all its tracks that
/// merely scored low stays put for someone to import by hand: it's probably the right music.
/// <para>
/// Lidarr also adds an artist unmonitored when an album brings them in, whatever it's told, and then won't keep looking
/// for the album. So an artist a request added is monitored here, with the album, while the album is missing. Nothing
/// else of theirs is monitored, so this starts no discography. An artist Lidarr had already is the admin's to decide.
/// </para>
/// </summary>
public sealed partial class MusicImportRetry(IServiceScopeFactory scopes, ILogger<MusicImportRetry> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);

    /// <summary>Copies rejected per album, so an album only ever posted incomplete isn't chased for good.</summary>
    internal const int MaxRetries = 3;

    /// <summary>In Lidarr's rejections ("Has missing tracks", "Album match is not close enough: … [tracks, missing tracks]").</summary>
    private const string MissingTracks = "missing tracks";

    /// <summary>Kept in memory: a restart only starts the count afresh, and each try still rules out a release for good.</summary>
    private readonly ConcurrentDictionary<string, int> _retries = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Every);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await SweepAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public async Task SweepAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var c = scope.ServiceProvider.GetRequiredService<MusicRequestContext>();
        if (c.Lidarrs.Count == 0)
        {
            return;
        }

        var since = DateTime.UtcNow - MusicMatcher.RequestedFor;
        var requests = await c.Db.MediaRequests.AsNoTracking()
            .Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.RequestedAt > since && r.MusicBrainzId != null)
            .Select(r => new AlbumAsked(r.MusicBrainzId!, r.AddedArtist ? r.ArtistMusicBrainzId : null)).ToListAsync(ct);
        if (requests.Count == 0)
        {
            return;
        }

        foreach (var lidarr in c.Lidarrs)
        {
            try
            {
                await SweepAsync(c, lidarr, requests, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogLidarrAway(logger, ex, lidarr.Name); // tried again at the next sweep
            }
        }
    }

    private sealed record AlbumAsked(string Album, string? ArtistItAdded);

    private async Task SweepAsync(MusicRequestContext c, ArrInstance lidarr, List<AlbumAsked> requests, CancellationToken ct)
    {
        var queue = await c.Client.GetQueueAsync(lidarr, ct);
        await KeepLookingAsync(c, lidarr, requests, queue, ct);
        var asked = requests.Select(r => r.Album).ToHashSet(StringComparer.Ordinal);
        foreach (var item in queue)
        {
            if (item.Album is not { } album || item.AlbumId is not { } albumId || album.ForeignAlbumId is not { } id || !asked.Contains(id) || !IsMissingTracks(item))
            {
                continue;
            }

            // A download that carries other albums too might be fine for them.
            if (item.DownloadId is not null && queue.Any(q => q.DownloadId == item.DownloadId && q.AlbumId != albumId))
            {
                continue;
            }

            var key = $"{lidarr.BaseUrl}|{albumId}";
            if (_retries.GetValueOrDefault(key) >= MaxRetries)
            {
                continue; // stays as "couldn't import"
            }

            _retries.AddOrUpdate(key, 1, (_, tries) => tries + 1);
            if (!album.Monitored)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, [albumId], monitored: true, ct); // Lidarr keeps looking if this search finds nothing
            }

            await c.Client.BlocklistFromQueueAsync(lidarr, item.Id, ct);
            await c.Client.SearchAlbumsAsync(lidarr, [albumId], ct);
            MusicRequested.Forget(c.Cache);
            LogRetrying(logger, album.Title, item.Title);
        }
    }

    /// <summary>Monitors the artists requests added, and their albums still missing, and searches for what that just put back on the list.</summary>
    private static async Task KeepLookingAsync(MusicRequestContext c, ArrInstance lidarr, List<AlbumAsked> requests, IReadOnlyList<ArrQueueItem> queue, CancellationToken ct)
    {
        var byArtist = requests.Where(r => r.ArtistItAdded is not null).ToLookup(r => r.ArtistItAdded!, r => r.Album, StringComparer.Ordinal);
        if (byArtist.Count == 0)
        {
            return;
        }

        var changed = false;
        foreach (var artist in await c.Client.GetArtistsAsync(lidarr, ct))
        {
            if (artist.ForeignArtistId is not { } artistId || !byArtist.Contains(artistId))
            {
                continue;
            }

            var missing = (await c.Client.GetArtistAlbumsAsync(lidarr, artist.Id, ct))
                .Where(a => a.ForeignAlbumId is { } id && byArtist[artistId].Contains(id, StringComparer.Ordinal)
                    && !(a.Statistics is { TrackFileCount: > 0 } s && s.TrackFileCount >= s.TrackCount))
                .ToList();
            var unmonitored = missing.Where(a => !a.Monitored).Select(a => a.Id).ToList();
            if (missing.Count == 0 || (artist.Monitored && unmonitored.Count == 0))
            {
                continue;
            }

            if (!artist.Monitored)
            {
                await c.Client.MonitorArtistAsync(lidarr, artist.Id, ct);
            }

            if (unmonitored.Count > 0)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, unmonitored, monitored: true, ct);
            }

            var search = missing.Where(a => !queue.Any(q => q.AlbumId == a.Id)).Select(a => a.Id).ToList();
            if (search.Count > 0)
            {
                await c.Client.SearchAlbumsAsync(lidarr, search, ct);
            }

            changed = true;
        }

        if (changed)
        {
            MusicMatcher.Forget(c.Cache, lidarr);
            MusicRequested.Forget(c.Cache);
        }
    }

    private static bool IsMissingTracks(ArrQueueItem item) =>
        item.TrackedDownloadState == "importFailed"
        && item.StatusMessages?.Any(s => s.Messages?.Any(m => m.Contains(MissingTracks, StringComparison.OrdinalIgnoreCase)) == true) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Release} is missing tracks of {Album}: blocklisted it and searching again")]
    private static partial void LogRetrying(ILogger logger, string album, string? release);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't check {Lidarr}'s queue for failed imports")]
    private static partial void LogLidarrAway(ILogger logger, Exception ex, string lidarr);
}
