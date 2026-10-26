using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.LiveTv;
using Flexarr.Core.Scanning;
using Flexarr.Streaming.Live;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.LiveTv;

/// <summary>Where recordings go.</summary>
public static partial class DvrFolders
{
    /// <summary>
    /// The folder chosen in Settings; else a DVR folder at the top of the drive the TV library is on
    /// ("/Volumes/Media/DVR" for a TV library at "/Volumes/Media/data/tv"), or beside it when it isn't on a mounted
    /// drive; else one in the data directory.
    /// </summary>
    public static string Resolve(LiveTvOptions? live, string dataDirectory, IEnumerable<string> tvFolders)
    {
        ArgumentNullException.ThrowIfNull(tvFolders);
        if (live?.DvrFolder is { Length: > 0 } chosen)
        {
            return Path.GetFullPath(chosen);
        }

        if (tvFolders.FirstOrDefault() is { } tv)
        {
            var full = Path.GetFullPath(tv).TrimEnd(Path.DirectorySeparatorChar);
            if (Volume().Match(full) is { Success: true } volume)
            {
                return Path.Combine(volume.Value, "DVR");
            }

            if (Path.GetDirectoryName(full) is { Length: > 1 } parent)
            {
                return Path.Combine(parent, "DVR");
            }
        }

        return Path.GetFullPath(Path.Combine(dataDirectory, "dvr"));
    }

    public static async Task<string> ResolveAsync(FlexarrDbContext db, FlexarrOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        // The TV libraries other than the recordings' own (its folder is a DVR folder).
        var folders = await db.Libraries.AsNoTracking().IgnoreQueryFilters().Where(l => l.Type == LibraryType.TvShows)
            .OrderBy(l => l.CreatedAt).SelectMany(l => l.Folders.Select(f => f.Path)).ToListAsync(ct);
        return Resolve(options.LiveTv, options.DataDirectory, folders.Where(f => !IsDvrFolder(f)));
    }

    private static bool IsDvrFolder(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is var name && (name.Equals("DVR", StringComparison.OrdinalIgnoreCase) || name == "dvr");

    [GeneratedRegex(@"^/Volumes/[^/]+")]
    private static partial Regex Volume();
}

/// <summary>A recording under way: what it's of, until when, and how to stop it.</summary>
public sealed record ActiveRecording(Guid Id, Guid ChannelId, string ChannelName, string Title, DateTime Until, CancellationTokenSource Stop)
{
    public Task Done { get; set; } = Task.CompletedTask;
}

/// <summary>Wakes the scheduler: a recording was added or changed, or the guide was refreshed.</summary>
public sealed class DvrSignal
{
    private readonly System.Threading.Channels.Channel<bool> _asked = System.Threading.Channels.Channel.CreateBounded<bool>(
        new System.Threading.Channels.BoundedChannelOptions(1) { FullMode = System.Threading.Channels.BoundedChannelFullMode.DropWrite });

    public void Request() => _asked.Writer.TryWrite(true);

    internal async Task<bool> WaitAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            return await _asked.Reader.ReadAsync(wait.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false;
        }
    }
}

