using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Scanning;

/// <summary>How a library folder is being watched for changes (see <see cref="FolderWatcher"/>).</summary>
public enum FolderWatchState
{
    /// <summary>Changes are noticed as they happen; the full scan is only a weekly backstop.</summary>
    Watching,

    /// <summary>
    /// Watched, but on a network share (or a container's shared folder), which often doesn't report changes made from
    /// other machines: it keeps the 12-hour scan.
    /// </summary>
    Share,

    /// <summary>
    /// Couldn't be watched (see <see cref="FolderWatchProblem"/>): the 12-hour scan, and tried again within five minutes
    /// of coming back if missing. If the system refused, 12 hours later, or as soon as its limit is raised.
    /// </summary>
    Unavailable,

    /// <summary>Watching is switched off in Settings › Libraries.</summary>
    Off,

    /// <summary>Not watched by design: the server fills it itself (podcasts, the DVR's recordings).</summary>
    Excluded,
}

/// <summary>Why a folder is <see cref="FolderWatchState.Unavailable"/>.</summary>
public enum FolderWatchProblem
{
    /// <summary>The folder isn't there: a drive or share that isn't connected.</summary>
    Missing,

    /// <summary>The system ran out of watches (Linux's inotify limit).</summary>
    Limit,

    /// <summary>Anything else the system refused.</summary>
    Failed,
}

public sealed record FolderWatchStatus(FolderWatchState State, FolderWatchProblem? Problem = null);

