using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Scanning;

/// <summary>
/// Runs scan requests one at a time. Bursts (a season pack import fires a webhook per episode)
/// are coalesced into one scan per folder.
/// </summary>
public sealed partial class LibraryScanWorker(
    ILibraryScanQueue queue,
    IServiceScopeFactory scopes,
    ILogger<LibraryScanWorker> logger,
    LibraryWorkGate gate) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScheduledScanInterval = TimeSpan.FromHours(4);

    /// <summary>
    /// A folder whose last full scan began longer ago than this is scanned in full again: about once a day at these
    /// ticks. In between, a sweep looks only at the folders that changed.
    /// </summary>
    public static readonly TimeSpan FullScanAfter = TimeSpan.FromHours(22);

    /// <summary>Folder times a little older than the last scan still count: the drive's clock and the server's can differ.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMinutes(10);

    /// <summary>
    /// More changed folders than this and the library folder is scanned in full instead: each scan of a folder also
    /// looks over the library's titles, which for this many costs more than one look at every file.
    /// </summary>
    private const int SweepAtMost = 50;

    /// <summary>When the last sweep of each library folder began, since the server started.</summary>
    private readonly Dictionary<string, DateTime> _swept = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Catch up on anything that changed while the server was down (and finish interrupted scans): a sweep of what
        // changed since the last full scan, or a full scan when that's a day old.
        _ = ScheduleAsync(stoppingToken);

        await foreach (var first in queue.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(Debounce, stoppingToken);

            var batch = new List<LibraryScanRequest> { first };
            while (queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            // The endpoints refuse such paths; one getting here anyway must not stop every scan after it.
            foreach (var unusable in batch.Where(r => PathComparison.HasControlCharacters(r.Path)))
            {
                LogUnusablePath(PathComparison.Printable(unusable.Path), PathComparison.Printable(unusable.Reason));
            }

            List<LibraryScanRequest> requests;
            try
            {
                requests = Coalesce(batch.Where(r => !PathComparison.HasControlCharacters(r.Path))).ToList();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogBatchFailed(ex, batch.Count);
                continue;
            }

            foreach (var request in requests)
            {
                try
                {
                    using var entered = await gate.EnterAsync(stoppingToken);
                    await ScanAsync(request, stoppingToken);
                }
                // Anything but shutdown, including an HTTP timeout (a TaskCanceledException), is one failed scan.
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogScanFailed(ex, PathComparison.Printable(request.Path));
                }
            }
        }
    }

    /// <summary>Picks up changes the *arr webhooks don't cover (files added by hand, other tools).</summary>
    private async Task ScheduleAsync(CancellationToken ct)
    {
        await EnqueueAllSafelyAsync("Startup", ct);
        using var timer = new PeriodicTimer(ScheduledScanInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await EnqueueAllSafelyAsync("Scheduled", ct);
        }
    }

    /// <summary>A failure here (the database busy, say) is logged and the next tick tries again.</summary>
    private async Task EnqueueAllSafelyAsync(string reason, CancellationToken ct)
    {
        try
        {
            await EnqueueAllAsync(reason, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogEnqueueFailed(ex, reason);
        }
    }

    /// <summary>
    /// Each library folder in full when its last full scan is a day old (or it's never had one), otherwise just the
    /// folders in it changed since that scan or the last sweep, whichever is later.
    /// </summary>
    internal async Task EnqueueAllAsync(string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        foreach (var library in await db.Libraries.AsNoTracking().Include(l => l.Folders).ToListAsync(ct))
        {
            foreach (var folder in library.Folders)
            {
                var now = DateTime.UtcNow;
                if (folder.FullScanAt is not { } full || now - full > FullScanAfter || library.Type == LibraryType.Podcasts)
                {
                    await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, reason), ct);
                    _swept[folder.Path] = now;
                    continue;
                }

                var since = (_swept.TryGetValue(folder.Path, out var swept) && swept > full ? swept : full) - Slack;
                List<string> changed;
                try
                {
                    changed = await LibrarySweep.ChangedAsync(db, library, folder.Path, since, ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LogSweepFailed(ex, PathComparison.Printable(folder.Path));
                    continue; // Looked at again next time, from the same point.
                }

                foreach (var path in changed.Count > SweepAtMost ? [folder.Path] : changed)
                {
                    await queue.EnqueueAsync(new LibraryScanRequest(path, $"{reason}: changed since the last scan"), ct);
                }

                _swept[folder.Path] = now;
            }
        }
    }

    /// <summary>
    /// Drops duplicates and paths already covered by a broader request in the same batch. A path counts as deleted
    /// only if every request for it says so.
    /// </summary>
    public static IEnumerable<LibraryScanRequest> Coalesce(IEnumerable<LibraryScanRequest> requests)
    {
        var all = requests.ToList();
        var distinct = all.GroupBy(r => PathComparison.Key(r.Path))
            .Select(g => g.First() with
            {
                ForceMetadata = g.Any(r => r.ForceMetadata),
                Deleted = g.All(r => r.Deleted),
                TmdbId = g.Select(r => r.TmdbId).FirstOrDefault(id => id is not null),
                TvdbId = g.Select(r => r.TvdbId).FirstOrDefault(id => id is not null),
            })
            .ToList();
        return distinct
            .Where(r => !distinct.Any(o => o.Path != r.Path && PathComparison.IsUnder(r.Path, o.Path)))
            .Select(r => r with { ForceMetadata = all.Any(o => o.ForceMetadata && PathComparison.IsUnder(o.Path, r.Path)) });
    }

    private async Task ScanAsync(LibraryScanRequest request, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();
        var metadata = scope.ServiceProvider.GetRequiredService<MetadataRefresher>();

        var libraries = await db.Libraries.Include(l => l.Folders).ToListAsync(ct);
        var targets = new List<(Library Library, string Scope)>();
        foreach (var library in libraries)
        {
            foreach (var root in library.Folders.Select(f => f.Path))
            {
                if (PathComparison.IsUnder(request.Path, root))
                {
                    targets.Add((library, request.Path));
                }
                else if (PathComparison.IsUnder(root, request.Path))
                {
                    targets.Add((library, root));
                }
            }
        }

        if (targets.Count == 0)
        {
            LogNoLibrary(PathComparison.Printable(request.Path), PathComparison.Printable(request.Reason));
            return;
        }

        foreach (var (library, target) in targets)
        {
            LogScanStarted(library.Name, PathComparison.Printable(target), PathComparison.Printable(request.Reason));
            var started = TimeProvider.System.GetTimestamp();
            // A metadata refresh also retries files ffprobe couldn't read.
            var result = await scanner.ScanAsync(library, target, request.ForceMetadata, request.Deleted, ct);
            var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
            LogScanFinished(library.Name, PathComparison.Printable(target), result.Added, result.Updated, result.Missing, result.Removed, result.Unchanged,
                result.Unmatched, result.ProbeFailures, seconds);
        }

        // The scans have saved everything. Their files and streams stay tracked otherwise, and every save of the refresh
        // below would check them all again: minutes, and hundreds of MB, on a big library. The libraries are only read
        // from here on.
        db.ChangeTracker.Clear();

        // A whole library was scanned (at startup, twice a day, after the connections change): first save any mapping its
        // titles make certain, so the refresh below can link them to Sonarr, Radarr or Lidarr.
        if (targets.Any(t => t.Library.Folders.Any(f => PathComparison.IsUnder(f.Path, t.Scope))))
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<PathMappingFinder>().ApplyCertainAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogMappingsFailed(ex);
            }
        }

        foreach (var library in targets.Select(t => t.Library).DistinctBy(l => l.Id))
        {
            // A scan of one folder (a webhook) also refreshes the films or series in it, so new episodes get their
            // titles straight away rather than when the series is next due. Titles merely out of date are left to
            // MetadataRefreshWorker, a few at a time.
            var folders = library.Folders.Select(f => f.Path).ToList();
            var scopes = targets.Where(t => t.Library.Id == library.Id).Select(t => t.Scope).ToList();
            var within = scopes.Any(s => folders.Any(f => PathComparison.IsUnder(f, s))) ? [] : scopes;

            var started = TimeProvider.System.GetTimestamp();
            var refreshed = await metadata.RefreshScannedAsync(library, request.ForceMetadata, within, request.TmdbId, request.TvdbId, ct);
            if (refreshed.FromArr + refreshed.FromLookup + refreshed.FromLocal + refreshed.Unmatched > 0)
            {
                var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
                LogMetadataRefreshed(library.Name, refreshed.FromArr, refreshed.FromLookup, refreshed.FromLocal, refreshed.Unmatched, seconds);
            }
        }

        // A film or show that came back under a new folder only has its TMDB/TVDB id once Radarr or Sonarr matched it.
        await WatchHistoryCarryOver.CarryOverAsync(db, ct);
        await TraktHolds.ApplyAsync(db, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't work out path mappings before refreshing metadata")]
    private partial void LogMappingsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanning {Library}: {Path} ({Reason})")]
    private partial void LogScanStarted(string library, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned {Library}: {Path} — {Added} added, {Updated} updated, {Missing} newly missing, {Removed} removed, {Unchanged} unchanged, {Unmatched} unmatched, {Failures} probe failures in {Seconds:F1}s")]
    private partial void LogScanFinished(string library, string path, int added, int updated, int missing, int removed, int unchanged, int unmatched, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed metadata for {Library}: {FromArr} from *arr, {FromLookup} looked up, {FromLocal} from local files, {Unmatched} unmatched in {Seconds:F1}s")]
    private partial void LogMetadataRefreshed(string library, int fromArr, int fromLookup, int fromLocal, int unmatched, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): no library contains it")]
    private partial void LogNoLibrary(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): the path holds a control character")]
    private partial void LogUnusablePath(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't sort out a batch of {Count} scan requests; dropping it")]
    private partial void LogBatchFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of {Path} failed")]
    private partial void LogScanFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't look for what changed in {Path}")]
    private partial void LogSweepFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't queue the {Reason} scan of every library")]
    private partial void LogEnqueueFailed(Exception exception, string reason);
}
