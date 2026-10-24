using System.Collections.Concurrent;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming.Markers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>Seasons to analyse ahead of the background sweep, e.g. the one someone just started watching.</summary>
public sealed class MarkerQueue
{
    private readonly ConcurrentQueue<Guid> _seasons = new();

    public void Prioritize(Guid seasonId) => _seasons.Enqueue(seasonId);

    public bool TryNext(out Guid seasonId) => _seasons.TryDequeue(out seasonId);
}

/// <summary>Works through the library finding intros and credits, one season at a time, while the server is idle.</summary>
public sealed partial class MarkerDetectionWorker(
    IServiceScopeFactory scopes,
    MarkerQueue queue,
    PlaybackSessions sessions,
    IOptionsMonitor<FlexarrOptions> options,
    ILogger<MarkerDetectionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Let startup scans go first.
        var detector = new MarkerDetector(options.CurrentValue.FfmpegPath, options.CurrentValue.FfprobePath);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var worked = await RunOnceAsync(detector, stoppingToken);
                // Give way to viewers: analysis competes with transcodes for disk and CPU.
                var busy = sessions.Active.Any(s => s.Transcode?.IsRunning == true);
                await Task.Delay(!worked ? TimeSpan.FromMinutes(10) : busy ? TimeSpan.FromSeconds(20) : TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <returns>false when there's nothing left to analyse.</returns>
    private async Task<bool> RunOnceAsync(MarkerDetector detector, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();

        Guid? seasonId = queue.TryNext(out var prioritized) ? prioritized : null;
        if (seasonId is null)
        {
            // Seasons of shows being watched first, then the most recently added.
            seasonId = await db.MediaItems
                .Where(s => s.Kind == MediaKind.Season && s.Children.Any(e => e.Files.Any(f => f.MarkersScannedAt == null && f.ProbeError == null)))
                .OrderByDescending(s => s.Parent!.Children.SelectMany(x => x.Children).SelectMany(e => e.PlaybackStates).Max(p => (DateTime?)p.LastPlayedAt))
                .ThenByDescending(s => s.Children.Max(e => e.AddedAt))
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (seasonId is { } id)
        {
            await AnalyseSeasonAsync(db, detector, id, ct);
            return true;
        }

        // Movies: chapter markers only (there's nothing to compare against).
        var movies = await db.MediaFiles.Where(f => f.MarkersScannedAt == null && f.ProbeError == null && f.MediaItem!.Kind == MediaKind.Movie)
            .Take(20).ToListAsync(ct);
        foreach (var file in movies)
        {
            Save(db, file, await detector.ChapterMarkersAsync(file.Path, ct));
        }

        await db.SaveChangesAsync(ct);
        return movies.Count > 0;
    }

    private async Task AnalyseSeasonAsync(FlexarrDbContext db, MarkerDetector detector, Guid seasonId, CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        var episodes = await db.MediaItems.Where(e => e.ParentId == seasonId).OrderBy(e => e.SortTitle)
            .Select(e => e.Files.Where(f => f.ProbeError == null).OrderByDescending(f => f.Height).FirstOrDefault())
            .ToListAsync(ct);
        var files = episodes.OfType<MediaFile>().ToList();
        if (files.Count == 0)
        {
            return;
        }

        var found = await detector.DetectSeasonAsync(files, ct);
        foreach (var file in files)
        {
            Save(db, file, found[file.Id]);
        }

        // Other versions of the same episodes aren't analysed; don't keep picking this season.
        var fileIds = files.Select(f => f.Id).ToList();
        await db.MediaFiles.Where(f => f.MediaItem!.ParentId == seasonId && !fileIds.Contains(f.Id) && f.MarkersScannedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MarkersScannedAt, DateTime.UtcNow), ct);
        await db.SaveChangesAsync(ct);

        var title = await db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.Parent!.Title + " " + s.Title).FirstAsync(ct);
        LogSeason(title, files.Count, found.Values.Count(m => m.Any(x => x.Type == MarkerType.Intro)),
            found.Values.Count(m => m.Any(x => x.Type == MarkerType.Credits)), TimeProvider.System.GetElapsedTime(started).TotalSeconds);
    }

    private static void Save(FlexarrDbContext db, MediaFile file, IEnumerable<DetectedMarker> markers)
    {
        db.MediaMarkers.RemoveRange(db.MediaMarkers.Where(m => m.MediaFileId == file.Id));
        db.MediaMarkers.AddRange(markers.Select(m => new MediaMarker
        {
            MediaFileId = file.Id,
            Type = m.Type,
            StartSeconds = Math.Round(m.Start, 2),
            EndSeconds = Math.Round(m.End, 2),
            Source = m.Source,
        }));
        file.MarkersScannedAt = DateTime.UtcNow;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysed {Season}: {Episodes} episodes, {Intros} intros, {Credits} credits in {Seconds:F0}s")]
    private partial void LogSeason(string season, int episodes, int intros, int credits, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Intro detection failed")]
    private partial void LogFailed(Exception exception);
}
