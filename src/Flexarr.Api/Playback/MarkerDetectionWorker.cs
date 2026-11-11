using System.Collections.Concurrent;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Streaming.Markers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>Seasons to analyse ahead of the background sweep, e.g. the one someone just started watching.</summary>
public sealed class MarkerQueue
{
    private readonly ConcurrentQueue<Guid> _seasons = new();
    private readonly ConcurrentDictionary<Guid, bool> _waiting = new();

    /// <summary>Puts the season next in line, once: asking again while it waits (another episode started) changes nothing.</summary>
    public void Prioritize(Guid seasonId)
    {
        if (_waiting.TryAdd(seasonId, true))
        {
            _seasons.Enqueue(seasonId);
        }
    }

    public bool TryNext(out Guid seasonId)
    {
        if (!_seasons.TryDequeue(out seasonId))
        {
            return false;
        }

        _waiting.TryRemove(seasonId, out _);
        return true;
    }
}

/// <summary>Works through the library finding intros and credits, one season at a time, while the server is idle.</summary>
public sealed partial class MarkerDetectionWorker(
    IServiceScopeFactory scopes,
    MarkerQueue queue,
    PlaybackSessions sessions,
    Flexarr.Streaming.TranscodeManager transcodes,
    IOptionsMonitor<FlexarrOptions> options,
    ILogger<MarkerDetectionWorker> logger) : BackgroundService
{
    /// <summary>How long analysis stops for at a time, while people are watching.</summary>
    private static readonly TimeSpan Pause = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The most <see cref="Pause"/>s in a row before the next episode is analysed anyway (ten minutes), so a server that
    /// is always converting something still gets its intros found, an episode at a time.
    /// </summary>
    private const int MostPauses = 30;

    /// <summary>How long the sweep leaves a season whose analysis failed (an episode that makes FFmpeg hang) before trying it again.</summary>
    private static readonly TimeSpan FailedSeasonRest = TimeSpan.FromDays(1);

    /// <summary>Seasons whose analysis failed, and when, so the sweep moves on to the rest of the library.</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _failed = new();

    private bool Watched => sessions.Playing.Any(s => DateTime.UtcNow - s.LastSeen < TimeSpan.FromMinutes(2));

    /// <summary>
    /// Waits before an episode is analysed (two decodes of its sound and their fingerprints, seconds of a whole core)
    /// while that would get in the way: one pause while anyone is watching, and for as long as a stream is being
    /// converted, up to <paramref name="most"/> pauses. A season someone <paramref name="asked"/> for (the one they
    /// just started watching, so skip intro is there for the next episode) only ever waits the one pause an episode.
    /// Returns how many pauses it took.
    /// </summary>
    internal static async Task<int> GiveWayAsync(Func<bool> converting, Func<bool> watching, bool asked, TimeSpan pause, int most, CancellationToken ct)
    {
        for (var pauses = 0; pauses < most; pauses++)
        {
            var busy = converting();
            if (!busy && (pauses > 0 || !watching()))
            {
                return pauses;
            }

            await Task.Delay(pause, ct);
            if (asked || !busy)
            {
                return pauses + 1;
            }
        }

        return most;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // Let startup scans go first.
        var detector = new MarkerDetector(options.CurrentValue.FfmpegPath, options.CurrentValue.FfprobePath);
        try
        {
            using var quietly = DataVersion.Quietly();
            await DropEarlyChapterCreditsAsync(stoppingToken);
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailed(ex);
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool worked;
                using (DataVersion.Quietly()) // intros and credits found: nothing Home shows
                {
                    worked = await RunOnceAsync(detector, stoppingToken);
                }

                // Give way to viewers: analysis competes with playback (and subtitle extraction) for the disk.
                var busy = Watched;
                await Task.Delay(!worked ? TimeSpan.FromMinutes(10) : busy ? Pause : TimeSpan.FromSeconds(1), stoppingToken);
            }
            // Only shutdown ends the loop: a timeout inside (a TaskCanceledException) is just a failed pass.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    /// <returns>false when there's nothing left to analyse.</returns>
    private async Task<bool> RunOnceAsync(MarkerDetector detector, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();

        var asked = queue.TryNext(out var prioritized);
        Guid? seasonId = asked ? prioritized : null;
        // Asked for again while it was being analysed (another episode, a new audio track): nothing left to do.
        if (seasonId is { } next && !await NeedsAnalysisAsync(db, next, ct))
        {
            return true;
        }

        if (seasonId is null)
        {
            var now = DateTime.UtcNow;
            var resting = _failed.Where(f => now - f.Value < FailedSeasonRest).Select(f => f.Key).ToList();
            // Seasons of shows being watched first, then the most recently added.
            seasonId = await db.MediaItems
                .Where(s => s.Kind == MediaKind.Season && s.Children.Any(e => e.Files.Any(f => f.MarkersScannedAt == null && f.ProbeError == null)))
                .Where(s => !resting.Contains(s.Id))
                .OrderByDescending(s => s.Parent!.Children.SelectMany(x => x.Children).SelectMany(e => e.PlaybackStates).Max(p => (DateTime?)p.LastPlayedAt))
                .ThenByDescending(s => s.Children.Max(e => e.AddedAt))
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        if (seasonId is { } id)
        {
            try
            {
                await AnalyseSeasonAsync(db, detector, id, asked, ct);
                _failed.TryRemove(id, out _);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _failed[id] = DateTime.UtcNow;
                throw;
            }

            return true;
        }

        // Movies: chapter markers only (there's nothing to compare against).
        var movies = await db.MediaFiles.Where(f => f.MarkersScannedAt == null && f.ProbeError == null && f.MediaItem!.Kind == MediaKind.Movie)
            .Take(20).ToListAsync(ct);
        foreach (var file in movies)
        {
            await SaveAsync(db, file, await detector.ChapterMarkersAsync(file.Path, ct), ct);
        }

        await db.SaveChangesAsync(ct);
        return movies.Count > 0;
    }

    /// <summary>Whether any of the season's files is still to be analysed.</summary>
    internal static Task<bool> NeedsAnalysisAsync(FlexarrDbContext db, Guid seasonId, CancellationToken ct) =>
        db.MediaFiles.AnyAsync(f => f.MediaItem!.ParentId == seasonId && f.MarkersScannedAt == null && f.ProbeError == null, ct);

    /// <summary>
    /// Two versions of an episode this close in length are the same edit, so have the same intro and credits: within a
    /// second and a half, or 0.2%, since the same edit at 23.976 and 24 frames a second runs about 0.1% apart.
    /// </summary>
    internal static bool SameEdit(double a, double b) => Math.Abs(a - b) <= Math.Max(1.5, Math.Max(a, b) * 0.002);

    /// <summary>Marks found on one version, for a version of the same edit: stretched to its length, as a different frame rate stretches them.</summary>
    internal static IEnumerable<DetectedMarker> FittedTo(IEnumerable<DetectedMarker> marks, double analysed, double length) =>
        marks.Select(m => m with { Start = m.Start * length / analysed, End = m.End * length / analysed });

    private async Task AnalyseSeasonAsync(FlexarrDbContext db, MarkerDetector detector, Guid seasonId, bool asked, CancellationToken ct)
    {
        // A folder of home videos isn't a season of a show: its videos share no intro or credits to find.
        if (await db.MediaItems.AnyAsync(s => s.Id == seasonId && db.Libraries.Any(l => l.Id == s.LibraryId && l.Type == LibraryType.HomeVideos), ct))
        {
            await db.MediaFiles.Where(f => f.MediaItem!.ParentId == seasonId && f.MarkersScannedAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(f => f.MarkersScannedAt, DateTime.UtcNow), ct);
            return;
        }

        var started = TimeProvider.System.GetTimestamp();
        var order = await db.MediaItems.Where(e => e.ParentId == seasonId).OrderBy(e => e.SortTitle).Select(e => e.Id).ToListAsync(ct);
        var readable = (await db.MediaFiles.Where(f => f.MediaItem!.ParentId == seasonId && f.ProbeError == null).ToListAsync(ct)).ToLookup(f => f.MediaItemId);
        var versions = order.Select(e => VersionRules.BestFirst(readable[e]).ToList()).Where(v => v.Count > 0).ToList();
        var files = versions.Select(v => v[0]).ToList();
        if (files.Count == 0)
        {
            return;
        }

        // Giving way to viewers between episodes, not only between seasons: a season is minutes of a core on a small server.
        var found = await detector.DetectSeasonAsync(files, ct,
            giveWay: wait => GiveWayAsync(() => transcodes.ActiveCount > 0, () => Watched, asked, Pause, MostPauses, wait));
        foreach (var file in files)
        {
            await SaveAsync(db, file, found[file.Id], ct);
        }

        // An episode's other versions: one that runs the same as what's been analysed has the same marks, and the rest
        // are compared with each other, a version from each episode at a time.
        var rest = new List<List<MediaFile>>();
        foreach (var episode in versions)
        {
            var left = new List<MediaFile>();
            foreach (var other in episode.Skip(1).Where(f => f.MarkersScannedAt == null))
            {
                if (other.DurationSeconds is { } length && episode[0].DurationSeconds is { } analysed && SameEdit(length, analysed))
                {
                    await SaveAsync(db, other, FittedTo(found[episode[0].Id], analysed, length), ct);
                }
                else
                {
                    left.Add(other);
                }
            }

            rest.Add(left);
        }

        await db.SaveChangesAsync(ct);
        for (var round = 0; rest.Any(left => left.Count > round); round++)
        {
            var these = rest.Where(left => left.Count > round).Select(left => left[round]).ToList();
            var marks = await detector.DetectSeasonAsync(WithPartner(these, files), ct,
                giveWay: wait => GiveWayAsync(() => transcodes.ActiveCount > 0, () => Watched, asked, Pause, MostPauses, wait));
            foreach (var file in these)
            {
                await SaveAsync(db, file, marks[file.Id], ct);
            }

            await db.SaveChangesAsync(ct);
        }

        var title = await db.MediaItems.Where(s => s.Id == seasonId).Select(s => s.Parent!.Title + " " + s.Title).FirstAsync(ct);
        LogSeason(title, files.Count, found.Values.Count(m => m.Any(x => x.Type == MarkerType.Intro)),
            found.Values.Count(m => m.Any(x => x.Type == MarkerType.Credits)), TimeProvider.System.GetElapsedTime(started).TotalSeconds);
    }

    /// <summary>
    /// What to compare a round of versions with. Comparing takes two, so one on its own (a season's only extended
    /// episode) is compared with another episode's analysed file; only its own marks are kept.
    /// </summary>
    internal static IReadOnlyList<MediaFile> WithPartner(List<MediaFile> round, IReadOnlyList<MediaFile> analysed) =>
        round.Count == 1 && analysed.FirstOrDefault(f => f.MediaItemId != round[0].MediaItemId) is { } partner ? [round[0], partner] : round;

    /// <summary>
    /// Chapter-title credits found before <see cref="MarkerDetector.FromChapters"/> learnt to leave out ones well before
    /// the end (a film's "Ending" chapter, mid-climax): the same rule, applied to what's saved.
    /// </summary>
    private async Task DropEarlyChapterCreditsAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var dropped = await db.MediaMarkers
            .Where(m => m.Source == "chapters" && m.Type == MarkerType.Credits)
            .Where(m => db.MediaFiles.Any(f => f.Id == m.MediaFileId && f.DurationSeconds != null
                && f.DurationSeconds - m.EndSeconds > (f.DurationSeconds * 0.1 > 300 ? f.DurationSeconds * 0.1 : 300)))
            .ExecuteDeleteAsync(ct);
        if (dropped > 0)
        {
            LogDroppedCredits(dropped);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Dropped {Count} chapter credits markers that came well before the end")]
    private partial void LogDroppedCredits(int count);

    private static async Task SaveAsync(FlexarrDbContext db, MediaFile file, IEnumerable<DetectedMarker> markers, CancellationToken ct)
    {
        await db.MediaMarkers.Where(m => m.MediaFileId == file.Id).ExecuteDeleteAsync(ct);
        db.MediaMarkers.AddRange(markers.Select(m => new MediaMarker
        {
            MediaFileId = file.Id,
            Type = m.Type,
            StartSeconds = Math.Round(m.Start, 2),
            EndSeconds = Math.Round(m.End, 2),
            Source = m.Source,
        }));
        file.MarkersScannedAt = DateTime.UtcNow;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysed {Season}: {Episodes} episodes, {Intros} intros, {Credits} credits in {Seconds:F0}s")]
    private partial void LogSeason(string season, int episodes, int intros, int credits, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Intro detection failed")]
    private partial void LogFailed(Exception exception);
}
