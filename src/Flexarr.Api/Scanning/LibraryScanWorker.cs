using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
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
    ILogger<LibraryScanWorker> logger,
    LibraryWorkGate gate,
    FolderWatcher? watcher = null,
    Events.PlaybackEvents? events = null) : BackgroundService
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ScheduledScanInterval = TimeSpan.FromHours(12);

    /// <summary>
    /// The schedule's interval for a folder <see cref="FolderWatcher"/> keeps up with: its changes are noticed as they
    /// happen, so the full scan is only a backstop for an event the system dropped, and the drive is woken less.
    /// </summary>
    internal static readonly TimeSpan WatchedScanInterval = TimeSpan.FromDays(7);

    internal const string StartupReason = "Startup";

    /// <summary>
    /// More new titles than this in one scan (a new library, a big import) aren't news: no "added" events for them, so an
    /// automation that announces new episodes doesn't read out a whole collection.
    /// </summary>
    internal const int MaxAddedEvents = 25;

    /// <summary>No profile has this id, so <see cref="ItemQueries.Summaries"/> says nothing of anyone's watching.</summary>
    private const string NoProfile = "";

    /// <summary>How often the schedule looks for a library folder whose last full scan is <see cref="ScheduledScanInterval"/> old.</summary>
    private static readonly TimeSpan ScheduleCheck = TimeSpan.FromMinutes(15);

    private static readonly TimeSpan RememberScansFor = TimeSpan.FromHours(1);

    /// <summary>When the last scan of each path started (see <see cref="AlreadyScanned"/>). Only this worker's own loop uses it.</summary>
    private readonly Dictionary<string, DateTime> _scanStartedAt = new(StringComparer.Ordinal);

    /// <summary>When the schedule last queued each library folder, since the server started: one queued but not yet done isn't queued again.</summary>
    private readonly Dictionary<string, DateTime> _queuedAt = new(StringComparer.Ordinal);

    /// <summary>
    /// The full scans of folders startup left alone for being recent, by path. Whatever was copied in while the server was
    /// down hasn't been seen, so these stay on <see cref="ScheduledScanInterval"/> until a scan newer than that one.
    /// </summary>
    private readonly Dictionary<string, DateTime?> _skippedAtStartup = new(StringComparer.Ordinal);

    /// <summary>How long a watched scan that failed waits before its next try: the watcher won't hear of those files again.</summary>
    internal TimeSpan RetryWatchedAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>What's been announced as added lately. Only this worker's own loop uses it.</summary>
    private readonly Dictionary<Guid, DateTime> _announcedAt = [];

    /// <summary>Scans waiting for <see cref="RetryWatchedAfter"/>, by path.</summary>
    private readonly Dictionary<string, LibraryScanRequest> _retrying = new(StringComparer.Ordinal);

    /// <summary>The tries after the first. Then it's the weekly scan's: a folder that goes on failing shouldn't go on being scanned.</summary>
    internal const int MaxWatchedRetries = 2;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Catch up on anything that changed while the server was down (and finish interrupted scans). A folder scanned
        // in full less than 12 hours ago waits until then: a restart doesn't scan everything again.
        await EnqueueAllSafelyAsync(StartupReason, stoppingToken);

        _ = ScheduleAsync(stoppingToken);

        await foreach (var first in queue.ReadAllAsync(stoppingToken))
        {
            await Task.Delay(Debounce, stoppingToken);

            var batch = new List<LibraryScanRequest> { first };
            while (queue.TryDequeue(out var next))
            {
                batch.Add(next);
            }

            // The endpoints refuse such paths; one getting here anyway must not stop every scan after it.
            foreach (var unusable in batch.Where(r => PathComparison.HasControlCharacters(r.Path)))
            {
                LogUnusablePath(PathComparison.Printable(unusable.Path), PathComparison.Printable(unusable.Reason));
            }

            List<LibraryScanRequest> requests;
            try
            {
                requests = Coalesce(batch.Where(r => !PathComparison.HasControlCharacters(r.Path))).ToList();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogBatchFailed(ex, batch.Count);
                continue;
            }

            foreach (var request in requests)
            {
                var attempt = DateTime.UtcNow;
                try
                {
                    using var entered = await gate.EnterAsync(stoppingToken);
                    attempt = DateTime.UtcNow;
                    if (await ScanAsync(request, attempt, stoppingToken))
                    {
                        // It finished, so nothing about it needs doing twice but looking again.
                        RetryLater(request with { ForceMetadata = false, AddedSince = null }, stoppingToken);
                    }
                }
                // Anything but shutdown, including an HTTP timeout (a TaskCanceledException), is one failed scan.
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogScanFailed(ex, PathComparison.Printable(request.Path));
                    // What it saved before it failed hasn't been announced: the next try goes back to here.
                    RetryLater(request with { AddedSince = request.AddedSince ?? attempt }, stoppingToken);
                }
            }
        }
    }

    /// <summary>
    /// Queues a watched scan that failed or couldn't see everything again, in a while: a NAS's hiccup or a busy database
    /// shouldn't hide a new film for a week. One wait per path: failures while it waits join it rather than stack up.
    /// </summary>
    private void RetryLater(LibraryScanRequest request, CancellationToken ct)
    {
        if (request.Reason != FolderWatcher.Reason || request.Retries >= MaxWatchedRetries)
        {
            return;
        }

        var key = PathComparison.Key(request.Path);
        var next = request with { Retries = request.Retries + 1 };
        lock (_retrying)
        {
            if (_retrying.TryGetValue(key, out var waiting))
            {
                _retrying[key] = Coalesce([waiting, next]).Single();
                return;
            }

            _retrying[key] = next;
        }

        _ = QueueRetryAsync(key, ct);
    }

    private async Task QueueRetryAsync(string key, CancellationToken ct)
    {
        try
        {
            await Task.Delay(RetryWatchedAfter, ct);
            LibraryScanRequest request;
            lock (_retrying)
            {
                _retrying.Remove(key, out request!);
            }

            await queue.EnqueueAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the next start catches up.
        }
    }

    /// <summary>Picks up changes the *arr webhooks don't cover (files added by hand, other tools).</summary>
    private async Task ScheduleAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(ScheduleCheck);
        while (await timer.WaitForNextTickAsync(ct))
        {
            await EnqueueAllSafelyAsync("Scheduled", ct);
        }
    }

    /// <summary>A failure here (the database busy, say) is logged and the next tick tries again.</summary>
    private async Task EnqueueAllSafelyAsync(string reason, CancellationToken ct)
    {
        try
        {
            await EnqueueAllAsync(reason, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            LogEnqueueFailed(ex, reason);
        }
    }

    /// <summary>
    /// Each library folder whose last full scan finished <see cref="ScheduledScanInterval"/> ago or more (or that never
    /// had one), unless the schedule queued it within that time; <see cref="WatchedScanInterval"/> for a folder that's
    /// being watched, except at startup, while it's on hold, or until the first full scan after a startup that skipped it.
    /// </summary>
    internal async Task EnqueueAllAsync(string reason, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        foreach (var folder in await db.LibraryFolders.AsNoTracking().Select(f => new { f.Path, f.FullScanAt, f.Hold }).ToListAsync(ct))
        {
            var now = DateTime.UtcNow;
            var last = folder.FullScanAt;
            if (_queuedAt.TryGetValue(folder.Path, out var queued) && (last is null || queued > last))
            {
                last = queued; // Queued and not done yet, or it failed: tried again when the interval is up, as before.
            }

            // At startup the watcher wasn't running while the server was down, so whatever changed then was missed. And a
            // folder on hold depends on full scans: only they end a hold, or see that it's lasted (LibraryScanner.KeepHolding).
            if (_skippedAtStartup.TryGetValue(folder.Path, out var skipped) && folder.FullScanAt > skipped)
            {
                _skippedAtStartup.Remove(folder.Path);
            }

            var interval = reason != StartupReason && folder.Hold is null && !_skippedAtStartup.ContainsKey(folder.Path) && watcher?.KeepsUpWith(folder.Path) == true ? WatchedScanInterval : ScheduledScanInterval;
            if (last is { } at && at <= now && now - at < interval)
            {
                if (reason == StartupReason)
                {
                    _skippedAtStartup[folder.Path] = folder.FullScanAt;
                }

                continue;
            }

            _queuedAt[folder.Path] = now;
            await queue.EnqueueAsync(new LibraryScanRequest(folder.Path, reason), ct);
        }
    }

    /// <summary>
    /// Drops duplicates and paths already covered by a broader request in the same batch. A path counts as deleted
    /// only if every request for it says so. A title's changed cover or .nfo the watcher heard of isn't passed up to a
    /// broader request, which would look every title in it up again: the title is scanned alone, after.
    /// </summary>
    public static IEnumerable<LibraryScanRequest> Coalesce(IEnumerable<LibraryScanRequest> requests)
    {
        var all = requests.ToList();
        var distinct = all.GroupBy(r => PathComparison.Key(r.Path))
            .Select(g => g.First() with
            {
                ForceMetadata = g.Any(r => r.ForceMetadata),
                Deleted = g.All(r => r.Deleted),
                TmdbId = g.Select(r => r.TmdbId).FirstOrDefault(id => id is not null),
                TvdbId = g.Select(r => r.TvdbId).FirstOrDefault(id => id is not null),
            })
            .ToList();
        return distinct
            .Where(r => !distinct.Any(o => o.Path != r.Path && PathComparison.IsUnder(r.Path, o.Path)))
            .SelectMany(r =>
            {
                var covered = all.Where(o => PathComparison.IsUnder(o.Path, r.Path)).ToList();
                bool StaysApart(LibraryScanRequest o) => o.ForceMetadata && o.Reason == FolderWatcher.Reason && PathComparison.Key(o.Path) != PathComparison.Key(r.Path);
                var apart = covered.Where(StaysApart).DistinctBy(o => PathComparison.Key(o.Path));
                return apart.Prepend(r with
                {
                    ForceMetadata = covered.Any(o => o.ForceMetadata && !StaysApart(o)),
                    LastChange = covered.All(o => o.LastChange is not null) ? covered.Max(o => o.LastChange) : null,
                    OnlySubtitles = covered.All(o => o.OnlySubtitles),
                    Retries = covered.Max(o => o.Retries),
                    AddedSince = covered.Min(o => o.AddedSince),
                    // A broader scan in its place: the watcher still won't hear of these files again if it fails.
                    Reason = covered.Any(o => o.Reason == FolderWatcher.Reason) ? FolderWatcher.Reason : r.Reason,
                });
            });
    }

    /// <summary>
    /// Whether a scan that started after the watcher last heard of this change has already covered it: Sonarr's webhook
    /// and the watcher both report one import, and the second would be a scan and a lookup for nothing. Not a changed
    /// cover or .nfo, which a scan doesn't read afresh without being asked.
    /// </summary>
    internal bool AlreadyScanned(LibraryScanRequest request) =>
        request is { LastChange: { } last, ForceMetadata: false }
        && _scanStartedAt.Any(s => s.Value >= last && PathComparison.IsUnder(request.Path, s.Key));

    internal void NoteScanned(string path, DateTime started)
    {
        foreach (var old in _scanStartedAt.Where(s => started - s.Value > RememberScansFor).Select(s => s.Key).ToList())
        {
            _scanStartedAt.Remove(old);
        }

        _scanStartedAt[path] = started;
    }

    /// <summary>Runs one request. True if it finished without seeing everything, or without being able to read a file it found.</summary>
    private async Task<bool> ScanAsync(LibraryScanRequest request, DateTime scanStarted, CancellationToken ct)
    {
        if (AlreadyScanned(request))
        {
            LogAlreadyScanned(PathComparison.Printable(request.Path));
            return false;
        }

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
            LogNoLibrary(PathComparison.Printable(request.Path), PathComparison.Printable(request.Reason));
            return false;
        }

        var incomplete = false;
        var unread = false;
        foreach (var (library, target) in targets)
        {
            LogScanStarted(library.Name, PathComparison.Printable(target), PathComparison.Printable(request.Reason));
            var started = TimeProvider.System.GetTimestamp();
            // A metadata refresh also retries files ffprobe couldn't read, and so does another try at them.
            var result = await scanner.ScanAsync(library, target, request.ForceMetadata || request.Retries > 0, request.Deleted, ct);
            incomplete |= result.Incomplete;
            unread |= result.ProbeFailures > 0;
            // What it couldn't look at it hasn't scanned: the schedule shouldn't wait a week on it, nor a restart skip it.
            foreach (var folder in result.Incomplete ? [] : library.Folders.Where(f => PathComparison.IsUnder(f.Path, target)))
            {
                // A scan of the whole folder: the schedule's next one is 12 hours (a week, if watched) from now, restart or not.
                var finished = DateTime.UtcNow;
                await db.LibraryFolders.Where(f => f.Id == folder.Id).ExecuteUpdateAsync(u => u.SetProperty(f => f.FullScanAt, finished), ct);
            }

            var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
            LogScanFinished(library.Name, PathComparison.Printable(target), result.Added, result.Updated, result.Missing, result.Removed, result.Unchanged,
                result.Unmatched, result.ProbeFailures, seconds);
        }

        // The scans have saved everything. Their files and streams stay tracked otherwise, and every save of the refresh
        // below would check them all again: minutes, and hundreds of MB, on a big library. The libraries are only read
        // from here on.
        db.ChangeTracker.Clear();

        // A whole library was scanned (at startup, twice a day, after the connections change): first save any mapping its
        // titles make certain, so the refresh below can link them to Sonarr, Radarr or Lidarr.
        var whole = targets.Any(t => t.Library.Folders.Any(f => PathComparison.IsUnder(f.Path, t.Scope)));
        if (whole)
        {
            try
            {
                await scope.ServiceProvider.GetRequiredService<PathMappingFinder>().ApplyCertainAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogMappingsFailed(ex);
            }
        }

        // Subtitles alone change nothing a lookup would find.
        foreach (var library in request.OnlySubtitles ? [] : targets.Select(t => t.Library).DistinctBy(l => l.Id))
        {
            // A scan of one folder (a webhook) also refreshes the films or series in it, so new episodes get their
            // titles straight away rather than when the series is next due. Titles merely out of date are left to
            // MetadataRefreshWorker, a few at a time.
            var folders = library.Folders.Select(f => f.Path).ToList();
            var scopes = targets.Where(t => t.Library.Id == library.Id).Select(t => t.Scope).ToList();
            var within = scopes.Any(s => folders.Any(f => PathComparison.IsUnder(f, s))) ? [] : scopes;

            var started = TimeProvider.System.GetTimestamp();
            var refreshed = await metadata.RefreshScannedAsync(library, request.ForceMetadata, within, request.TmdbId, request.TvdbId, ct);
            if (refreshed.FromArr + refreshed.FromLookup + refreshed.FromLocal + refreshed.Unmatched > 0)
            {
                var seconds = TimeProvider.System.GetElapsedTime(started).TotalSeconds;
                LogMetadataRefreshed(library.Name, refreshed.FromArr, refreshed.FromLookup, refreshed.FromLocal, refreshed.Unmatched, seconds);
            }
        }

        // A film or show that came back under a new folder only has its TMDB/TVDB id once Radarr or Sonarr matched it.
        await WatchHistoryCarryOver.CarryOverAsync(db, ct);
        await TraktHolds.ApplyAsync(db, ct);
        // A whole folder relinks everything; a folder or file in one (the folder watcher, a webhook) only what's in it.
        await TitleLinker.LinkAsync(db, whole ? null : await InScopeAsync(db, targets.Select(t => t.Scope), ct), ct);

        // Not when there's more to see: the next try at it isn't to be taken for a scan that's already been.
        var again = incomplete || unread;
        if (!again)
        {
            NoteScanned(request.Path, scanStarted);
        }

        if (events is not null)
        {
            await AnnounceAddedAsync(db, targets.Select(t => t.Library.Id).Distinct().ToList(), request.AddedSince ?? scanStarted, ct);
        }

        return again;
    }

    /// <summary>
    /// The items a scan of <paramref name="scopes"/> (folders or files) could have changed: those with files there, gone
    /// or not, and those whose own folder is there. LIKE matches regardless of case, and its wildcards only ever find more.
    /// </summary>
    internal static async Task<List<Guid>> InScopeAsync(FlexarrDbContext db, IEnumerable<string> scopes, CancellationToken ct)
    {
        var items = new List<Guid>();
        foreach (var scope in scopes.Distinct(StringComparer.Ordinal))
        {
            var folder = scope.TrimEnd('/');
            var under = folder + "/%";
            items.AddRange(await db.MediaFiles.IgnoreQueryFilters().AsNoTracking().Where(f => EF.Functions.Like(f.Path, folder) || EF.Functions.Like(f.Path, under))
                .Select(f => f.MediaItemId).ToListAsync(ct));
            items.AddRange(await db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.Path != null && (EF.Functions.Like(i.Path, folder) || EF.Functions.Like(i.Path, under)))
                .Select(i => i.Id).ToListAsync(ct));
        }

        return [.. items.Distinct()];
    }

    /// <summary>
    /// "event: added" for the films, episodes, albums and books this scan put in the library, with their details and
    /// nobody's watch state: the stream is the household's, not one profile's. Not ones that only moved (see
    /// <see cref="WasHereBeforeAsync"/>).
    /// </summary>
    internal async Task AnnounceAddedAsync(FlexarrDbContext db, List<Guid> libraries, DateTime since, CancellationToken ct)
    {
        MediaKind[] news = [MediaKind.Movie, MediaKind.Episode, MediaKind.Album, MediaKind.Audiobook];
        var added = db.MediaItems.IgnoreQueryFilters().Where(i => libraries.Contains(i.LibraryId) && i.FirstSeenAt >= since && i.MissingSince == null && news.Contains(i.Kind));
        var count = await added.CountAsync(ct);
        if (count == 0 || count > MaxAddedEvents)
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var old in _announcedAt.Where(a => now - a.Value > RememberScansFor).Select(a => a.Key).ToList())
        {
            _announcedAt.Remove(old);
        }

        foreach (var item in await added.OrderBy(i => i.FirstSeenAt).Summaries(NoProfile).ToListAsync(ct))
        {
            // A later try looks back past scans that have announced theirs since.
            if (!_announcedAt.ContainsKey(item.Id) && !await WasHereBeforeAsync(db, item.Id, since, ct))
            {
                _announcedAt[item.Id] = now;
                events!.PublishAdded(new Events.LibraryAddedEventDto(item, now));
            }
        }
    }

    /// <summary>
    /// A film or show Radarr or Sonarr moved or renamed is a new item at its new path, but nothing new to the household:
    /// the same film or episode is still in the library from before, at its old path, for a day after going missing.
    /// </summary>
    private static async Task<bool> WasHereBeforeAsync(FlexarrDbContext db, Guid id, DateTime since, CancellationToken ct)
    {
        var items = db.MediaItems.IgnoreQueryFilters();
        var item = await items.Where(i => i.Id == id).Select(i => new
        {
            i.Kind, i.Title, i.TmdbId, i.TvdbId, i.ImdbId, i.SeasonNumber, i.EpisodeNumber,
            ParentTitle = i.Parent != null ? i.Parent.Title : null,
            SeriesTmdb = i.Parent != null && i.Parent.Parent != null ? i.Parent.Parent.TmdbId : null,
            SeriesTvdb = i.Parent != null && i.Parent.Parent != null ? i.Parent.Parent.TvdbId : null,
            SeriesImdb = i.Parent != null && i.Parent.Parent != null ? i.Parent.Parent.ImdbId : null,
        }).FirstAsync(ct);
        var earlier = items.Where(i => i.Id != id && i.Kind == item.Kind && i.FirstSeenAt < since);
        if (item.Kind == MediaKind.Episode)
        {
            // The series by its indexed ids first, then its seasons, then only their episodes: season and episode numbers
            // have no index, so asking for them outright reads every earlier episode in the library. Only a series that was
            // here before this scan: one it just added, which is what a new show is, can have no earlier episodes.
            var series = await items.Where(i => i.Kind == MediaKind.Series && (i.FirstSeenAt == null || i.FirstSeenAt < since)
                && ((item.SeriesTmdb != null && i.TmdbId == item.SeriesTmdb) || (item.SeriesTvdb != null && i.TvdbId == item.SeriesTvdb) || (item.SeriesImdb != null && i.ImdbId == item.SeriesImdb)))
                .Select(i => i.Id).ToListAsync(ct);
            if (series.Count == 0)
            {
                return false;
            }

            var seasons = await items.Where(i => i.Kind == MediaKind.Season && i.ParentId != null && series.Contains(i.ParentId.Value)).Select(i => i.Id).ToListAsync(ct);
            return await items.AnyAsync(i => i.ParentId != null && seasons.Contains(i.ParentId.Value) && i.Id != id && i.FirstSeenAt < since
                && i.SeasonNumber == item.SeasonNumber && i.EpisodeNumber == item.EpisodeNumber, ct);
        }

        return item.Kind == MediaKind.Movie
            ? await earlier.AnyAsync(i => (item.TmdbId != null && i.TmdbId == item.TmdbId) || (item.TvdbId != null && i.TvdbId == item.TvdbId) || (item.ImdbId != null && i.ImdbId == item.ImdbId), ct)
            : await earlier.AnyAsync(i => i.Title == item.Title && i.Parent != null && i.Parent.Title == item.ParentTitle, ct);
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path} was already scanned since it last changed")]
    private partial void LogAlreadyScanned(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't work out path mappings before refreshing metadata")]
    private partial void LogMappingsFailed(Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanning {Library}: {Path} ({Reason})")]
    private partial void LogScanStarted(string library, string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Scanned {Library}: {Path} — {Added} added, {Updated} updated, {Missing} newly missing, {Removed} removed, {Unchanged} unchanged, {Unmatched} unmatched, {Failures} probe failures in {Seconds:F1}s")]
    private partial void LogScanFinished(string library, string path, int added, int updated, int missing, int removed, int unchanged, int unmatched, int failures, double seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed metadata for {Library}: {FromArr} from *arr, {FromLookup} looked up, {FromLocal} from local files, {Unmatched} unmatched in {Seconds:F1}s")]
    private partial void LogMetadataRefreshed(string library, int fromArr, int fromLookup, int fromLocal, int unmatched, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): no library contains it")]
    private partial void LogNoLibrary(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Ignoring scan request for {Path} ({Reason}): the path holds a control character")]
    private partial void LogUnusablePath(string path, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't sort out a batch of {Count} scan requests; dropping it")]
    private partial void LogBatchFailed(Exception exception, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Scan of {Path} failed")]
    private partial void LogScanFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't queue the {Reason} scan of every library")]
    private partial void LogEnqueueFailed(Exception exception, string reason);
}
