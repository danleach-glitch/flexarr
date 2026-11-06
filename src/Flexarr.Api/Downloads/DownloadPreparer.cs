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
/// converted, so it never slows a stream down. Films and episodes become MP4s, tracks stay as they are or become M4As.
/// Downloads whose file is sent as it is have nothing to wait for: they're made ready together, on a clock of their own.
/// Downloads not fetched within a week are deleted.
/// </summary>
public sealed partial class DownloadPreparer(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, TranscodeManager transcodes,
    ILogger<DownloadPreparer> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Queued downloads already found to need converting or fetching (or to have no file): they wait their turn in
    /// <see cref="PrepareNextAsync"/>, and <see cref="SendAsIsAsync"/> doesn't look at them again every round.
    /// </summary>
    private readonly HashSet<Guid> _needTheirTurn = [];

    /// <summary>Where prepared downloads are kept until they're fetched.</summary>
    public static string Folder(FlexarrOptions options) => Path.Combine(options.ResolvedCacheDirectory, "downloads");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RecoverAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            // Escaping ExecuteAsync stops the whole server; a folder it can't tidy mustn't.
            LogRecoverFailed(ex);
        }

        // Two clocks: a round of RunOnceAsync lasts as long as the film it converts, and the downloads that need no
        // converting mustn't wait behind it.
        await Task.WhenAll(EveryTickAsync(RunOnceAsync, stoppingToken), EveryTickAsync(SendAsIsAsync, stoppingToken));
    }

    private async Task EveryTickAsync(Func<CancellationToken, Task> round, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await round(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
    }

    /// <summary>
    /// Makes ready every queued download whose file is sent as it is (a track or an MP4 the phone plays): there's
    /// nothing to wait for, so they neither queue behind a conversion nor stop for someone watching. An album or a
    /// playlist is one download per track, and one a round kept a 300-track playlist at Getting ready for 15 minutes.
    /// </summary>
    internal async Task SendAsIsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var queued = await db.Downloads.IgnoreQueryFilters().Where(d => d.Status == DownloadStatus.Queued).OrderBy(d => d.CreatedAt)
            .Select(d => new { d.Id, d.MediaItemId, d.Quality }).ToListAsync(ct);
        _needTheirTurn.IntersectWith(queued.Select(d => d.Id));
        foreach (var download in queued.Where(d => !_needTheirTurn.Contains(d.Id)))
        {
            var file = await BestFileAsync(db, download.MediaItemId, ct);
            if (file is not null && DownloadPlanner.Plan(file, download.Quality, Subtitles.For(file)) is { SendAsIs: true })
            {
                await SentAsItIsAsync(db, download.Id, file, ct);
            }
            else
            {
                _needTheirTurn.Add(download.Id);
            }
        }
    }

    /// <summary>The file a download is made from: the title's best one that can be read.</summary>
    private static Task<MediaFile?> BestFileAsync(FlexarrDbContext db, Guid itemId, CancellationToken ct) =>
        db.MediaFiles.AsNoTracking().Include(f => f.Streams)
            .Where(f => f.MediaItemId == itemId && f.ProbeError == null && f.MissingSince == null)
            .OrderByDescending(f => f.Height).ThenByDescending(f => f.Bitrate).FirstOrDefaultAsync(ct);

    /// <summary>
    /// A queued download is ready as the library's own file. Only while it's still queued: both clocks can reach the
    /// same download, and it may have been deleted (or already made ready, fetched and received) meanwhile.
    /// </summary>
    private static Task<int> SentAsItIsAsync(FlexarrDbContext db, Guid id, MediaFile file, CancellationToken ct) =>
        db.Downloads.IgnoreQueryFilters().Where(d => d.Id == id && d.Status == DownloadStatus.Queued).ExecuteUpdateAsync(u => u
            .SetProperty(d => d.FilePath, file.Path)
            .SetProperty(d => d.IsLibraryFile, true)
            .SetProperty(d => d.SizeBytes, file.Size)
            .SetProperty(d => d.Progress, 1)
            .SetProperty(d => d.Status, DownloadStatus.Ready)
            .SetProperty(d => d.ReadyAt, DateTime.UtcNow), ct);

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
            DeletePartials(folder, recurse: false);
        }

        // Podcast episodes half fetched into the Podcasts library's folders.
        var podcastFolders = await db.Libraries.IgnoreQueryFilters().Where(l => l.Type == Core.Libraries.LibraryType.Podcasts)
            .SelectMany(l => l.Folders).Select(f => f.Path).ToListAsync(ct);
        foreach (var root in podcastFolders.Where(Directory.Exists))
        {
            DeletePartials(root, recurse: true);
        }
    }

    /// <summary>Deletes leftover <c>*.part.*</c> files, skipping folders it can't read and files it can't delete.</summary>
    internal void DeletePartials(string folder, bool recurse)
    {
        var enumeration = new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true };
        foreach (var partial in Directory.EnumerateFiles(folder, "*.part.*", enumeration))
        {
            try
            {
                File.Delete(partial);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogPartialNotDeleted(partial, ex.Message);
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

        var file = await BestFileAsync(db, download.MediaItemId, ct);
        if (file is null && await db.PodcastEpisodes.AsNoTracking().FirstOrDefaultAsync(e => e.MediaItemId == download.MediaItemId, ct) is { } enclosure)
        {
            await PreparePodcastAsync(scope.ServiceProvider, db, download, enclosure, ct);
            return;
        }

        var plan = file is null ? null : DownloadPlanner.Plan(file, download.Quality, Subtitles.For(file));
        if (file is null || plan is null)
        {
            download.Status = DownloadStatus.Failed;
            download.Error = file is null
                ? "The file for this title can't be read."
                : file.Streams.All(s => s.Type != StreamType.Video)
                    ? "This track's file has no sound in it that can be played."
                    : "This file can't be kept on a phone as it is. Choose another size.";
            await db.SaveChangesAsync(ct);
            return;
        }

        if (plan.SendAsIs)
        {
            await SentAsItIsAsync(db, download.Id, file, ct);
            return;
        }

        download.Status = DownloadStatus.Preparing;
        download.Progress = 0;
        await db.SaveChangesAsync(ct);
        LogPreparing(download.Id, download.Quality.ToString());

        var folder = Folder(options.CurrentValue);
        var extension = plan.IsAudioOnly ? "m4a" : "mp4";
        var partial = Path.Combine(folder, $"{download.Id:N}.part.{extension}");
        try
        {
            await ConvertAsync(db, download, file, plan, folder, partial, extension, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // A missing ffmpeg, a busy database, a file that won't move: the download says it failed (it can be tried
            // again) rather than sitting at "Preparing 0%" with its partial file until the server restarts.
            LogPrepareFailed(ex, download.Id);
            TryDelete(partial);
            await MarkFailedAsync(download.Id, CancellationToken.None);
        }
    }

    private async Task ConvertAsync(FlexarrDbContext db, Download download, MediaFile file, DownloadPlan plan, string folder, string partial, string extension,
        CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var finished = Path.Combine(folder, $"{download.Id:N}.{extension}");
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        IReadOnlyList<string> Args(TranscodeSettings current) => plan.IsAudioOnly
            ? FfmpegCommand.M4a(file.Path, plan.AudioIndex ?? 0, plan.Convert?.AudioChannels ?? 2, partial)
            : FfmpegCommand.Mp4(file.Path, plan.Convert, plan.VideoIndex, plan.AudioIndex, plan.VideoCodec, plan.Subtitles, partial, current);

        // Someone starting to watch takes precedence: the conversion stops and goes back in the queue.
        var error = await RunAsync(settings.FfmpegPath, Args(settings), file.DurationSeconds ?? 0, download.Id, ct, yieldTo: () => transcodes.ActiveCount > 0);

        // The hardware encoder failed (busy, out of sessions, a codec it won't take): once more in software, as streams do.
        if (error is not null && !ReferenceEquals(error, Yielded) && !plan.IsAudioOnly && plan.Convert is { VideoStreamIndex: >= 0 }
            && settings is { SoftwareFallback: true } && settings.Resolved != HardwareAcceleration.None
            && await db.Downloads.IgnoreQueryFilters().AnyAsync(d => d.Id == download.Id, ct))
        {
            LogSoftwareFallback(download.Id, settings.Resolved.ToString());
            TryDelete(partial);
            settings = settings with { HardwareAcceleration = HardwareAcceleration.None };
            error = await RunAsync(settings.FfmpegPath, Args(settings), file.DurationSeconds ?? 0, download.Id, ct, yieldTo: () => transcodes.ActiveCount > 0);
        }

        if (ReferenceEquals(error, Yielded))
        {
            File.Delete(partial);
            await db.Entry(download).ReloadAsync(ct);
            if (db.Entry(download).State != EntityState.Detached)
            {
                download.Status = DownloadStatus.Queued;
                download.Progress = 0;
                await db.SaveChangesAsync(ct);
            }

            LogYielded(download.Id);
            return;
        }

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

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Swept up as a leftover partial at the next start.
        }
    }

    /// <summary>Marks a download failed in a fresh context (the one it was being prepared in may be what broke).</summary>
    private async Task MarkFailedAsync(Guid id, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.Downloads.IgnoreQueryFilters().Where(d => d.Id == id && d.Status == DownloadStatus.Preparing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DownloadStatus.Failed)
                    .SetProperty(d => d.Error, "Converting this file didn't work."), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Still at Preparing: the next start puts it back in the queue.
            LogPrepareFailed(ex, id);
        }
    }

    /// <summary>Podcast episodes the phone plays as they come; anything else (Ogg, Opus) becomes an M4A.</summary>
    private static readonly HashSet<string> PhonePodcastFiles = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".aac", ".mp4" };

    /// <summary>The longest episode file fetched for a phone (some are hours of uncompressed sound).</summary>
    private const long MaxPodcastBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// A podcast episode isn't on the server: it's fetched from its host into the Podcasts library's folder (or the
    /// downloads folder when there's no library folder to hand), for the phone to take and the server to then delete.
    /// </summary>
    private async Task PreparePodcastAsync(IServiceProvider services, FlexarrDbContext db, Download download, Core.Podcasts.PodcastEpisode enclosure, CancellationToken ct)
    {
        download.Status = DownloadStatus.Preparing;
        download.Progress = 0;
        await db.SaveChangesAsync(ct);
        LogPreparing(download.Id, "podcast");

        var show = await db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == enclosure.FeedId).Select(i => i.Title).FirstOrDefaultAsync(ct) ?? "Podcast";
        var root = await db.Libraries.IgnoreQueryFilters().Where(l => l.Type == Core.Libraries.LibraryType.Podcasts).OrderBy(l => l.CreatedAt)
            .SelectMany(l => l.Folders).Select(f => f.Path).FirstOrDefaultAsync(ct);
        var folder = root is not null && Directory.Exists(root) ? Path.Combine(root, SafeName(show)) : Folder(options.CurrentValue);
        Directory.CreateDirectory(folder);

        var extension = Path.GetExtension(new Uri(enclosure.AudioUrl).AbsolutePath).ToLowerInvariant() is { Length: > 1 and <= 5 } fromUrl ? fromUrl
            : enclosure.MimeType is "audio/mp4" or "audio/x-m4a" or "audio/aac" ? ".m4a" : ".mp3";
        var partial = Path.Combine(folder, $"{download.Id:N}.part{extension}");
        var error = await FetchAsync(services.GetRequiredService<Podcasts.PodcastFetcher>(), enclosure, partial, download.Id, ct);

        string? finished = null;
        if (error is null && PhonePodcastFiles.Contains(extension))
        {
            finished = Path.Combine(folder, $"{download.Id:N}{extension}");
            File.Move(partial, finished, overwrite: true);
        }
        else if (error is null)
        {
            finished = Path.Combine(folder, $"{download.Id:N}.m4a");
            var converting = Path.Combine(folder, $"{download.Id:N}.part.m4a");
            var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
            error = await RunAsync(settings.FfmpegPath, FfmpegCommand.M4a(partial, 0, 2, converting), enclosure.DurationSeconds ?? 0, download.Id, ct);
            File.Delete(partial);
            if (error is null)
            {
                File.Move(converting, finished, overwrite: true);
            }
            else
            {
                File.Delete(converting);
            }
        }
        else
        {
            File.Delete(partial);
        }

        await db.Entry(download).ReloadAsync(ct);
        if (error is null && finished is not null && db.Entry(download).State != EntityState.Detached)
        {
            download.FilePath = finished;
            download.IsLibraryFile = false;
            download.SizeBytes = new FileInfo(finished).Length;
            download.Progress = 1;
            download.Status = DownloadStatus.Ready;
            download.ReadyAt = DateTime.UtcNow;
        }
        else
        {
            if (finished is not null && error is null)
            {
                File.Delete(finished); // deleted while it was being fetched
            }

            download.Status = DownloadStatus.Failed;
            download.Error = error ?? "The episode couldn't be fetched.";
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fetches an episode's file from its host, noting progress; null when it worked, else why not.</summary>
    private async Task<string?> FetchAsync(Podcasts.PodcastFetcher fetcher, Core.Podcasts.PodcastEpisode enclosure, string path, Guid id, CancellationToken ct)
    {
        try
        {
            var (response, _, _) = await fetcher.GetAsync(new Uri(enclosure.AudioUrl), null, ct);
            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    return $"The podcast's host answered {(int)response.StatusCode}.";
                }

                var total = response.Content.Headers.ContentLength ?? enclosure.LengthBytes;
                if (total > MaxPodcastBytes)
                {
                    return "This episode's file is too big to keep on a phone.";
                }

                await using var body = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                var lastSaved = DateTime.UtcNow;
                int read;
                while ((read = await body.ReadAsync(buffer, ct)) > 0)
                {
                    done += read;
                    if (done > MaxPodcastBytes)
                    {
                        return "This episode's file is too big to keep on a phone.";
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    if (total > 0 && DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(2))
                    {
                        lastSaved = DateTime.UtcNow;
                        if (!await SaveProgressAsync(id, Math.Clamp((double)done / total.Value, 0, 0.99), ct))
                        {
                            return "Deleted while it was being fetched.";
                        }
                    }
                }

                return done == 0 ? "The podcast's host sent an empty file." : null;
            }
        }
        catch (HttpRequestException ex)
        {
            LogPodcastFetchFailed(id, ex.Message);
            return "The podcast's host couldn't be reached.";
        }
        catch (IOException ex)
        {
            LogPodcastFetchFailed(id, ex.Message);
            return "The episode couldn't be saved on the server.";
        }
    }

    /// <summary>A show's title as a folder name: without characters file systems refuse, and never empty.</summary>
    internal static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '/', '\\', '?', '*', '"', '<', '>', '|']).ToHashSet();
        var name = new string([.. title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c)]).Trim().Trim('.');
        return name.Length == 0 ? "Podcast" : name.Length > 100 ? name[..100].Trim() : name;
    }

    /// <summary>What <see cref="RunAsync"/> gives back when it stopped to make way for someone watching.</summary>
    private static readonly string Yielded = new("yielded".ToCharArray());

    /// <summary>
    /// Runs ffmpeg, noting its progress; null when it worked, else what went wrong, or <see cref="Yielded"/> when
    /// <paramref name="yieldTo"/> said to stop for someone else.
    /// </summary>
    private async Task<string?> RunAsync(string ffmpeg, IReadOnlyList<string> args, double duration, Guid id, CancellationToken ct, Func<bool>? yieldTo = null)
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
                if (yieldTo?.Invoke() == true)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct);
                    return Yielded;
                }

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
        catch
        {
            // Stopped, or something else went wrong (a database that's busy, say): ffmpeg mustn't carry on alone.
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Finished meanwhile.
            }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} stopped to make way for someone watching; it starts again when they're done")]
    private partial void LogYielded(Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: the {Encoder} encoder failed; converting in software instead")]
    private partial void LogSoftwareFallback(Guid id, string encoder);

    [LoggerMessage(Level = LogLevel.Error, Message = "Preparing download {Id} failed")]
    private partial void LogPrepareFailed(Exception exception, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing download {Id} ({Quality})")]
    private partial void LogPreparing(Guid id, string quality);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Converting download {Id} failed: {Errors}")]
    private partial void LogFfmpegFailed(Guid id, string errors);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Fetching podcast episode for download {Id} failed: {Error}")]
    private partial void LogPodcastFetchFailed(Guid id, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't delete the unfinished download {Path}: {Error}")]
    private partial void LogPartialNotDeleted(string path, string error);

    [LoggerMessage(Level = LogLevel.Error, Message = "Tidying up downloads the server stopped in the middle of failed")]
    private partial void LogRecoverFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Preparing downloads failed")]
    private partial void LogFailed(Exception exception);
}
