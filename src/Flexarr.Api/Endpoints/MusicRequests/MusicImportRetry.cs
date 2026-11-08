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
        var asked = (await c.Db.MediaRequests.AsNoTracking()
            .Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.RequestedAt > since && r.MusicBrainzId != null)
            .Select(r => r.MusicBrainzId!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        if (asked.Count == 0)
        {
            return;
        }

        foreach (var lidarr in c.Lidarrs)
        {
            try
            {
                await SweepAsync(c, lidarr, asked, ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                LogLidarrAway(logger, ex, lidarr.Name); // tried again at the next sweep
            }
        }
    }

    private async Task SweepAsync(MusicRequestContext c, ArrInstance lidarr, HashSet<string> asked, CancellationToken ct)
    {
        var queue = await c.Client.GetQueueAsync(lidarr, ct);
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

    private static bool IsMissingTracks(ArrQueueItem item) =>
        item.TrackedDownloadState == "importFailed"
        && item.StatusMessages?.Any(s => s.Messages?.Any(m => m.Contains(MissingTracks, StringComparison.OrdinalIgnoreCase)) == true) == true;

    [LoggerMessage(Level = LogLevel.Information, Message = "{Release} is missing tracks of {Album}: blocklisted it and searching again")]
    private static partial void LogRetrying(ILogger logger, string album, string? release);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't check {Lidarr}'s queue for failed imports")]
    private static partial void LogLidarrAway(ILogger logger, Exception ex, string lidarr);
}