/// <summary>
/// The DVR: the streams there are (what Dispatcharr says the login allows, unless Settings says otherwise), which
/// channels are open (live and recording; Dispatcharr shares one channel's stream among everyone on it), making room for
/// a recording by stopping live viewing of other channels, and running the recordings.
/// </summary>
public sealed partial class DvrService(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, LiveStreams streams, LiveTvViewers viewers,
    ILibraryScanQueue scans, TimeProvider clock, ILogger<DvrService> logger) : IAsyncDisposable
{
    private static readonly TimeSpan ReportKeptFor = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<Guid, ActiveRecording> _active = new();
    private int? _reported;
    private DateTime _reportedAt = DateTime.MinValue;
    private volatile bool _shuttingDown;

    public IReadOnlyCollection<ActiveRecording> Active => [.. _active.Values];

    public TimeSpan Before => TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.LiveTv?.PaddingBeforeMinutes ?? 1, 0, 30));

    public TimeSpan After => TimeSpan.FromMinutes(Math.Clamp(options.CurrentValue.LiveTv?.PaddingAfterMinutes ?? 5, 0, 60));

    /// <summary>How many channels can be open at once: Settings' number, else what Dispatcharr reports (checked hourly), else one.</summary>
    public async Task<int> StreamsAsync(CancellationToken ct)
    {
        var live = options.CurrentValue.LiveTv;
        if (live?.MaxStreams is > 0 and var chosen)
        {
            return chosen;
        }

        if (live?.Account is { } account && clock.GetUtcNow().UtcDateTime - _reportedAt > ReportKeptFor)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var info = await scope.ServiceProvider.GetRequiredService<XtreamClient>().CheckAsync(account, ct);
                _reported = info.MaxConnections is > 0 and var reported ? reported : null;
            }
            catch (Exception ex) when (ex is HttpRequestException or XtreamException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // Dispatcharr can't be reached: the last answer (or one) will do.
            }

            _reportedAt = clock.GetUtcNow().UtcDateTime;
        }

        return _reported ?? 1;
    }

    /// <summary>The channels open now, live or recording.</summary>
    public HashSet<Guid> ChannelsInUse()
    {
        var open = _active.Values.Select(a => a.ChannelId).ToHashSet();
        foreach (var session in streams.All)
        {
            if (session.ChannelKey.Length >= 32 && Guid.TryParseExact(session.ChannelKey[..32], "N", out var channel))
            {
                open.Add(channel);
            }
        }

        return open;
    }

    /// <summary>
    /// The recordings keeping <paramref name="channelId"/> from being watched: every stream is in use and recordings
    /// hold them. Empty when it can be watched (already open, a stream free, or live viewing is what's in the way,
    /// which changing channel deals with).
    /// </summary>
    public async Task<List<ActiveRecording>> BlockingAsync(Guid channelId, CancellationToken ct)
    {
        var recording = _active.Values.Select(a => a.ChannelId).ToHashSet();
        if (recording.Contains(channelId) || recording.Count < await StreamsAsync(ct))
        {
            return [];
        }

        return [.. _active.Values.OrderBy(a => a.Until)];
    }

    /// <summary>Stops live viewing of other channels until a recording on <paramref name="channelId"/> fits (recording wins).</summary>
    public async Task MakeRoomAsync(Guid channelId, string title, CancellationToken ct)
    {
        var limit = await StreamsAsync(ct);
        var recording = _active.Values.Select(a => a.ChannelId).Append(channelId).ToHashSet();
        var live = streams.All.Where(s => s.ChannelKey.Length >= 32 && Guid.TryParseExact(s.ChannelKey[..32], "N", out var c) && !recording.Contains(c))
            .GroupBy(s => s.ChannelKey[..32]).ToList();
        var over = recording.Count + live.Count - limit;
        foreach (var channel in live.Take(Math.Max(0, over)))
        {
            foreach (var session in channel)
            {
                foreach (var viewer in viewers.All.Where(v => v.Session == session))
                {
                    viewers.End(viewer.Id, $"Recording {title} needed the stream. You can watch that channel, or stop the recording.");
                }

                await streams.EndAsync(session);
                LogMadeRoom(session.ChannelKey, title);
            }
        }
    }

    /// <summary>Starts recording, unless it's already going.</summary>
    public void Start(Recording recording, Guid channelId)
    {
        ArgumentNullException.ThrowIfNull(recording);
        var until = recording.End + After;
        var active = new ActiveRecording(recording.Id, channelId, recording.ChannelName, recording.Title, until, new CancellationTokenSource());
        if (!_active.TryAdd(recording.Id, active))
        {
            active.Stop.Dispose();
            return;
        }

        active.Done = Task.Run(() => RunAsync(recording.Id, active));
    }

    /// <summary>Stops a recording early, keeping what it has.</summary>
    public bool Stop(Guid id)
    {
        if (!_active.TryGetValue(id, out var active))
        {
            return false;
        }

        active.Stop.Cancel();
        return true;
    }

    private async Task RunAsync(Guid id, ActiveRecording active)
    {
        try
        {
            await RecordAsync(id, active);
        }
        catch (Exception ex) when (!_shuttingDown)
        {
            LogRecordingFailed(ex, active.Title);
            await FinishAsync(id, RecordingState.Failed, "Something went wrong recording it.", null, null);
        }
        finally
        {
            _active.TryRemove(id, out _);
            active.Stop.Dispose();
        }
    }

    private async Task RecordAsync(Guid id, ActiveRecording active)
    {
        var settings = options.CurrentValue;
        string folder;
        Recording recording;
        LiveSource? source = null;
        await using (var scope = scopes.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            recording = await db.Recordings.FirstAsync(r => r.Id == id);
            folder = await DvrFolders.ResolveAsync(db, settings, CancellationToken.None);
            var channel = await db.LiveChannels.AsNoTracking().FirstOrDefaultAsync(c => c.Id == recording.ChannelId);
            if (channel is not null && settings.LiveTv?.Account is { } account)
            {
                source = account.StreamOf(channel.SourceId);
            }

            recording.State = RecordingState.Recording;
            recording.StartedAt ??= clock.GetUtcNow().UtcDateTime;
            recording.Reason = null;
            await db.SaveChangesAsync();
        }

        if (source is null)
        {
            await FinishAsync(id, RecordingState.Failed, "The channel isn't in Dispatcharr any more.", null, null);
            return;
        }

        LogStarted(recording.Title, recording.ChannelName, active.Until);
        var work = Path.Combine(folder, ".recording", id.ToString("N"));
        var result = await LiveRecorder.RecordAsync(settings.FfmpegPath, source, work, active.Until, clock, logger, active.Stop.Token);
        if (_shuttingDown)
        {
            // The parts stay, and it's still Recording: after the restart it carries on (or, if it's over by then, the
            // parts are joined into the file).
            return;
        }

        var stopped = active.Stop.IsCancellationRequested;
        if (result.Parts.Count == 0)
        {
            TryDelete(work);
            await FinishAsync(id, RecordingState.Failed, result.Error ?? "Nothing was recorded.", null, null);
            return;
        }

        var target = Unique(Path.Combine(folder, RecordingNames.RelativePath(recording, TimeZoneInfo.Local)));
        var error = await LiveRecorder.RemuxAsync(settings.FfmpegPath, result.Parts, target, CancellationToken.None);
        if (error is not null)
        {
            await FinishAsync(id, RecordingState.Failed, error, null, null);
            return;
        }

        TryDelete(work);
        var note = stopped ? "Stopped early." : result.Error is null ? null : "The stream dropped for a moment, so a little may be missing.";
        await FinishAsync(id, RecordingState.Completed, note, target, new FileInfo(target).Length);
        await EnsureLibraryAsync(folder);
        await scans.EnqueueAsync(new LibraryScanRequest(target, "Recorded"));
        LogFinished(recording.Title, target);
        await PruneAsync(recording.RuleId);
    }

    /// <summary>Recordings appear in a Recordings library, made when the first one is done, with the DVR folder in it.</summary>
    private async Task EnsureLibraryAsync(string folder)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var full = Path.GetFullPath(folder);
        if (await db.LibraryFolders.AnyAsync(f => f.Path == full))
        {
            return;
        }

        // A DVR folder moved in Settings joins the Recordings library, so earlier recordings stay where they are.
        var library = await db.Libraries.Include(l => l.Folders)
            .FirstOrDefaultAsync(l => l.Type == LibraryType.TvShows && l.Name == "Recordings");
        if (library is null)
        {
            db.Libraries.Add(new Library { Name = "Recordings", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = full }] });
        }
        else
        {
            library.Folders.Add(new LibraryFolder { Path = full });
        }

        await db.SaveChangesAsync();
    }

    /// <summary>A series that keeps only its latest few: older recordings (the earliest first) are deleted.</summary>
    private async Task PruneAsync(Guid? ruleId)
    {
        if (ruleId is null)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        if (await db.RecordingRules.AsNoTracking().FirstOrDefaultAsync(r => r.Id == ruleId) is not { KeepLatest: > 0 } rule)
        {
            return;
        }

        var old = await db.Recordings.Where(r => r.RuleId == ruleId && r.State == RecordingState.Completed)
            .OrderByDescending(r => r.Start).Skip(rule.KeepLatest).ToListAsync();
        foreach (var recording in old)
        {
            if (recording.FilePath is { } path && File.Exists(path))
            {
                File.Delete(path);
                await scans.EnqueueAsync(new LibraryScanRequest(path, "Recording deleted", Deleted: true));
            }

            db.Recordings.Remove(recording);
        }

        await db.SaveChangesAsync();
    }

    private async Task FinishAsync(Guid id, RecordingState state, string? reason, string? path, long? size)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        if (await db.Recordings.FirstOrDefaultAsync(r => r.Id == id) is not { } recording)
        {
            return;
        }

        recording.State = state;
        recording.Reason = reason;
        recording.FilePath = path ?? recording.FilePath;
        recording.Size = size ?? recording.Size;
        recording.FinishedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();
    }

    /// <summary>The path, or with " (2)" (and so on) when something's there already: a repeat recorded twice.</summary>
    private static string Unique(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var stem = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path));
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){Path.GetExtension(path)}";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void TryDelete(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (IOException ex)
        {
            LogCleanupFailed(directory, ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            LogCleanupFailed(directory, ex.Message);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Shutting down: each recording keeps what it has, and carries on after the restart if its programme's still on.
        _shuttingDown = true;
        foreach (var active in _active.Values)
        {
            active.Stop.Cancel();
        }

        await Task.WhenAll(_active.Values.Select(a => a.Done));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Recording {Title} on {Channel} until {Until:HH:mm} UTC")]
    private partial void LogStarted(string title, string channel, DateTime until);

    [LoggerMessage(Level = LogLevel.Information, Message = "Recorded {Title} to {Path}")]
    private partial void LogFinished(string title, string path);

    [LoggerMessage(Level = LogLevel.Information, Message = "Stopped live channel {Channel}: {Title} needed the stream")]
    private partial void LogMadeRoom(string channel, string title);

    [LoggerMessage(Level = LogLevel.Error, Message = "Recording {Title} failed")]
    private partial void LogRecordingFailed(Exception exception, string title);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tidy away {Directory}: {Reason}")]
    private partial void LogCleanupFailed(string directory, string reason);
}

/// <summary>
/// Every few seconds: series turned into scheduled recordings (when the guide changes, and every few minutes), and
/// recordings started when due (unless they clash, or the drive's nearly full) and noted when they were missed.
/// After a restart, recordings that were under way carry on.
/// </summary>
public sealed partial class DvrScheduler(IServiceScopeFactory scopes, DvrService dvr, DvrSignal signal, IOptionsMonitor<FlexarrOptions> options,
    TimeProvider clock, ILogger<DvrScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan PlanEvery = TimeSpan.FromMinutes(10);

    /// <summary>How far ahead series are scheduled (about as far as guides go).</summary>
    private static readonly TimeSpan Ahead = TimeSpan.FromDays(8);

    private DateTime _plannedAt = DateTime.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var asked = false;
            try
            {
                if (options.CurrentValue.LiveTv?.Account is not null)
                {
                    await RunOnceAsync(stoppingToken);
                }
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }

            asked = await signal.WaitAsync(Tick, stoppingToken);
            if (asked)
            {
                _plannedAt = DateTime.MinValue;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        if (now - _plannedAt > PlanEvery)
        {
            await PlanAsync(db, now, ct);
            _plannedAt = now;
        }

        var (before, after) = (dvr.Before, dvr.After);
        var active = await db.Recordings.Where(r => r.State == RecordingState.Scheduled || r.State == RecordingState.Recording).ToListAsync(ct);
        var running = dvr.Active.Select(a => a.Id).ToHashSet();
        var clashing = DvrPlanner.Clashing(active.Where(r => r.End + after > now), await dvr.StreamsAsync(ct), before, after);
        foreach (var recording in active.OrderBy(r => r.Start))
        {
            if (running.Contains(recording.Id))
            {
                continue;
            }

            // Under way when Flexarr stopped: carried on, or (if it's over) what was recorded is saved.
            if (recording.State == RecordingState.Recording)
            {
                dvr.Start(recording, recording.ChannelId);
                running.Add(recording.Id);
                continue;
            }

            if (recording.End + after <= now)
            {
                // Its time went by without it.
                recording.State = RecordingState.Failed;
                recording.Reason ??= "Flexarr wasn't running when it was on.";
                recording.FinishedAt = now;
                continue;
            }

            if (recording.Start - before > now)
            {
                continue;
            }

            if (clashing.Contains(recording.Id))
            {
                recording.State = RecordingState.Skipped;
                recording.Reason = "Another recording was using the stream.";
                recording.FinishedAt = now;
                continue;
            }

            var folder = await DvrFolders.ResolveAsync(db, options.CurrentValue, ct);
            if (SpaceProblem(folder) is { } problem)
            {
                recording.State = RecordingState.Failed;
                recording.Reason = problem;
                recording.FinishedAt = now;
                LogNoSpace(recording.Title, problem);
                continue;
            }

            await dvr.MakeRoomAsync(recording.ChannelId, recording.Title, ct);
            dvr.Start(recording, recording.ChannelId);
            running.Add(recording.Id);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Each series' showings over the coming days, scheduled.</summary>
    private async Task PlanAsync(FlexarrDbContext db, DateTime now, CancellationToken ct)
    {
        var rules = await db.RecordingRules.AsNoTracking().ToListAsync(ct);
        if (rules.Count == 0)
        {
            return;
        }

        var titles = rules.Select(r => r.Title.Trim().ToUpperInvariant()).ToHashSet();
        var until = now + Ahead;
        var programmes = await db.LiveProgrammes.AsNoTracking().Where(p => p.End > now && p.Start < until).ToListAsync(ct);
        var channels = await db.LiveChannels.AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        var guide = programmes.Where(p => titles.Contains(p.Title.Trim().ToUpperInvariant()) && channels.ContainsKey(p.ChannelId))
            .Select(p => new GuideShowing(p, channels[p.ChannelId])).ToList();
        var existing = await db.Recordings.AsNoTracking().Where(r => r.RuleId != null || r.End > now).ToListAsync(ct);
        var added = DvrPlanner.Expand(rules, guide, existing, now);
        if (added.Count > 0)
        {
            db.Recordings.AddRange(added);
            await db.SaveChangesAsync(ct);
            LogPlanned(added.Count);
        }
    }

    /// <summary>Why a recording can't start in the folder, or null: it can't be made, or the drive's below the floor.</summary>
    private string? SpaceProblem(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var free = new DriveInfo(folder).AvailableFreeSpace;
            var floor = (long)Math.Max(0, options.CurrentValue.LiveTv?.MinFreeGigabytes ?? 100) * 1_000_000_000;
            return free < floor ? $"Less than {floor / 1_000_000_000} GB free on the recordings drive." : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return $"The recordings folder {folder} can't be used (is the drive connected?).";
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Scheduled {Count} recordings for series")]
    private partial void LogPlanned(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Didn't record {Title}: {Reason}")]
    private partial void LogNoSpace(string title, string reason);

    [LoggerMessage(Level = LogLevel.Error, Message = "The DVR's schedule couldn't be run")]
    private partial void LogFailed(Exception exception);
}
