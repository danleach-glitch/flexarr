using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>
/// Keeps the cache folders from growing for ever: scrubbing previews of files no longer in the library, subtitles,
/// keyframe indexes and channel logos not made again for a long while (their names include the file's path and time, so
/// every replaced file leaves its old ones behind), Live TV folders a crash left, and prepared downloads whose download
/// has gone (deleted with its profile or title). Shortly after starting, then daily.
/// </summary>
public sealed partial class CacheJanitor(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, LiveStreams live,
    ILogger<CacheJanitor> logger) : BackgroundService
{
    /// <summary>A cached subtitle, keyframe index or logo older than this is deleted; anything still wanted is made again.</summary>
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(90);

    /// <summary>Half-made scrubbing previews older than this were left by a run that stopped.</summary>
    private static readonly TimeSpan PartialKeptFor = TimeSpan.FromDays(1);

    /// <summary>A file in the downloads folder this new is left alone: its download may be being saved as it's made.</summary>
    internal static readonly TimeSpan DownloadGrace = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Not while the server is busy starting.
            await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromDays(1));
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var cache = options.CurrentValue.ResolvedCacheDirectory;
        var deleted = 0;

        // Live TV: a folder per channel being watched; any other was left by a crash.
        var watching = live.All.Select(s => Path.GetFullPath(s.Directory)).ToHashSet(StringComparer.Ordinal);
        foreach (var folder in Folders(Path.Combine(cache, "live")).Where(f => !watching.Contains(Path.GetFullPath(f))))
        {
            deleted += Delete(folder);
        }

        // Scrubbing previews (and the note that they couldn't be made) are by file: gone with the file.
        var trickplay = Path.Combine(cache, "trickplay");
        if (Directory.Exists(trickplay))
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var files = (await db.MediaFiles.IgnoreQueryFilters().AsNoTracking().Select(f => f.Id).ToListAsync(ct)).ToHashSet();
            foreach (var entry in Directory.EnumerateFileSystemEntries(trickplay))
            {
                var name = Path.GetFileName(entry);
                var known = name.Length >= 32 && Guid.TryParseExact(name[..32], "N", out var id) && files.Contains(id);
                var abandoned = name.EndsWith(".partial", StringComparison.Ordinal) && DateTime.UtcNow - Directory.GetLastWriteTimeUtc(entry) > PartialKeptFor;
                if (!known || abandoned)
                {
                    deleted += Delete(entry);
                }
            }
        }

        // Prepared downloads: a file no download points at (nor one queued or being made, whose part file it may be) is
        // left over. Their rows go with a deleted profile or title, and the files would take the space for good.
        var downloads = Path.Combine(cache, "downloads");
        if (Directory.Exists(downloads))
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var rows = await db.Downloads.IgnoreQueryFilters().AsNoTracking()
                .Where(d => d.Status == DownloadStatus.Queued || d.Status == DownloadStatus.Preparing || d.FilePath != null)
                .Select(d => new { d.Id, d.Status, d.FilePath }).ToListAsync(ct);
            var underway = rows.Where(d => d.Status is DownloadStatus.Queued or DownloadStatus.Preparing).Select(d => d.Id).ToHashSet();
            var kept = rows.Where(d => d.FilePath != null).Select(d => Path.GetFileName(d.FilePath!)).ToHashSet(StringComparer.Ordinal);
            foreach (var file in Directory.EnumerateFiles(downloads))
            {
                var name = Path.GetFileName(file);
                var wanted = kept.Contains(name) || (name.Length >= 32 && Guid.TryParseExact(name[..32], "N", out var id) && underway.Contains(id));
                if (!wanted && DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > DownloadGrace)
                {
                    deleted += Delete(file);
                }
            }
        }

        // Keyed by the file's path and time (or a logo's address): an old one is for a file that's since changed, or
        // is made again in moments.
        foreach (var folder in new[] { "subtitles", "remux-index", "livetv-logos" })
        {
            var root = Path.Combine(cache, folder);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(root, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            {
                if (DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > KeptFor)
                {
                    deleted += Delete(file);
                }
            }
        }

        if (deleted > 0)
        {
            LogTidied(deleted);
        }
    }

    private static IEnumerable<string> Folders(string root) => Directory.Exists(root) ? Directory.EnumerateDirectories(root) : [];

    /// <summary>Deletes a file or folder; 1 if it went, 0 if it couldn't be (in use: the next round tries again).</summary>
    private int Delete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                File.Delete(path);
            }

            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogNotDeleted(path, ex.Message);
            return 0;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Tidied the cache: deleted {Count} old entries")]
    private partial void LogTidied(int count);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Couldn't delete {Path} from the cache: {Reason}")]
    private partial void LogNotDeleted(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tidying the cache failed")]
    private partial void LogFailed(Exception exception);
}
