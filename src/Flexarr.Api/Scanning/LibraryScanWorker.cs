using Flexarr.Core.Scanning;

namespace Flexarr.Api.Scanning;

/// <summary>Drains the scan queue. The actual scanner (probe, match, metadata) lands here next.</summary>
public sealed partial class LibraryScanWorker(ILibraryScanQueue queue, ILogger<LibraryScanWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in queue.ReadAllAsync(stoppingToken))
        {
            LogScanRequested(request.Path, request.Reason);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scan requested for {Path} ({Reason})")]
    private partial void LogScanRequested(string path, string reason);
}
