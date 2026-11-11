using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Probing;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Playback;

/// <summary>
/// Reads the ReplayGain of tracks scanned before the scan kept it (<see cref="MediaFile.Loudness"/>), so their first play
/// doesn't wait for ffprobe either. A few at a time, only while nothing is being watched or converted, and only files
/// still as the scan found them: one changed since is read again by the next scan, which keeps its ReplayGain then.
/// </summary>
public sealed partial class LoudnessBackfill(
    IServiceScopeFactory scopes,
    IMediaProbe probe,
    PlaybackSessions sessions,
    Flexarr.Streaming.TranscodeManager transcodes,
    ILogger<LoudnessBackfill> logger) : BackgroundService
{
    /// <summary>Tracks read between short rests.</summary>
    internal const int BatchSize = 20;

    private readonly Sweep _sweep = new();

    private bool Busy =>
        transcodes.ActiveCount > 0 || sessions.Playing.Any(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken); // Let startup scans and the rest go first.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Busy)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var left = await BackfillAsync(db, probe, _sweep, () => Busy, stoppingToken);
                // Nothing left: tracks scanned from now on keep their ReplayGain as they're scanned.
                await Task.Delay(left ? TimeSpan.FromSeconds(2) : TimeSpan.FromHours(6), stoppingToken);
            }
            // Only shutdown ends the loop.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
            }
        }
    }

    /// <summary>Where a sweep through the library has got to.</summary>
    internal sealed class Sweep
    {
        /// <summary>The last path read, so each batch picks up after it along the path index rather than from the start.</summary>
        public string? After { get; set; }

        /// <summary>Files that couldn't be read (not there, changed since the scan, ffprobe failed): left to their first play.</summary>
        public HashSet<Guid> Skipped { get; } = [];
    }

    /// <summary>
    /// Reads up to <see cref="BatchSize"/> tracks' ReplayGain and keeps it with their files, stopping early when
    /// <paramref name="busy"/> says so. Returns false when no track was left to read (and starts the next sweep afresh).
    /// </summary>
    internal static async Task<bool> BackfillAsync(FlexarrDbContext db, IMediaProbe probe, Sweep sweep, Func<bool> busy, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(sweep);
        ArgumentNullException.ThrowIfNull(busy);

        // Files skipped before are passed over here rather than in the query: a drive that isn't there skips every
        // track on it, far more ids than SQLite takes as parameters. Pages of only those are walked past without a rest.
        List<(Guid Id, string Path, DateTime ModifiedAt)> tracks;
        while (true)
        {
            var after = sweep.After;
            var page = await Candidates(db, after).Select(f => new { f.Id, f.Path, f.ModifiedAt }).Take(BatchSize).ToListAsync(ct);
            if (page.Count == 0)
            {
                sweep.After = null;
                return false;
            }

            tracks = page.Where(t => !sweep.Skipped.Contains(t.Id)).Select(t => (t.Id, t.Path, t.ModifiedAt)).ToList();
            if (tracks.Count > 0)
            {
                break;
            }

            sweep.After = page[^1].Path;
        }

        foreach (var track in tracks)
        {
            if (busy())
            {
                return true;
            }

            sweep.After = track.Path;

            // A file not there (a drive not mounted) or changed since the scan isn't the one the row describes.
            if (!File.Exists(track.Path) || File.GetLastWriteTimeUtc(track.Path) != track.ModifiedAt)
            {
                sweep.Skipped.Add(track.Id);
                continue;
            }

            Loudness loudness;
            try
            {
                loudness = ReplayGain.FromTags((await probe.ProbeAsync(track.Path, ct)).Tags);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                sweep.Skipped.Add(track.Id);
                continue;
            }

            if (File.GetLastWriteTimeUtc(track.Path) != track.ModifiedAt)
            {
                sweep.Skipped.Add(track.Id); // changed while it was read
                continue;
            }

            // Unless a scan has read the file again meanwhile.
            var json = loudness.ToJson();
            var modifiedAt = track.ModifiedAt;
            using var quietly = DataVersion.Quietly(); // nothing Home shows
            await db.MediaFiles.IgnoreQueryFilters()
                .Where(f => f.Id == track.Id && f.Loudness == null && f.ModifiedAt == modifiedAt)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.Loudness, json), ct);
        }

        return true;
    }

    /// <summary>Tracks whose files have no ReplayGain kept, in path order after <paramref name="after"/>.</summary>
    internal static IQueryable<MediaFile> Candidates(FlexarrDbContext db, string? after)
    {
        var files = db.MediaFiles.IgnoreQueryFilters().AsNoTracking();
        if (after is not null)
        {
#pragma warning disable CA1309 // translated to SQL, which compares the paths byte by byte, as the path index orders them
            files = files.Where(f => string.Compare(f.Path, after) > 0);
#pragma warning restore CA1309
        }

        return files.Where(f => f.Loudness == null && f.ProbeError == null && f.MissingSince == null && f.MediaItem!.Kind == MediaKind.Track)
            .OrderBy(f => f.Path);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading the ReplayGain of tracks scanned before it was kept failed")]
    private partial void LogFailed(Exception exception);
}