/// <summary>
/// Watches every library folder for files added, changed, renamed or removed, and queues a scan of just the title they
/// belong to (its folder in the library folder: the film, show, artist or book) once the files have stopped changing,
/// so new files appear straight away rather than at the next scheduled scan. It uses the system's own change events
/// (FSEvents on macOS, inotify on Linux, ReadDirectoryChangesW on Windows), which never read the drive themselves.
/// </summary>
public sealed partial class FolderWatcher(
    ILibraryScanQueue queue,
    IServiceScopeFactory scopes,
    IOptionsMonitor<FlexarrOptions> options,
    TimeProvider time,
    ILogger<FolderWatcher> logger) : BackgroundService
{
    /// <summary>The reason watched scans give, in the log and to <see cref="LibraryScanWorker"/>.</summary>
    public const string Reason = "Watched";

    /// <summary>How long a title's folder must go without events before it's looked at.</summary>
    internal TimeSpan QuietFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Then its files' sizes must stay the same for this long: a copy that stalls isn't scanned half-written.</summary>
    internal TimeSpan SettleFor { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// How often the folders to watch are read again, watched ones are checked for still being the same folder, and ones
    /// that couldn't be watched are tried again.
    /// </summary>
    internal TimeSpan ResyncEvery { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How often a folder that isn't there is looked for: asking can wake a NAS, or an automount's drive.
    /// </summary>
    internal TimeSpan LookForMissingEvery { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How often a watched network share is checked for still being the same folder: each look reaches the machine it's
    /// on, which may be trying to sleep. Local folders are checked at every resync.
    /// </summary>
    internal TimeSpan LookAtSharesEvery { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A title that never goes quiet is scanned anyway after this long: no later than the 12-hour scan it replaced. Not
    /// sooner, since a slow copy of a big film can take hours, and scanning it half-written would show a broken film.
    /// </summary>
    internal TimeSpan WaitsAtMost { get; set; } = TimeSpan.FromHours(12);

    internal TimeSpan TickEvery { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a folder the system refused to watch (its watch limit, say) waits before it's tried again. Each try lists
    /// every folder in it on Linux, which wakes its drive, so no more often than the 12-hour scan does. A raised limit
    /// is tried straight away (see <see cref="WatchLimits"/>).
    /// </summary>
    internal TimeSpan RetryRefusedAfter { get; set; } = TimeSpan.FromHours(12);

    /// <summary>
    /// A title's details are read afresh for a changed cover or <c>.nfo</c> at most this often. Each read asks Sonarr,
    /// Radarr or TMDB, and something rewriting the file shouldn't turn into a stream of lookups. The title's scan still
    /// picks up a replaced picture.
    /// </summary>
    internal TimeSpan ForceAtMostEvery { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Makes a watcher for a folder. Tests replace it, to have a watch refused.</summary>
    internal Func<string, FileSystemWatcher> NewWatcher { get; set; } = root => new FileSystemWatcher(root);

    /// <summary>Whether a folder is on another machine. Tests replace it, having no share to hand.</summary>
    internal Func<string, bool> IsShare { get; set; } = FolderKind.IsShare;

    /// <summary>How long a resync waits for its folders to start before getting on with the next one.</summary>
    internal TimeSpan StartsWaitedFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Starts a watcher: this is where the system walks the folder's tree, and can refuse.</summary>
    internal Action<FileSystemWatcher> Enable { get; set; } = watcher => watcher.EnableRaisingEvents = true;

    /// <summary>
    /// This many titles gone from one library folder within <see cref="GoneWithin"/> is no longer someone deleting or
    /// renaming a film: only a scan of the library folder asks whether most of it has vanished, and holds it if so.
    /// </summary>
    internal const int ManyTitlesGone = 3;

    internal TimeSpan GoneWithin { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>More titles than this waiting in one folder (a whole drive copied in): the folder is scanned as a whole.</summary>
    internal const int MaxPendingPerFolder = 50;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Watched> _folders = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly List<FileSystemWatcher> _broken = [];
    private readonly Dictionary<string, DateTimeOffset> _forcedAt = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<DateTimeOffset>> _goneAt = new(StringComparer.Ordinal);
    private bool _synced;

    /// <summary>Folders whose watch is on its way: a mount that's stopped answering can take as long as it likes.</summary>
    private readonly HashSet<string> _starting = new(StringComparer.Ordinal);

    /// <summary>Stopped for good: a start finishing now has nowhere to go.</summary>
    private bool _stopped;
    private volatile bool _syncSoon;

    /// <summary>
    /// How each library folder is watched, by its path as saved. Empty until the first look at the libraries, shortly
    /// after the server starts.
    /// </summary>
    public IReadOnlyDictionary<string, FolderWatchStatus> Statuses
    {
        get
        {
            lock (_lock)
            {
                return _folders.ToDictionary(f => f.Key, f => f.Value.Status, StringComparer.Ordinal);
            }
        }
    }

    public FolderWatchStatus? StatusOf(string path)
    {
        lock (_lock)
        {
            return _folders.TryGetValue(path, out var watched) ? watched.Status : null;
        }
    }

    /// <summary>
    /// Whether changes in the folder are noticed as they happen, so its full scan can wait a week. False before the first
    /// look at the libraries, for shares and for folders that couldn't be watched.
    /// </summary>
    public bool KeepsUpWith(string path) => StatusOf(path)?.State == FolderWatchState.Watching;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The switch in Settings takes effect within a tick or two, not at the next minute's look.
        using var changes = options.OnChange((_, _) => _syncSoon = true);
        try
        {
            // Apart, so a mount that's stopped answering holds up only the one that reached it.
            await Task.WhenAll(KeepSyncingAsync(stoppingToken), KeepFlushingAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            StopWatching();
        }
    }

    private async Task KeepSyncingAsync(CancellationToken ct)
    {
        await Task.Yield();
        var nextSync = DateTimeOffset.MinValue;
        while (!ct.IsCancellationRequested)
        {
            if (time.GetUtcNow() >= nextSync || _syncSoon)
            {
                _syncSoon = false;
                try
                {
                    await SyncAsync(ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    LogSyncFailed(ex);
                }

                nextSync = time.GetUtcNow() + ResyncEvery;
            }

            await Task.Delay(TickEvery, time, ct);
        }
    }

    private async Task KeepFlushingAsync(CancellationToken ct)
    {
        await Task.Yield();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await FlushAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogFlushFailed(ex);
            }

            await Task.Delay(TickEvery, time, ct);
        }
    }

    public override void Dispose()
    {
        lock (_lock)
        {
            _stopped = true;
        }

        StopWatching();
        base.Dispose();
    }

    private void StopWatching()
    {
        lock (_lock)
        {
            foreach (var watched in _folders.Values)
            {
                watched.Watcher?.Dispose();
            }

            _broken.ForEach(watcher => watcher.Dispose());
            _broken.Clear();
            _folders.Clear();
            _pending.Clear();
            _goneAt.Clear();
        }
    }

    /// <summary>
    /// Starts watching library folders that are new (or that couldn't be watched before and now can), stops watching
    /// ones that have gone, and follows the switch in Settings.
    /// </summary>
    internal async Task SyncAsync(CancellationToken ct)
    {
        List<(string Path, bool Excluded)> wanted;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var libraries = await db.Libraries.IgnoreQueryFilters().AsNoTracking().Include(l => l.Folders).ToListAsync(ct);
            wanted = libraries
                .SelectMany(l => l.Folders.Select(f => (f.Path, Excluded: l.Type == LibraryType.Podcasts || RecordingsLibrary.Is(l))))
                .DistinctBy(f => f.Path)
                .ToList();
        }

        var enabled = options.CurrentValue.WatchFolders.Enabled;
        var finished = new List<FileSystemWatcher>();
        var toStart = new List<string>();
        var limits = WatchLimits();
        // Nothing tells a watcher its folder was unmounted, or deleted and made again: only looking does.
        var now = time.GetUtcNow();
        List<string> live;
        lock (_lock)
        {
            live = _folders.Where(f => f.Value.Watcher is not null && (f.Value.Status.State != FolderWatchState.Share || now - f.Value.LookedAt >= LookAtSharesEvery))
                .Select(f => f.Key).ToList();
        }

        var found = live.ToDictionary(path => path, FolderKind.Identity, StringComparer.Ordinal);
        lock (_lock)
        {
            finished.AddRange(_broken);
            _broken.Clear();
            foreach (var gone in _folders.Keys.Where(p => wanted.All(w => w.Path != p)).ToList())
            {
                if (_folders[gone].Watcher is { } watcher)
                {
                    finished.Add(watcher);
                }

                _folders.Remove(gone);
                foreach (var key in _pending.Keys.Where(k => _pending[k].Root == gone).ToList())
                {
                    _pending.Remove(key);
                }
            }

            foreach (var (path, excluded) in wanted)
            {
                _folders.TryGetValue(path, out var current);
                if (excluded || !enabled)
                {
                    if (current?.Watcher is { } watcher)
                    {
                        finished.Add(watcher);
                    }

                    _folders[path] = new Watched(null, new FolderWatchStatus(excluded ? FolderWatchState.Excluded : FolderWatchState.Off));
                    foreach (var key in _pending.Keys.Where(k => _pending[k].Root == path).ToList())
                    {
                        _pending.Remove(key);
                    }

                    continue;
                }

                if (_starting.Contains(path))
                {
                    continue;
                }

                if (current?.Watcher is { } running)
                {
                    if (found.TryGetValue(path, out var identity))
                    {
                        if (identity != current.Identity)
                        {
                            finished.Add(running);
                            toStart.Add(path);
                            _starting.Add(path);
                        }
                        else
                        {
                            _folders[path] = current with { LookedAt = now };
                        }
                    }

                    continue;
                }

                var waiting = current?.RetryAt > time.GetUtcNow() && !(current.Status.Problem == FolderWatchProblem.Limit && current.LimitsWere != limits);
                if (!waiting)
                {
                    toStart.Add(path);
                    _starting.Add(path);
                }
            }
        }

        foreach (var watcher in finished)
        {
            watcher.Dispose();
        }

        // Outside the lock: on Linux starting a watch walks the folder's whole tree, and a network mount that's stopped
        // answering would hang here. The libraries list and the change events only ever wait for the lock. Each on its
        // own, so one like that holds up neither the other folders nor, past a while, this resync: it finishes when it
        // finishes, and isn't started twice.
        bool catchUp;
        lock (_lock)
        {
            catchUp = _synced;
            _synced = true;
        }

        var starts = toStart.Select(path => Task.Run(() => StartAndInstallAsync(path, catchUp, ct), CancellationToken.None)).ToList();
        try
        {
            await Task.WhenAll(starts).WaitAsync(StartsWaitedFor, time, ct);
        }
        catch (TimeoutException)
        {
            // Still going, which is theirs to sort out.
        }
    }

    /// <summary>Starts a folder's watch and puts it in place. Runs alone: nothing it hits may be left to the caller, who may have moved on.</summary>
    private async Task StartAndInstallAsync(string path, bool catchUp, CancellationToken ct)
    {
        try
        {
            var (started, starting) = Start(path);
            Exception? late;
            bool back;
            bool stopped;
            lock (_lock)
            {
                _starting.Remove(path);
                stopped = _stopped;
                // A library folder new to the server was queued when it was added.
                var existed = _folders.ContainsKey(path);
                if (!stopped)
                {
                    _folders[path] = started;
                }

                starting.Installed = true;
                late = starting.Error;
                // Watching again after it couldn't be (a drive that's back, the switch turned on): whatever changed in the
                // meantime was missed, so the folder is scanned. Not at startup, which the schedule already covers.
                back = existed && catchUp && !stopped && started.Watcher is not null && late is null;
            }

            if (stopped)
            {
                started.Watcher?.Dispose();
                return;
            }

            if (late is not null && started.Watcher is { } refused)
            {
                Failed(path, refused, late);
            }

            if (back)
            {
                LogWatchingAgain(PathComparison.Printable(path));
                await queue.EnqueueAsync(new LibraryScanRequest(path, Reason), ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (_lock)
            {
                _starting.Remove(path);
            }

            LogChangeFailed(ex, PathComparison.Printable(path));
        }
    }

    private (Watched Watched, Starting Starting) Start(string root)
    {
        var starting = new Starting();
        if (!Directory.Exists(root))
        {
            return (new Watched(null, new FolderWatchStatus(FolderWatchState.Unavailable, FolderWatchProblem.Missing)) { RetryAt = time.GetUtcNow() + LookForMissingEvery }, starting);
        }

        // Before the watch starts: if the folder is swapped meanwhile, the next look sees a difference.
        var identity = FolderKind.Identity(root);
        FileSystemWatcher? watcher = null;
        try
        {
            watcher = NewWatcher(root);
            watcher.IncludeSubdirectories = true;
            watcher.NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite;
            watcher.InternalBufferSize = 64 * 1024;
            watcher.Created += (_, e) => Heard(root, e.Name);
            watcher.Changed += (_, e) => Heard(root, e.Name);
            watcher.Deleted += (_, e) => Heard(root, e.Name);
            watcher.Renamed += (_, e) =>
            {
                Heard(root, e.OldName);
                Heard(root, e.Name);
            };
            var started = watcher;
            watcher.Error += (_, e) =>
            {
                try
                {
                    WatchError(root, started, starting, e.GetException());
                }
                catch (Exception ex)
                {
                    LogChangeFailed(ex, PathComparison.Printable(root));
                }
            };
            // Linux adds its watches right here, and reports what it can't add through the Error event, not by throwing.
            Enable(watcher);
            Exception? refused;
            lock (_lock)
            {
                refused = starting.Error;
            }

            if (refused is not null)
            {
                return (Refused(root, watcher, refused), starting);
            }

            var share = IsShare(root);
            LogWatching(PathComparison.Printable(root), share);
            return (new Watched(watcher, new FolderWatchStatus(share ? FolderWatchState.Share : FolderWatchState.Watching)) { Identity = identity, LookedAt = time.GetUtcNow() }, starting);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or PlatformNotSupportedException)
        {
            return (Refused(root, watcher, ex), starting);
        }
    }

    private Watched Refused(string root, FileSystemWatcher? watcher, Exception ex)
    {
        // The watcher goes, and with it the watches it did manage to add, which other folders may need.
        watcher?.Dispose();
        var unavailable = Unavailable(root, ex);
        LogCantWatch(ex, PathComparison.Printable(root), unavailable.Status.Problem ?? FolderWatchProblem.Failed);
        return unavailable;
    }

    /// <summary>What's left of a folder the system won't watch, and when it's tried again.</summary>
    private Watched Unavailable(string root, Exception ex)
    {
        var problem = IsWatchLimit(ex) ? FolderWatchProblem.Limit : Directory.Exists(root) ? FolderWatchProblem.Failed : FolderWatchProblem.Missing;
        return new Watched(null, new FolderWatchStatus(FolderWatchState.Unavailable, problem))
        {
            RetryAt = time.GetUtcNow() + (problem == FolderWatchProblem.Missing ? LookForMissingEvery : RetryRefusedAfter),
            LimitsWere = WatchLimits(),
        };
    }

    /// <summary>
    /// Linux's "The configured user limit (8192) on the number of inotify watches has been reached", or the same as an
    /// error code (ENOSPC, 28).
    /// </summary>
    private static bool IsWatchLimit(Exception ex) =>
        ex.Message.Contains("inotify", StringComparison.OrdinalIgnoreCase) || (ex.HResult & 0xFFFF) == 28;

    /// <summary>Linux's watch limits as they stand, or null elsewhere. Reading them doesn't touch a drive.</summary>
    internal static string? WatchLimits()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        try
        {
            return File.ReadAllText("/proc/sys/fs/inotify/max_user_watches") + File.ReadAllText("/proc/sys/fs/inotify/max_user_instances");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A folder inside the library that can't be read (a root-only <c>lost+found</c>): no scan can read it either.</summary>
    private static bool IsUnreadableSubfolder(string root, Exception ex)
    {
        if (ex is not UnauthorizedAccessException)
        {
            return false;
        }

        try
        {
            _ = Directory.EnumerateFileSystemEntries(root).Any();
            return true;
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return false; // The library folder itself.
        }
    }

    private void WatchError(string root, FileSystemWatcher watcher, Starting starting, Exception ex)
    {
        // The rest of the folder is still watched: the system carries on past a folder it can't add.
        if (IsUnreadableSubfolder(root, ex))
        {
            LogSubfolderSkipped(PathComparison.Printable(root));
            return;
        }

        lock (_lock)
        {
            if (!starting.Installed)
            {
                // Still starting: Start or the sync that called it decides what becomes of the folder.
                starting.Error ??= ex;
                return;
            }

            // Too many changes at once for the system to keep up (macOS says so with a plain IOException, once running).
            // The watch itself is fine, and starting it again would walk the tree: the folder is just scanned, when it's quiet.
            if (ex is InternalBufferOverflowException || (ex is IOException && OperatingSystem.IsMacOS() && !IsWatchLimit(ex)))
            {
                WholeFolderChanged(root);
                return;
            }
        }

        Failed(root, watcher, ex);
    }

    /// <summary>
    /// The watch broke (the drive went, the watch limit was reached): whatever changed can't
    /// be known, so once the next sync watches it afresh, the whole folder is scanned. The next sync disposes the watcher
    /// too: this runs on the watcher's own thread.
    /// </summary>
    private void Failed(string root, FileSystemWatcher watcher, Exception ex)
    {
        LogWatchFailed(ex, PathComparison.Printable(root));
        var unavailable = Unavailable(root, ex);
        lock (_lock)
        {
            if (_folders.TryGetValue(root, out var watched) && ReferenceEquals(watched.Watcher, watcher))
            {
                _folders[root] = unavailable;
            }

            _broken.Add(watcher);
            foreach (var key in _pending.Keys.Where(k => _pending[k].Root == root).ToList())
            {
                _pending.Remove(key);
            }
        }

        _syncSoon = true;
    }

    /// <summary>
    /// <see cref="Changed"/> on the watcher's own thread, where an exception nobody catches ends the server on macOS.
    /// </summary>
    private void Heard(string root, string? name)
    {
        try
        {
            Changed(root, name);
        }
        catch (Exception ex)
        {
            LogChangeFailed(ex, PathComparison.Printable(root));
        }
    }

    /// <summary>Something changed at <paramref name="name"/>, relative to the library folder <paramref name="root"/>.</summary>
    internal void Changed(string root, string? name)
    {
        if (string.IsNullOrEmpty(name) || Ignored(name))
        {
            return;
        }

        // The title's folder: the first one in the library folder. A file loose in the library folder is its own title.
        // By this system's separators only: on Linux and macOS a backslash can be part of a name.
        var segments = name.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], 2, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
        {
            return;
        }

        var target = Path.Combine(root, segments[0]);
        var artwork = IsArtworkOrNfo(name);
        // The title's own folder changing says nothing either way: it does whenever anything's added to it. But alone,
        // with no file heard of, it's a folder moved in whole, which is a new title like any other.
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var subtitle = extension is ".srt" or ".ass" or ".ssa" or ".vtt" or ".sub" or ".idx" or ".sup";
        var subtitles = subtitle || (extension.Length == 0 && segments.Length == 1);
        var now = time.GetUtcNow();
        lock (_lock)
        {
            if (!_folders.TryGetValue(root, out var watched) || watched.Watcher is null)
            {
                return;
            }

            if (_pending.TryGetValue(root, out var whole))
            {
                // The whole folder is being scanned: that looks at every title's artwork anyway (see below).
                whole.LastEvent = now;
                whole.Snapshot = null;
            }
            else if (_pending.TryGetValue(target, out var pending))
            {
                pending.LastEvent = now;
                pending.Snapshot = null;
                pending.ForceMetadata |= artwork;
                pending.OnlySubtitles &= subtitles;
                pending.HeardSubtitle |= subtitle;
            }
            else if (_pending.Values.Count(p => p.Root == root) >= MaxPendingPerFolder)
            {
                // A whole drive's worth arriving: one scan of the library folder, not hundreds of title scans.
                WholeFolderChanged(root);
            }
            else
            {
                _pending[target] = new Pending(root, now) { ForceMetadata = artwork, OnlySubtitles = subtitles, HeardSubtitle = subtitle };
            }
        }
    }

    /// <summary>One scan of the library folder, in place of any of its titles waiting. Under the lock.</summary>
    private void WholeFolderChanged(string root)
    {
        if (!_folders.TryGetValue(root, out var watched) || watched.Watcher is null)
        {
            return;
        }

        var first = _pending.TryGetValue(root, out var whole) ? whole.FirstEvent : time.GetUtcNow();
        foreach (var key in _pending.Keys.Where(k => _pending[k].Root == root).ToList())
        {
            _pending.Remove(key);
        }

        _pending[root] = new Pending(root, time.GetUtcNow(), first);
    }

    /// <summary>Queues the titles whose files have stopped changing.</summary>
    internal async Task FlushAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        List<(string Path, Pending Pending)> quiet;
        lock (_lock)
        {
            quiet = _pending.Where(p => Overdue(p.Value, now) || (now - p.Value.LastEvent >= QuietFor && (p.Value.Snapshot is null || now - p.Value.SnapshotAt >= SettleFor)))
                .Select(p => (p.Key, p.Value)).ToList();
        }

        var gone = new List<(string Path, Pending Pending)>();
        foreach (var (path, pending) in quiet)
        {
            // Not for a whole library folder, which would mean walking all of it (twice) to see if it had settled, and not
            // if it's waited long enough: then it's scanned as it is, and again once it stops.
            var overdue = Overdue(pending, now);
            var settled = overdue || path == pending.Root;
            // Read outside the lock: listing a big show's folder takes a moment, and events keep arriving meanwhile.
            var snapshot = settled ? null : Snapshot(path);
            var ready = false;
            lock (_lock)
            {
                if (!_pending.TryGetValue(path, out var current) || !ReferenceEquals(current, pending) || (pending.LastEvent > now && !overdue))
                {
                    continue; // Something changed while it was read: it waits again.
                }

                if (snapshot == Gone)
                {
                    _pending.Remove(path);
                    gone.Add((path, pending));
                    continue;
                }

                if (settled || (pending.Snapshot is { } before && before == snapshot))
                {
                    _pending.Remove(path);
                    ready = true;
                }
                else
                {
                    pending.Snapshot = snapshot;
                    pending.SnapshotAt = time.GetUtcNow();
                }
            }

            if (ready)
            {
                LogChanged(PathComparison.Printable(path));
                var whole = path == pending.Root;
                await queue.EnqueueAsync(new LibraryScanRequest(path, Reason, ForceMetadata: pending.ForceMetadata && !whole && MayForce(path), LastChange: pending.LastEvent, OnlySubtitles: pending.OnlySubtitles && pending.HeardSubtitle && !whole), ct);
            }
        }

        foreach (var titles in gone.GroupBy(g => g.Pending.Root))
        {
            await TitlesGoneAsync(titles.Key, titles.ToList(), ct);
        }
    }

    /// <summary>
    /// A title gone may be one film deleted or renamed, which is only worth a scan of that title, or the first sign of a
    /// drive going. Only a scan of the library folder holds a vanished drive's titles (see LibraryScanner's mass-removal
    /// guard), so that's what happens when the library folder has emptied or is unreadable, or when many have gone at once.
    /// </summary>
    private async Task TitlesGoneAsync(string root, List<(string Path, Pending Pending)> titles, CancellationToken ct)
    {
        // Read outside the lock, as the snapshots are: this is one folder's top level, not every title's.
        var rootGone = NothingLeftIn(root);
        var now = time.GetUtcNow();
        bool whole;
        lock (_lock)
        {
            if (!_goneAt.TryGetValue(root, out var times))
            {
                _goneAt[root] = times = [];
            }

            times.RemoveAll(t => now - t >= GoneWithin);
            times.AddRange(titles.Select(_ => now));
            whole = rootGone || times.Count >= ManyTitlesGone;
            if (whole)
            {
                if (_pending.TryGetValue(root, out var waiting))
                {
                    waiting.LastEvent = now;
                }
                else
                {
                    _pending[root] = new Pending(root, now);
                }
            }
        }

        if (whole)
        {
            return;
        }

        foreach (var (path, pending) in titles)
        {
            LogChanged(PathComparison.Printable(path));
            await queue.EnqueueAsync(new LibraryScanRequest(path, Reason, LastChange: pending.LastEvent), ct);
        }
    }

    /// <summary>The library folder isn't there, can't be read, or holds nothing a scan would care about: an unmounted drive's look.</summary>
    private static bool NothingLeftIn(string root)
    {
        try
        {
            return !Directory.EnumerateFileSystemEntries(root).Any(entry => !Ignored(Path.GetFileName(entry)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private bool MayForce(string path)
    {
        var now = time.GetUtcNow();
        lock (_lock)
        {
            foreach (var old in _forcedAt.Where(f => now - f.Value >= ForceAtMostEvery).Select(f => f.Key).ToList())
            {
                _forcedAt.Remove(old);
            }

            return _forcedAt.TryAdd(path, now);
        }
    }

    private const string Gone = "gone";

    private bool Overdue(Pending pending, DateTimeOffset now) => now - pending.FirstEvent >= WaitsAtMost;

    /// <summary>The sizes and change times of the files at <paramref name="path"/> (a folder or a file), or <see cref="Gone"/>.</summary>
    private static string Snapshot(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                return $"{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            }

            if (!Directory.Exists(path))
            {
                return Gone;
            }

            long count = 0, total = 0, newest = 0;
            var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.Hidden | FileAttributes.System };
            foreach (var file in new DirectoryInfo(path).EnumerateFiles("*", walk))
            {
                if (Ignored(file.Name))
                {
                    continue;
                }

                count++;
                total += file.Length;
                newest = Math.Max(newest, file.LastWriteTimeUtc.Ticks);
            }

            return $"{count}:{total}:{newest}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"unreadable:{ex.GetType().Name}";
        }
    }

    /// <summary>
    /// Files no scan cares about, or that aren't finished: hidden ones (<c>.DS_Store</c>, macOS's <c>._</c> files, a
    /// download's hidden temporary copy), downloads in progress, and what NAS systems keep in media folders.
    /// </summary>
    internal static bool Ignored(string name)
    {
        foreach (var segment in name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.StartsWith('.') || segment.EndsWith('~') || NotMedia.Contains(segment))
            {
                return true;
            }
        }

        return Path.GetExtension(name).ToLowerInvariant() is ".part" or ".partial" or ".tmp" or ".temp" or ".!qb" or ".crdownload" or ".download" or ".filepart";
    }

    /// <summary>What Windows and NAS systems keep in media folders. By name: a film can be called <c>#Alive</c> or <c>@Home</c>.</summary>
    private static readonly HashSet<string> NotMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        "Thumbs.db", "desktop.ini", "#recycle", "#snapshot", "@eaDir", "@tmp", "@Recycle", "@Recently-Snapshot", "@__thumb", "@SynoResource", "$RECYCLE.BIN", "System Volume Information", "lost+found",
    };

    /// <summary>
    /// A cover, poster or other picture kept beside the title, or its <c>.nfo</c>: the title's details are read afresh.
    /// Any scan notices a replaced picture by its size and change time (<c>LibraryScanner.NoticeReplacedArtworkAsync</c>),
    /// and a title's own scan re-reads its <c>.nfo</c>; forcing it also catches a cover added to an album that had none.
    /// Never for a whole library folder, where it would look every title up again.
    /// </summary>
    internal static bool IsArtworkOrNfo(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (extension == ".nfo")
        {
            return true;
        }

        if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp"))
        {
            return false;
        }

        var stem = Path.GetFileNameWithoutExtension(name).ToLowerInvariant();
        var last = stem.Contains('-') ? stem[(stem.LastIndexOf('-') + 1)..] : stem;
        return last is "cover" or "folder" or "poster" or "fanart" or "backdrop" or "background" or "banner" or "logo" or "clearlogo" or "landscape"
            or "thumb" or "clearart" or "disc" or "discart" or "keyart" or "front" or "album" or "artist" or "author";
    }

    private sealed record Watched(FileSystemWatcher? Watcher, FolderWatchStatus Status)
    {
        /// <summary>Not tried again before then (see <see cref="RetryRefusedAfter"/>).</summary>
        public DateTimeOffset? RetryAt { get; init; }

        /// <summary>What <see cref="WatchLimits"/> said when the system refused: if it's changed, it's worth another try.</summary>
        public string? LimitsWere { get; init; }

        /// <summary>The folder this watcher is on (see <see cref="FolderKind.Identity"/>).</summary>
        public string? Identity { get; init; }

        /// <summary>When that was last checked (see <see cref="LookAtSharesEvery"/>).</summary>
        public DateTimeOffset LookedAt { get; init; }
    }

    /// <summary>A watcher on its way to being in <see cref="_folders"/>, which errors can't reach until it is. Under the lock.</summary>
    private sealed class Starting
    {
        public bool Installed { get; set; }
        public Exception? Error { get; set; }
    }

    private sealed class Pending(string root, DateTimeOffset lastEvent, DateTimeOffset? firstEvent = null)
    {
        public string Root { get; } = root;
        public DateTimeOffset FirstEvent { get; } = firstEvent ?? lastEvent;
        public DateTimeOffset LastEvent { get; set; } = lastEvent;
        public string? Snapshot { get; set; }
        public DateTimeOffset SnapshotAt { get; set; }
        public bool ForceMetadata { get; set; }
        public bool OnlySubtitles { get; set; }
        public bool HeardSubtitle { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching {Path} for changes{Share}")]
    private partial void LogWatchingCore(string path, string share);

    private void LogWatching(string path, bool share) =>
        LogWatchingCore(path, share ? " (a network share: changes made on other machines may not be noticed)" : "");

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching {Path} again; scanning it for anything that changed meanwhile")]
    private partial void LogWatchingAgain(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Can't watch {Path} for changes ({Problem}); it's scanned every 12 hours instead")]
    private partial void LogCantWatch(Exception exception, string path, FolderWatchProblem problem);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lost track of changes in {Path}; scanning it")]
    private partial void LogWatchFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "A folder in {Path} can't be read, so it isn't watched; the rest is")]
    private partial void LogSubfolderSkipped(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't take note of a change in {Path}")]
    private partial void LogChangeFailed(Exception exception, string path);

    [LoggerMessage(Level = LogLevel.Debug, Message = "{Path} changed")]
    private partial void LogChanged(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't queue the folders that changed")]
    private partial void LogFlushFailed(Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't read the library folders to watch")]
    private partial void LogSyncFailed(Exception exception);
}
