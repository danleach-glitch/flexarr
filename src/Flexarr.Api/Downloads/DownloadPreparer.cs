using System.Diagnostics;
using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Downloads;

/// <summary>
/// Gets downloads ready, one at a time and at low priority: it waits while anyone is watching something that's being
/// converted, so it never slows a stream down. Downloads not fetched within a week are deleted.
/// </summary>
public sealed partial class DownloadPreparer(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, TranscodeManager transcodes,
    ILogger<DownloadPreparer> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>Where prepared downloads are kept until they're fetched.</summary>
    public static string Folder(FlexarrOptions options) => Path.Combine(options.ResolvedCacheDirectory, "downloads");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RecoverAsync(stoppingToken);
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
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
    }

    /// <summary>One round: expire what's been waiting too long, then prepare the next download unless someone's watching.</summary>
    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await ExpireAsync(ct);
        // Someone's watching something being converted: they come first.
        if (transcodes.ActiveCount == 0)
        {
            await PrepareNextAsync(ct);
        }
    }

    /// <summary>A preparation the server stopped in the middle of (a restart) starts again, from scratch.</summary>
    private async Task RecoverAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        await db.Downloads.Where(d => d.Status == DownloadStatus.Preparing)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Status, DownloadStatus.Queued).SetProperty(d => d.Progress, 0), ct);
        var folder = Folder(options.CurrentValue);
        if (Directory.Exists(folder))
        {
            foreach (var partial in Directory.EnumerateFiles(folder, "*.part.mp4"))
            {
                File.Delete(partial);
            }
        }
    }

    private async Task ExpireAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var before = DateTime.UtcNow - KeptFor;
        var stale = await db.Downloads.IgnoreQueryFilters().Where(d => d.Status == DownloadStatus.Ready && d.ReadyAt < before).ToListAsync(ct);
        foreach (var download in stale)
        {
            DeleteFile(download);
            download.Status = DownloadStatus.Expired;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Deletes the server's copy, unless it's the library's own file.</summary>
    public static void DeleteFile(Download download)
    {
        ArgumentNullException.ThrowIfNull(download);
        if (download is { IsLibraryFile: false, FilePath: { } path })
        {
            File.Delete(path);
        }

        download.FilePath = null;
    }

    private async Task PrepareNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var download = await db.Downloads.IgnoreQueryFilters().Where(d => d.Status == DownloadStatus.Queued).OrderBy(d => d.CreatedAt).FirstOrDefaultAsync(ct);
        if (download is null)
        {
            return;
        }

        var file = await db.MediaFiles.AsNoTracking().Include(f => f.Streams)
            .Where(f => f.MediaItemId == download.MediaItemId && f.ProbeError == null && f.MissingSince == null)
            .OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefaultAsync(ct);
        var plan = file is null ? null : DownloadPlanner.Plan(file, download.Quality, Subtitles.For(file));
        if (file is null || plan is null)
        {
            download.Status = DownloadStatus.Failed;
            download.Error = file is null
                ? "The file for this title can't be read."
                : "This file can't be kept on a phone as it is. Choose another size.";
            await db.SaveChangesAsync(ct);
            return;
        }

        if (plan.SendAsIs)
        {
            download.FilePath = file.Path;
            download.IsLibraryFile = true;
            download.SizeBytes = file.Size;
            download.Progress = 1;
            download.Status = DownloadStatus.Ready;
            download.ReadyAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }

        download.Status = DownloadStatus.Preparing;
        download.Progress = 0;
        await db.SaveChangesAsync(ct);
        LogPreparing(download.Id, download.Quality.ToString());

        var folder = Folder(options.CurrentValue);
        Directory.CreateDirectory(folder);
        var partial = Path.Combine(folder, $"{download.Id:N}.part.mp4");
        var finished = Path.Combine(folder, $"{download.Id:N}.mp4");
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        var args = FfmpegCommand.Mp4(file.Path, plan.Convert, plan.VideoIndex, plan.AudioIndex, plan.VideoCodec, plan.Subtitles, partial, settings);
        var error = await RunAsync(settings.FfmpegPath, args, file.DurationSeconds ?? 0, download.Id, ct);

        // Deleted while it was being made? Then there's nothing to keep.
        await db.Entry(download).ReloadAsync(ct);
        if (error is null && db.Entry(download).State != EntityState.Detached && File.Exists(partial))
        {
            File.Move(partial, finished, overwrite: true);
            download.FilePath = finished;
            download.SizeBytes = new FileInfo(finished).Length;
            download.Progress = 1;
            download.Status = DownloadStatus.Ready;
            download.ReadyAt = DateTime.UtcNow;
        }
        else
        {
            File.Delete(partial);
            download.Status = DownloadStatus.Failed;
            download.Error = error ?? "Converting this file didn't work.";
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Runs ffmpeg, noting its progress; null when it worked, else what went wrong.</summary>
    private async Task<string?> RunAsync(string ffmpeg, IReadOnlyList<string> args, double duration, Guid id, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        try
        {
            // Below anyone watching.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or PlatformNotSupportedException)
        {
        }

        var errors = process.StandardError.ReadToEndAsync(ct);
        var lastSaved = DateTime.MinValue;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("out_time_us=", StringComparison.Ordinal) || duration <= 0
                    || !long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros)
                    || DateTime.UtcNow - lastSaved < TimeSpan.FromSeconds(3))
                {
                    continue;
                }

                lastSaved = DateTime.UtcNow;
                if (!await SaveProgressAsync(id, Math.Clamp(micros / 1_000_000.0 / duration, 0, 0.99), ct))
                {
                    process.Kill(entireProcessTree: true); // deleted meanwhile
                    break;
                }
            }

            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        if (process.ExitCode == 0)
        {
            return null;
        }

        LogFfmpegFailed(id, (await errors).Trim());
        return "Converting this file didn't work.";
    }

    /// <summary>Saves how far it's got; false if the download has been deleted.</summary>
    private async Task<bool> SaveProgressAsync(Guid id, double progress, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        return await db.Downloads.IgnoreQueryFilters().Where(d => d.Id == id).ExecuteUpdateAsync(u => u.SetProperty(d => d.Progress, progress), ct) > 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing download {Id} ({Quality})")]
    private partial void LogPreparing(Guid id, string quality);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Converting download {Id} failed: {Errors}")]
    private partial void LogFfmpegFailed(Guid id, string errors);

    [LoggerMessage(Level = LogLevel.Error, Message = "Preparing downloads failed")]
    private partial void LogFailed(Exception exception);
}
