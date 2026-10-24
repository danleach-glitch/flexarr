using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
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

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Catch up on anything that changed while the server was down (and finish interrupted scans).
        // Unchanged files are only stat'ed, so this is cheap after the first scan.
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            foreach (var folder in await db.LibraryFolders.Select(f => f.Path).ToListAsync(stoppingToken))
            {
                await queue.EnqueueAsync(new LibraryScanRequest(folder, "Startup"), stoppingToken);
            }
        }

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

    /// <summary>Drops duplicates and paths already covered by a broader request in the same batch.</summary>
    public static IEnumerable<LibraryScanRequest> Coalesce(IEnumerable<LibraryScanRequest> requests)
    {
        var distinct = requests.DistinctBy(r => r.Path.TrimEnd('/')).ToList();
        return distinct.Where(r => !distinct.Any(o => !ReferenceEquals(o, r) && o.Path != r.Path && PathComparison.IsUnder(r.Path, o.Path)));
    }

    private async Task ScanAsync(LibraryScanRequest request, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var scanner = scope.ServiceProvider.GetRequiredService<LibraryScanner>();

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
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanning {Library}: {Path} ({Reason})")]
    private partial void LogScanStarted(string library, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned {Library}: {Path} — {Added} added, {Updated} updated, {Removed} removed, {Unchanged} unchanged, {Unmatched} unmatched, {Failures} probe failures in {Seconds:F1}s")]
    private partial void LogScanFinished(string library, string path, int added, int updated, int removed, int unchanged, int unmatched, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): no library contains it")]
    private partial void LogNoLibrary(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of {Path} failed")]
    private partial void LogScanFailed(Exception exception, string path);
}
