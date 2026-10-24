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
    ILogger<LibraryScanWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScheduledScanInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Catch up on anything that changed while the server was down (and finish interrupted scans).
        // Unchanged files are only stat'ed, so this is cheap after the first scan.
        await EnqueueAllAsync("Startup", stoppingToken);

        _ = ScheduleAsync(stoppingToken);

        await foreach (var first in queue.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(Debounce, stoppingToken);

            var batch = new List<LibraryScanRequest> { first };
            while (queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            foreach (var request in Coalesce(batch))
            {
                try
                {
                    await ScanAsync(request, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogScanFailed(ex, request.Path);
                }
            }
        }
    }

    /// <summary>Picks up changes the *arr webhooks don't cover (files added by hand, other tools) and stale metadata.</summary>
    private async Task ScheduleAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ScheduledScanInterval);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await EnqueueAllAsync("Scheduled", ct);
        }
    }

    private async Task EnqueueAllAsync(string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        foreach (var folder in await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct))
        {
            await queue.EnqueueAsync(new LibraryScanRequest(folder, reason), ct);
        }
    }

    /// <summary>Drops duplicates and paths already covered by a broader request in the same batch.</summary>
    public static IEnumerable<LibraryScanRequest> Coalesce(IEnumerable<LibraryScanRequest> requests)
    {
        var all = requests.ToList();
        var distinct = all.GroupBy(r => r.Path.TrimEnd('/'))
            .Select(g => g.First() with { ForceMetadata = g.Any(r => r.ForceMetadata) })
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
            LogNoLibrary(request.Path, request.Reason);
            return;
        }

        foreach (var (library, target) in targets)
        {
            LogScanStarted(library.Name, target, request.Reason);
            var started = TimeProvider.System.GetTimestamp();
            var result = await scanner.ScanAsync(library, target, ct);
            var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
            LogScanFinished(library.Name, target, result.Added, result.Updated, result.Removed, result.Unchanged,
                result.Unmatched, result.ProbeFailures, seconds);
        }

        foreach (var library in targets.Select(t => t.Library).DistinctBy(l => l.Id))
        {
            var started = TimeProvider.System.GetTimestamp();
            var refreshed = await metadata.RefreshLibraryAsync(library, request.ForceMetadata, ct);
            if (refreshed.FromArr + refreshed.FromLookup + refreshed.Unmatched > 0)
            {
                var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
                LogMetadataRefreshed(library.Name, refreshed.FromArr, refreshed.FromLookup, refreshed.Unmatched, seconds);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanning {Library}: {Path} ({Reason})")]
    private partial void LogScanStarted(string library, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned {Library}: {Path} — {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Unmatched} unmatched, {Failures} probe failures in {Seconds:F1}s")]
    private partial void LogScanFinished(string library, string path, int added, int updated, int removed, int unchanged, int unmatched, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed metadata for {Library}: {FromArr} from *arr, {FromLookup} looked up, {Unmatched} unmatched in {Seconds:F1}s")]
    private partial void LogMetadataRefreshed(string library, int fromArr, int fromLookup, int unmatched, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): no library contains it")]
    private partial void LogNoLibrary(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of {Path} failed")]
    private partial void LogScanFailed(Exception exception, string path);
}
