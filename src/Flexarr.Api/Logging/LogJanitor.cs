using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Logging;

/// <summary>
/// Keeps the log folder within the retention settings: files older than the limit go, then the oldest until it fits
/// the size cap. Runs at startup, hourly, and when the settings change. The file being written is never deleted.
/// </summary>
public sealed partial class LogJanitor(LogPaths paths, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<LogJanitor> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var changed = new SemaphoreSlim(0);
        using var watch = options.OnChange(_ => changed.Release());
        while (!stoppingToken.IsCancellationRequested)
        {
            Tidy();
            try
            {
                await changed.WaitAsync(TimeSpan.FromHours(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    public void Tidy()
    {
        var settings = options.CurrentValue.Logs;
        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-Math.Max(1, settings.RetentionDays));
        var logs = paths.LogFiles();
        var current = logs.Count > 0 ? logs[0] : null;
        var uploads = new DirectoryInfo(paths.UploadsDirectory);
        var limit = Math.Max(10, settings.MaxSizeMb) * 1024L * 1024;
        // The server's log and the apps' diagnostics are kept apart, so diagnostics can never push out the server's
        // own record; they have their own budget besides (ClientLogs.UploadsBudgetBytes).
        var removed = Trim([.. logs.Skip(1).OrderBy(f => f.LastWriteTimeUtc)], (current?.Length ?? 0), limit, cutoff)
            + Trim(uploads.Exists ? [.. uploads.EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc)] : [], 0, ClientLogs.UploadsBudgetBytes, cutoff);

        if (removed > 0)
        {
            LogTidied(removed);
        }
    }

    /// <summary>Deletes the oldest of <paramref name="files"/> while any is past <paramref name="cutoff"/> or they're over <paramref name="limit"/>.</summary>
    private int Trim(List<FileInfo> files, long alsoKept, long limit, DateTime cutoff)
    {
        var total = files.Sum(f => f.Length) + alsoKept;
        var removed = 0;
        foreach (var file in files)
        {
            if (file.LastWriteTimeUtc >= cutoff && total <= limit)
            {
                break;
            }

            try
            {
                file.Delete();
                total -= file.Length;
                removed++;
            }
            catch (IOException ex)
            {
                LogDeleteFailed(file.Name, ex);
            }
            catch (UnauthorizedAccessException ex)
            {
                LogDeleteFailed(file.Name, ex);
            }
        }

        return removed;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Count} old log files")]
    private partial void LogTidied(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete old log file {File}")]
    private partial void LogDeleteFailed(string file, Exception exception);
}
