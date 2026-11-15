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
/// Downloads not fetched within a week are deleted. With nothing queued it asks the database nothing: queueing a download
/// wakes it, and it looks once an hour for downloads whose week is up.
/// </summary>
public sealed partial class DownloadPreparer(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, TranscodeManager transcodes,
    ILogger<DownloadPreparer> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(3);

    /// <summary>How often it looks with nothing queued: only to delete the downloads nobody fetched in their week.</summary>
    private static readonly TimeSpan IdleTick = TimeSpan.FromHours(1);

    private TaskCompletionSource _queued = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>A download was queued: both clocks look now, rather than when they next would.</summary>
    public void Wake() => Interlocked.Exchange(ref _queued, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
    public static readonly TimeSpan KeptFor = TimeSpan.FromDays(7);

    /// <summary>
    /// Queued downloads already found to need converting or fetching (or to have no file): they wait their turn in
    /// <see cref="PrepareNextAsync"/>, and <see cref="SendAsIsAsync"/> doesn't look at them again every round.
    /// </summary>
    private readonly HashSet<Guid> _needTheirTurn = [];

    /// <summary>Where prepared downloads are kept until they're fetched.</summary>
    public static string Folder(FlexarrOptions options) => Path.Combine(options.ResolvedCacheDirectory, "downloads");

    /// <summary>The room the downloads folder's drive keeps to spare once a copy is made.</summary>
    internal const long SpareBytes = 2L << 30;

    /// <summary>
    /// How long a podcast's host may go without sending anything before the fetch gives up: one host that accepts the
    /// connection and then says nothing would otherwise hold the preparing loop, and every conversion behind it, for good.
    /// </summary>
    internal TimeSpan PodcastStall { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>The longest an episode's fetch may take, however steadily a slow host trickles it out.</summary>
    internal TimeSpan PodcastFetchCap { get; set; } = TimeSpan.FromHours(1);

    /// <summary>How much room the drive holding a folder has (tests say).</summary>
    internal Func<string, long> FreeBytes { get; set; } = folder => new DriveInfo(folder).AvailableFreeSpace;

    /// <summary>
    /// Whether a copy of about <paramref name="bytes"/> would leave the downloads folder's drive without room to spare.
    /// Copies wait there up to a week for a device to fetch them, and anyone may ask for as many as they like, so this
    /// is what stops them filling the disk the server runs on. A drive that won't say how full it is isn't held back.
    /// </summary>
    private bool ShortOfSpace(string folder, long bytes)
    {
        try
        {
            Directory.CreateDirectory(folder);
            return FreeBytes(folder) - bytes < SpareBytes;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

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
        // A round of SendAsIsAsync leaves nothing for its next: what it didn't make ready waits for the other clock.
        await Task.WhenAll(EveryTickAsync(RunOnceAsync, stoppingToken), EveryTickAsync(async ct =>
        {
            await SendAsIsAsync(ct);
            return false;
        }, stoppingToken));
    }

    /// <param name="round">One look; true when downloads are still waiting, so it looks again in a few seconds.</param>
    private async Task EveryTickAsync(Func<CancellationToken, Task<bool>> round, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            // Taken before looking, so a download queued while this round looks isn't left until the next idle look.
            var queued = Volatile.Read(ref _queued).Task;
            var waiting = true;
            try
            {
                waiting = await round(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }

            if (waiting)
            {
                await Task.Delay(Tick, stoppingToken);
                continue;
            }

            try
            {
                await queued.WaitAsync(IdleTick, stoppingToken);
            }
            catch (TimeoutException)
            {
                // Nothing was queued this hour: time to look for downloads that have run out.
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
            .Select(d => new { d.Id, d.MediaItemId, d.Quality, d.ProfileId }).ToListAsync(ct);
        _needTheirTurn.IntersectWith(queued.Select(d => d.Id));
        foreach (var download in queued.Where(d => !_needTheirTurn.Contains(d.Id)))
        {
            var file = await DownloadSource.ForAsync(db, download.MediaItemId, download.Quality, download.ProfileId, ct);
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
    /// <returns>True when a download was queued (it may not be the last, or it's waiting for whoever's watching): another round is due.</returns>
    internal async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        await ExpireAsync(ct);
        // Someone's watching something whose picture is being converted: they come first (see MakesWay). A repackaged
        // stream (the picture copied as it is) or a song costs next to nothing, and a conversion needn't stop for it.
        if (!MakesWay(PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue)))
        {
            return await PrepareNextAsync(ct);
        }

        await using var scope = scopes.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Downloads.IgnoreQueryFilters().AnyAsync(d => d.Status == DownloadStatus.Queued, ct);
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

    /// <summary>Whose download is converted next (see <see cref="DownloadTurns"/>).</summary>
    private readonly DownloadTurns _turns = new();

    /// <returns>False when nothing was queued.</returns>
    private async Task<bool> PrepareNextAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var queued = (await db.Downloads.IgnoreQueryFilters().Where(d => d.Status == DownloadStatus.Queued)
            .Select(d => new
            {
                d.Id, d.ProfileId, d.CreatedAt, d.MediaItemId,
                Batch = d.MediaItem!.Kind == MediaKind.Episode ? d.MediaItem.Parent!.ParentId : d.MediaItem.Kind == MediaKind.Track ? d.MediaItem.ParentId : null,
            }).ToListAsync(ct))
            .Select(d => new DownloadTurns.Waiting(d.Id, d.ProfileId, d.Batch ?? d.MediaItemId, d.CreatedAt)).ToList();
        ForgetPiecesExcept(queued.Select(d => d.Id));
        if (_turns.Next(queued) is not { } next
            || await db.Downloads.IgnoreQueryFilters().FirstOrDefaultAsync(d => d.Id == next && d.Status == DownloadStatus.Queued, ct) is not { } download)
        {
            return queued.Count > 0; // taken meanwhile (made ready as it is, or deleted): the next round looks again
        }

        var file = await DownloadSource.ForAsync(db, download.MediaItemId, download.Quality, download.ProfileId, ct);
        if (file is null && await db.PodcastEpisodes.AsNoTracking().FirstOrDefaultAsync(e => e.MediaItemId == download.MediaItemId, ct) is { } enclosure)
        {
            try
            {
                await PreparePodcastAsync(scope.ServiceProvider, db, download, enclosure, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // A feed's address that isn't one, a folder it can't write to: the download says it failed rather than
                // sitting at "Getting ready" until a restart, which would only try it again. Its partials are swept at the next start.
                LogPrepareFailed(ex, download.Id);
                await MarkFailedAsync(download.Id, "The episode couldn't be fetched.", CancellationToken.None);
            }

            return true;
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
            return true;
        }

        if (plan.SendAsIs)
        {
            await SentAsItIsAsync(db, download.Id, file, ct);
            return true;
        }

        var folder = Folder(options.CurrentValue);
        if (ShortOfSpace(folder, DownloadPlanner.EstimateBytes(file, download.Quality)))
        {
            download.Status = DownloadStatus.Failed;
            download.Error = "The server is too short of space to get this ready. Try again later.";
            await db.SaveChangesAsync(ct);
            LogShortOfSpace(download.Id, folder);
            return true;
        }

        download.Status = DownloadStatus.Preparing;
        download.Progress = 0;
        await db.SaveChangesAsync(ct);
        LogPreparing(download.Id, download.Quality.ToString());

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
            await MarkFailedAsync(download.Id, "Converting this file didn't work.", CancellationToken.None);
        }

        return true;
    }

    private async Task ConvertAsync(FlexarrDbContext db, Download download, MediaFile file, DownloadPlan plan, string folder, string partial, string extension,
        CancellationToken ct)
    {
        Directory.CreateDirectory(folder);
        var finished = Path.Combine(folder, $"{download.Id:N}.{extension}");
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        var duration = file.DurationSeconds ?? 0;
        // A film's picture being converted is the long kind, and is made in pieces when it has to stop for someone:
        // the next turn carries on from where it got. Copies and sound alone are quick, and simply start again.
        var inPieces = !plan.IsAudioOnly && plan.Convert is { VideoStreamIndex: >= 0 } && duration > 0;
        IReadOnlyList<string> Args(TranscodeSettings current, string output, double start = 0) => plan.IsAudioOnly
            ? FfmpegCommand.M4a(file.Path, plan.AudioIndex ?? 0, plan.Convert?.AudioChannels ?? 2, output)
            : FfmpegCommand.Mp4(file.Path, plan.Convert, plan.VideoIndex, plan.AudioIndex, plan.VideoCodec, plan.Subtitles, output, current, start, stoppable: inPieces);

        // Pieces kept from an earlier turn go on only if this turn would make them the same way (same file, size and encoder).
        var kept = inPieces ? KeptPieces(download.Id, string.Join('\n', Args(settings, "-"))) : null;
        if (!inPieces)
        {
            ForgetPieces(download.Id);
        }

        var start = kept?.Seconds ?? 0;
        var output = kept is null ? partial : PiecePath(folder, download.Id, kept.Pieces.Count);

        // Someone starting to watch something converted takes precedence: the conversion stops and goes back in the queue.
        var error = await RunAsync(settings.FfmpegPath, Args(settings, output, start), duration, download.Id, ct, yieldTo: () => MakesWay(settings),
            startSeconds: start, stopsGently: inPieces);

        // The hardware encoder failed (busy, out of sessions, a codec it won't take): once more in software, as streams
        // do, and from the start, since pieces from two encoders don't join.
        if (error is not null && !ReferenceEquals(error, Yielded) && !ReferenceEquals(error, Paused) && !plan.IsAudioOnly && plan.Convert is { VideoStreamIndex: >= 0 }
            && settings is { SoftwareFallback: true } && settings.Resolved != HardwareAcceleration.None
            && await db.Downloads.IgnoreQueryFilters().AnyAsync(d => d.Id == download.Id, ct))
        {
            LogSoftwareFallback(download.Id, settings.Resolved.ToString());
            TryDelete(output);
            ForgetPieces(download.Id);
            kept = null;
            start = 0;
            output = partial;
            settings = settings with { HardwareAcceleration = HardwareAcceleration.None };
            error = await RunAsync(settings.FfmpegPath, Args(settings, output), duration, download.Id, ct, yieldTo: () => MakesWay(settings), stopsGently: inPieces);
        }

        if (ReferenceEquals(error, Paused) || ReferenceEquals(error, Yielded))
        {
            // Stopped cleanly, what it made is kept as a piece and the next turn starts where its picture ends; one
            // too short to bother with (or that can't be measured) is thrown away, and so is one ffmpeg was killed in
            // the middle of. Pieces from earlier turns stay either way.
            var pieces = kept?.Pieces ?? [];
            var piece = PiecePath(folder, download.Id, pieces.Count);
            if (ReferenceEquals(error, Paused) && output != piece)
            {
                File.Move(output, piece, overwrite: true);
            }

            if (ReferenceEquals(error, Paused) && await PictureSecondsAsync(piece, ct) is var seconds and > 1)
            {
                pieces = [.. pieces, new FfmpegCommand.Piece(piece, seconds)];
            }
            else
            {
                TryDelete(piece);
                TryDelete(output);
            }

            if (pieces.Count > 0)
            {
                _pieces[download.Id] = new KeptConversion(string.Join('\n', Args(settings, "-")), pieces);
            }

            await db.Entry(download).ReloadAsync(ct);
            if (StillPreparing(db, download))
            {
                download.Status = DownloadStatus.Queued;
                download.Progress = 0;
                await db.SaveChangesAsync(ct);
            }
            else
            {
                ForgetPieces(download.Id); // deleted meanwhile
            }

            if (pieces.Count > 0)
            {
                LogPaused(download.Id, Math.Round(pieces.Sum(p => p.Seconds)));
            }
            else
            {
                LogYielded(download.Id);
            }

            return;
        }

        // The last piece is done: the pieces become one file.
        if (error is null && kept is not null)
        {
            error = await JoinAsync(settings.FfmpegPath, folder, download.Id, [.. kept.Pieces, new FfmpegCommand.Piece(output, 0)], file.Path, plan.Subtitles, partial, ct);
        }

        ForgetPieces(download.Id);
        if (output != partial)
        {
            TryDelete(output);
        }

        // Deleted while it was being made? Then there's nothing to keep.
        await db.Entry(download).ReloadAsync(ct);
        if (error is null && StillPreparing(db, download) && File.Exists(partial))
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
            if (StillPreparing(db, download))
            {
                download.Status = DownloadStatus.Failed;
                download.Error = error ?? "Converting this file didn't work.";
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Whether a download just reloaded is still the one being prepared: not deleted, and not taken off the phone (a
    /// podcast episode's row stays, as Removed, and must stay so: it keeps auto-download from handing it back).
    /// </summary>
    private static bool StillPreparing(FlexarrDbContext db, Download download) =>
        db.Entry(download).State != EntityState.Detached && download.Status == DownloadStatus.Preparing;

    /// <summary>A conversion stopped part way for someone watching: the pieces made so far, and how they were made.</summary>
    private sealed record KeptConversion(string Made, IReadOnlyList<FfmpegCommand.Piece> Pieces)
    {
        /// <summary>Where in the film the next piece starts.</summary>
        public double Seconds => Pieces.Sum(p => p.Seconds);
    }

    /// <summary>
    /// Conversions waiting to carry on, by download. Only in memory: after a restart the pieces are swept up with the
    /// other partial files (<see cref="RecoverAsync"/>) and the conversion starts again, as it always did.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, KeptConversion> _pieces = new();

    /// <summary>The pieces kept for <paramref name="id"/>, if they were made as <paramref name="made"/> says; others are deleted.</summary>
    private KeptConversion? KeptPieces(Guid id, string made)
    {
        if (!_pieces.TryGetValue(id, out var kept))
        {
            return null;
        }

        if (kept.Made == made && kept.Pieces.All(p => File.Exists(p.Path)))
        {
            return kept;
        }

        ForgetPieces(id);
        return null;
    }

    /// <summary>Deletes the pieces kept for a download, if any.</summary>
    private void ForgetPieces(Guid id)
    {
        if (_pieces.TryRemove(id, out var kept))
        {
            foreach (var piece in kept.Pieces)
            {
                TryDelete(piece.Path);
            }
        }
    }

    /// <summary>Deletes the pieces of downloads no longer waiting (deleted, or taken off the phone, while queued).</summary>
    private void ForgetPiecesExcept(IEnumerable<Guid> queued)
    {
        var waiting = queued.ToHashSet();
        foreach (var id in _pieces.Keys.Where(id => !waiting.Contains(id)))
        {
            ForgetPieces(id);
        }
    }

    /// <summary>How many conversions have pieces waiting (tests).</summary>
    internal int KeptConversions => _pieces.Count;

    /// <summary>A piece's file: a partial, so a restart sweeps it up.</summary>
    private static string PiecePath(string folder, Guid id, int index) =>
        Path.Combine(folder, $"{id:N}.part.{index.ToString(CultureInfo.InvariantCulture)}.mp4");

    /// <summary>How long a piece's picture runs, by ffprobe; 0 when it can't say.</summary>
    private async Task<double> PictureSecondsAsync(string piece, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.CurrentValue.FfprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in (string[])["-v", "error", .. FfmpegCommand.LocalFile, "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", piece])
        {
            start.ArgumentList.Add(arg);
        }

        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            _ = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                process.Kill(entireProcessTree: true);
                return 0;
            }

            return process.ExitCode == 0 && double.TryParse((await output).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) && seconds > 0
                ? seconds
                : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0; // no ffprobe
        }
    }

    /// <summary>Joins a conversion's pieces into <paramref name="output"/>; null when it worked, else what went wrong.</summary>
    private async Task<string?> JoinAsync(string ffmpeg, string folder, Guid id, IReadOnlyList<FfmpegCommand.Piece> pieces, string input,
        IReadOnlyList<SubtitleTrack> subtitles, string output, CancellationToken ct)
    {
        // The join writes a second copy before the pieces go: it needs the room for one.
        if (ShortOfSpace(folder, pieces.Sum(p => File.Exists(p.Path) ? new FileInfo(p.Path).Length : 0)))
        {
            LogShortOfSpace(id, folder);
            return "The server is too short of space to get this ready. Try again later.";
        }

        var list = Path.Combine(folder, $"{id:N}.part.txt");
        await File.WriteAllTextAsync(list, FfmpegCommand.ConcatList(pieces), ct);
        try
        {
            return await RunAsync(ffmpeg, FfmpegCommand.JoinMp4(list, input, subtitles, output), 0, id, ct);
        }
        finally
        {
            TryDelete(list);
        }
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
    private async Task MarkFailedAsync(Guid id, string error, CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.Downloads.IgnoreQueryFilters().Where(d => d.Id == id && d.Status == DownloadStatus.Preparing)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(d => d.Status, DownloadStatus.Failed)
                    .SetProperty(d => d.Error, error), ct);
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
        // A fetched episode waits there for the phone as a converted copy does, so it's kept to the same room to spare.
        if (ShortOfSpace(folder, Math.Clamp(enclosure.LengthBytes ?? 0, 0, MaxPodcastBytes)))
        {
            download.Status = DownloadStatus.Failed;
            download.Error = "The server is too short of space to get this ready. Try again later.";
            await db.SaveChangesAsync(ct);
            LogShortOfSpace(download.Id, folder);
            return;
        }

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
            // The file is whatever a stranger's feed pointed at: read as a sound file, or not at all.
            error = await RunAsync(settings.FfmpegPath, FfmpegCommand.M4a(partial, 0, 2, converting, soundFileOnly: true), enclosure.DurationSeconds ?? 0, download.Id, ct);
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
        if (error is null && finished is not null && StillPreparing(db, download))
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

            // Taken off the phone meanwhile, it stays as that left it: removed (a podcast episode's), or gone.
            if (StillPreparing(db, download))
            {
                download.Status = DownloadStatus.Failed;
                download.Error = error ?? "The episode couldn't be fetched.";
            }
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Fetches an episode's file from its host, noting progress; null when it worked, else why not.</summary>
    private async Task<string?> FetchAsync(Podcasts.PodcastFetcher fetcher, Core.Podcasts.PodcastEpisode enclosure, string path, Guid id, CancellationToken ct)
    {
        // The stall clock starts again with every read; the overall cap doesn't.
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overall.CancelAfter(PodcastFetchCap);
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(overall.Token);
        stall.CancelAfter(PodcastStall);
        try
        {
            var (response, _, _) = await fetcher.GetAsync(new Uri(enclosure.AudioUrl), null, stall.Token);
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

                await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
                await using var output = File.Create(path);
                var buffer = new byte[81920];
                long done = 0;
                var lastSaved = DateTime.UtcNow;
                int read;
                while (true)
                {
                    stall.CancelAfter(PodcastStall);
                    if ((read = await body.ReadAsync(buffer, stall.Token)) <= 0)
                    {
                        break;
                    }

                    done += read;
                    if (done > MaxPodcastBytes)
                    {
                        return "This episode's file is too big to keep on a phone.";
                    }

                    await output.WriteAsync(buffer.AsMemory(0, read), ct);
                    // With no length to go by, still asked every few seconds: it may have been deleted meanwhile.
                    if (DateTime.UtcNow - lastSaved > TimeSpan.FromSeconds(2))
                    {
                        lastSaved = DateTime.UtcNow;
                        if (!await SaveProgressAsync(id, total > 0 ? Math.Clamp((double)done / total.Value, 0, 0.99) : 0, ct))
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var timedOut = overall.IsCancellationRequested;
            LogPodcastFetchFailed(id, timedOut ? "took too long" : "the host stopped sending");
            return timedOut ? "The podcast's host took too long to send this episode." : "The podcast's host stopped answering.";
        }
    }

    /// <summary>A show's title as a folder name: without characters file systems refuse, and never empty.</summary>
    internal static string SafeName(string title)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat([':', '/', '\\', '?', '*', '"', '<', '>', '|']).ToHashSet();
        var name = new string([.. title.Select(c => invalid.Contains(c) || char.IsControl(c) ? ' ' : c)]).Trim().Trim('.');
        return name.Length == 0 ? "Podcast" : name.Length > 100 ? name[..100].Trim() : name;
    }

    /// <summary>
    /// How far a conversion has got when it no longer stops for someone watching: stopping throws the work away, and
    /// what's left of it (at low priority) costs the stream less than starting again later would cost the queue.
    /// </summary>
    internal const double NearlyDone = 0.9;

    /// <summary>
    /// Whether a download's conversion waits (or stops, going back in the queue) for people watching converted streams.
    /// Converting in software, it does for anyone: the processor is theirs. With a hardware encoder it carries on, below
    /// them, while the server has a conversion slot to spare (<see cref="TranscodeSettings.MaxConcurrentTranscodes"/>),
    /// so a household that streams most evenings doesn't restart a long film's conversion every time.
    /// </summary>
    internal bool MakesWay(TranscodeSettings settings)
    {
        var encoding = transcodes.EncodingCount;
        if (encoding == 0)
        {
            return false;
        }

        return settings.Resolved == HardwareAcceleration.None || (settings.MaxConcurrentTranscodes > 0 && encoding >= settings.MaxConcurrentTranscodes);
    }

    /// <summary>What <see cref="RunAsync"/> gives back when it stopped to make way for someone watching.</summary>
    private static readonly string Yielded = new("yielded".ToCharArray());

    /// <summary>What <see cref="RunAsync"/> gives back when it stopped for someone watching and ffmpeg finished its file cleanly.</summary>
    private static readonly string Paused = new("paused".ToCharArray());

    /// <summary>How long ffmpeg has to finish its file once asked to stop (it moves the index to the front first).</summary>
    internal TimeSpan StopGrace { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Runs ffmpeg, noting its progress; null when it worked, else what went wrong, or <see cref="Yielded"/> when
    /// <paramref name="yieldTo"/> said to stop for someone else (it isn't asked once the conversion is <see cref="NearlyDone"/>).
    /// When it <paramref name="stopsGently"/>, ffmpeg is asked to stop (a <c>q</c> on stdin) rather than killed, and
    /// <see cref="Paused"/> means it finished what it had made into a whole file. <paramref name="startSeconds"/> is
    /// where in the film this run started, for the progress.
    /// </summary>
    private async Task<string?> RunAsync(string ffmpeg, IReadOnlyList<string> args, double duration, Guid id, CancellationToken ct, Func<bool>? yieldTo = null,
        double startSeconds = 0, bool stopsGently = false)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = stopsGently, UseShellExecute = false,
        };
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
        var done = 0.0;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (done < NearlyDone && yieldTo?.Invoke() == true)
                {
                    if (stopsGently && await StopGentlyAsync(process, ct))
                    {
                        return Paused;
                    }

                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(ct);
                    return Yielded;
                }

                if (line.StartsWith("out_time_us=", StringComparison.Ordinal) && duration > 0
                    && long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
                {
                    done = Math.Clamp((startSeconds + (micros / 1_000_000.0)) / duration, 0, 0.99);
                }

                // Every few seconds whatever ffmpeg said (it writes its progress twice a second): with no duration to
                // measure by there's no fraction to save, but it's still how a deletion meanwhile is noticed.
                if (DateTime.UtcNow - lastSaved < TimeSpan.FromSeconds(3))
                {
                    continue;
                }

                lastSaved = DateTime.UtcNow;
                if (!await SaveProgressAsync(id, done, ct))
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

    /// <summary>
    /// Asks ffmpeg to stop (<c>q</c>, as at a terminal) and waits for it to finish its file; true when it did, false
    /// when it wouldn't (the caller then kills it and the piece is thrown away).
    /// </summary>
    private async Task<bool> StopGentlyAsync(Process process, CancellationToken ct)
    {
        try
        {
            await process.StandardInput.WriteAsync('q');
            await process.StandardInput.FlushAsync(ct);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            return false; // gone already
        }

        using var grace = CancellationTokenSource.CreateLinkedTokenSource(ct);
        grace.CancelAfter(StopGrace);
        try
        {
            // Its last words go on stdout, which must be read for it to get them out and finish.
            while (await process.StandardOutput.ReadLineAsync(grace.Token) is not null)
            {
            }

            await process.WaitForExitAsync(grace.Token);
            return process.ExitCode == 0;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }

    /// <summary>Saves how far it's got; false if the download has been deleted (or, a podcast episode's, taken off the phone).</summary>
    private async Task<bool> SaveProgressAsync(Guid id, double progress, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        using var quietly = DataVersion.Quietly(); // every few seconds, and nothing Home shows
        return await db.Downloads.IgnoreQueryFilters().Where(d => d.Id == id && d.Status == DownloadStatus.Preparing)
            .ExecuteUpdateAsync(u => u.SetProperty(d => d.Progress, progress), ct) > 0;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} stopped to make way for someone watching; it starts again when they're done")]
    private partial void LogYielded(Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Download {Id} paused at {Seconds}s to make way for someone watching; it carries on from there when they're done")]
    private partial void LogPaused(Guid id, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id}: the {Encoder} encoder failed; converting in software instead")]
    private partial void LogSoftwareFallback(Guid id, string encoder);

    [LoggerMessage(Level = LogLevel.Error, Message = "Preparing download {Id} failed")]
    private partial void LogPrepareFailed(Exception exception, Guid id);

    [LoggerMessage(Level = LogLevel.Information, Message = "Preparing download {Id} ({Quality})")]
    private partial void LogPreparing(Guid id, string quality);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Download {Id} wasn't prepared: too little room left on the drive holding {Folder}")]
    private partial void LogShortOfSpace(Guid id, string folder);

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
