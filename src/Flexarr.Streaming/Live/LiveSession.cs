using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Flexarr.Core.Localization;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming.Live;

/// <summary>
/// One channel being watched: an ffmpeg turning its stream into a live HLS playlist that keeps the last
/// <see cref="Rewind"/>, shared by everyone watching it, so a household on one channel takes one of the provider's
/// connections. It runs while anyone is watching. When the source drops, ffmpeg is started again on the same playlist
/// (marked as a discontinuity) a few times before giving up. Never paused with SIGSTOP (see TranscodeSession).
/// </summary>
public sealed partial class LiveSession : IAsyncDisposable
{
    public const string PlaylistName = "live.m3u8";

    /// <summary>Restarts after the source drops before the session gives up and says why.</summary>
    private const int MaxRestarts = 5;

    private readonly LiveSource _source;
    private readonly TranscodeSettings _settings;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, DateTime> _viewers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private Process? _process;
    private int _restarts;
    private volatile bool _disposed;
    private string? _lastError;

    public LiveSession(string channelKey, LiveSource source, LivePlan plan, TimeSpan rewind, TranscodeSettings settings, ILogger logger)
    {
        ChannelKey = channelKey;
        _source = source;
        Plan = plan;
        Rewind = rewind;
        _settings = settings;
        _logger = logger;
        Directory = System.IO.Directory.CreateDirectory(Path.Combine(settings.CacheDirectory, "live", TranscodeManager.NewId())).FullName;
    }

    /// <summary>Which channel this is (the manager's key for it).</summary>
    public string ChannelKey { get; }

    public LivePlan Plan { get; }
    public TimeSpan Rewind { get; }
    public string Directory { get; }
    public DateTime StartedAt { get; } = DateTime.UtcNow;

    /// <summary>When the last viewer left (or the session started, before anyone joined); null while someone watches.</summary>
    public DateTime? EmptySince { get; private set; } = DateTime.UtcNow;

    /// <summary>The source dropped more often than it's worth retrying; <see cref="Error"/> says why.</summary>
    public bool Failed { get; private set; }

    /// <summary>
    /// ffmpeg stopped while nobody was watching, so it wasn't started again: what's on disk is a stream that will never
    /// move on. The next viewer gets a fresh session instead.
    /// </summary>
    public bool Ended { get; private set; }

    /// <summary>Why the channel stopped or wouldn't start, in words for the viewer.</summary>
    public string? Error => _lastError is { } e ? LiveStreamException.Explain(e) : null;

    public int ViewerCount => _viewers.Count;

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            StartProcess(resume: false);
        }
    }

    private void StartProcess(bool resume)
    {
        var start = new ProcessStartInfo(_settings.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in LiveCommand.Hls(_source, Plan, Directory, _settings, Rewind, resume))
        {
            start.ArgumentList.Add(arg);
        }

        var process = Process.Start(start) ?? throw new LiveStreamException($"Could not start {_settings.FfmpegPath}");
        var startedAt = DateTime.UtcNow;
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastError = _source.Redact(e.Data);
            }
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => OnExited(process, startedAt);
        process.BeginErrorReadLine();
        _process = process;
        using (Loc.Use(null))
        {
            LogStarted(ChannelKey, Plan.Describe(), resume); // the log stays English
        }
    }

    private void OnExited(Process process, DateTime startedAt)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(process, _process))
            {
                return;
            }

            int code;
            try
            {
                code = process.ExitCode;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            LogExited(ChannelKey, code, _lastError ?? "no message");

            // A run that played a while earns its retries back; one that fails straight away counts against them.
            if (DateTime.UtcNow - startedAt > TimeSpan.FromMinutes(2))
            {
                _restarts = 0;
            }

            if (++_restarts > MaxRestarts || !File.Exists(Path.Combine(Directory, PlaylistName)))
            {
                // Never got going (refused, no connection free), or keeps dropping: say why rather than spin.
                Failed = true;
                return;
            }

            _ = RestartSoonAsync();
        }
    }

    private async Task RestartSoonAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(Math.Min(10, 2 * _restarts))).ConfigureAwait(false);
        lock (_gate)
        {
            if (!_disposed && ViewerCount > 0)
            {
                try
                {
                    StartProcess(resume: true);
                }
                catch (Exception ex) when (ex is LiveStreamException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _lastError = ex.Message;
                    Failed = true;
                }
            }
            else
            {
                Ended = true;
            }
        }
    }

    /// <summary>
    /// Waits until the playlist has <paramref name="segments"/> segments, enough for a player to start smoothly.
    /// Throws, saying why, when ffmpeg gives up first.
    /// </summary>
    public async Task WaitUntilPlayableAsync(int segments, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (Failed)
            {
                throw _lastError is { } failure ? LiveStreamException.FromError(failure) : new LiveStreamException(Loc.T("The channel couldn’t be played."));
            }

            if (ReadPlaylist() is { } playlist && SegmentCount(playlist) >= segments)
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw _lastError is { } error ? LiveStreamException.FromError(error) : new LiveStreamException(Loc.T("The channel didn’t start sending in time."));
    }

    /// <summary>
    /// The most a channel's rewind window keeps on disk: about four hours of HD. A 4K channel copied as it comes keeps
    /// less time than <see cref="Rewind"/> asks, rather than tens of gigabytes; so does any channel once the cache's disk
    /// is down to <see cref="TranscodeManager.MinFreeBytes"/>.
    /// </summary>
    public const long MaxRewindBytes = 8L << 30;

    /// <summary>Segments always kept, however short of room: enough to go on playing.</summary>
    private const int SegmentsAlwaysKept = 3;

    /// <summary>This channel's most on disk (tests set it lower).</summary>
    internal long RewindBudget { get; set; } = MaxRewindBytes;

    /// <summary>Free bytes on the disk holding a folder; null when that can't be told (only the budget applies then).</summary>
    internal Func<string, long?> FreeBytes { get; set; } = FreeBytesOn;

    private static long? FreeBytesOn(string folder)
    {
        try
        {
            return new DriveInfo(folder).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Segments past the budget are dropped up to and including this number: no longer listed or served.</summary>
    private volatile int _droppedThrough = -1;

    /// <summary>The size of each segment the playlist lists, by number, measured once.</summary>
    private readonly Dictionary<int, long> _sizes = [];

    /// <summary>
    /// Keeps the rewind window within <see cref="RewindBudget"/>, and within what the disk can spare: the oldest
    /// segments over it are dropped (emptied, so ffmpeg still finds them to delete in its turn, and left out of the
    /// playlist). Called on each sweep. Returns how many segments it dropped.
    /// </summary>
    public int KeepWithinBudget()
    {
        if (ReadPlaylistFile() is not { } playlist)
        {
            return 0;
        }

        var listed = SegmentNumbers(playlist);
        lock (_sizes)
        {
            if (listed.Count > 0 && listed[^1] <= _droppedThrough)
            {
                _droppedThrough = -1; // numbered afresh (it never should be): nothing of the new run was dropped
            }

            var current = listed.ToHashSet();
            foreach (var gone in _sizes.Keys.Where(n => !current.Contains(n)).ToList())
            {
                _sizes.Remove(gone);
            }

            foreach (var number in listed.Where(n => n > _droppedThrough && !_sizes.ContainsKey(n)))
            {
                try
                {
                    _sizes[number] = new FileInfo(Path.Combine(Directory, $"seg{number}.ts")).Length;
                }
                catch (IOException)
                {
                    // Being renamed into place, or gone: measured next time.
                }
            }

            var kept = listed.Where(n => n > _droppedThrough && _sizes.ContainsKey(n)).ToList();
            var used = kept.Sum(n => _sizes[n]);
            var allowed = RewindBudget;
            if (FreeBytes(Directory) is { } free)
            {
                allowed = Math.Min(allowed, used + free - TranscodeManager.MinFreeBytes);
            }

            var dropped = 0;
            foreach (var number in kept.Take(Math.Max(0, kept.Count - SegmentsAlwaysKept)))
            {
                if (used <= allowed)
                {
                    break;
                }

                used -= _sizes[number];
                _sizes.Remove(number);
                _droppedThrough = number;
                dropped++;
                try
                {
                    using (new FileStream(Path.Combine(Directory, $"seg{number}.ts"), FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                    {
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Deleted by ffmpeg meanwhile, or being read: either way it's out of the playlist now.
                }
            }

            if (dropped > 0)
            {
                LogTrimmed(ChannelKey, dropped, used >> 20);
            }

            return dropped;
        }
    }

    /// <summary>The numbers of the segments a playlist lists, oldest first.</summary>
    internal static List<int> SegmentNumbers(string playlist) =>
        [.. playlist.Split('\n').Select(l => l.Trim()).Where(l => SegmentName().IsMatch(l)).Select(Number)];

    private static int Number(string segment) => int.Parse(segment.AsSpan(3, segment.Length - 6), System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// <paramref name="playlist"/> without the segments numbered up to <paramref name="through"/> (always keeping the
    /// last <see cref="SegmentsAlwaysKept"/>): their tags go with them, and the media and discontinuity sequences move
    /// on by what was left out, as players expect of a live window that slides.
    /// </summary>
    internal static string WithoutDropped(string playlist, int through)
    {
        if (through < 0)
        {
            return playlist;
        }

        var lines = playlist.Split('\n');
        var header = new List<string>();
        var groups = new List<List<string>>();
        var group = new List<string>();
        var inSegments = false;
        foreach (var line in lines)
        {
            var text = line.TrimEnd('\r');
            if (!inSegments && (text.StartsWith("#EXTINF", StringComparison.Ordinal) || text.StartsWith("#EXT-X-PROGRAM-DATE-TIME", StringComparison.Ordinal)
                || text.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !text.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal)))
            {
                inSegments = true;
            }

            if (!inSegments)
            {
                header.Add(line);
                continue;
            }

            group.Add(line);
            if (text.Length > 0 && !text.StartsWith('#'))
            {
                groups.Add(group);
                group = [];
            }
        }

        var droppable = Math.Max(0, groups.Count - SegmentsAlwaysKept);
        var drop = 0;
        while (drop < droppable && groups[drop][^1].Trim() is var uri && SegmentName().IsMatch(uri) && Number(uri) <= through)
        {
            drop++;
        }

        if (drop == 0)
        {
            return playlist;
        }

        var discontinuities = groups.Take(drop).Count(g => g.Any(l => l.TrimEnd('\r') == "#EXT-X-DISCONTINUITY"));
        var media = header.FindIndex(l => l.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal));
        if (media >= 0 && long.TryParse(header[media].AsSpan("#EXT-X-MEDIA-SEQUENCE:".Length).Trim(), out var sequence))
        {
            header[media] = $"#EXT-X-MEDIA-SEQUENCE:{sequence + drop}";
        }

        if (discontinuities > 0)
        {
            var at = header.FindIndex(l => l.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.Ordinal));
            if (at >= 0 && long.TryParse(header[at].AsSpan("#EXT-X-DISCONTINUITY-SEQUENCE:".Length).Trim(), out var d))
            {
                header[at] = $"#EXT-X-DISCONTINUITY-SEQUENCE:{d + discontinuities}";
            }
            else
            {
                header.Insert(media >= 0 ? media + 1 : header.Count, $"#EXT-X-DISCONTINUITY-SEQUENCE:{discontinuities}");
            }
        }

        return string.Join('\n', header.Concat(groups.Skip(drop).SelectMany(g => g)).Concat(group));
    }

    /// <summary>
    /// The playlist players are given: ffmpeg's, less the segments dropped to keep within the budget
    /// (<see cref="KeepWithinBudget"/>).
    /// </summary>
    public string? ReadPlaylist() => ReadPlaylistFile() is { } playlist ? WithoutDropped(playlist, _droppedThrough) : null;

    /// <summary>ffmpeg's playlist as it stands; it writes a new one beside it and renames, so a read never sees half.</summary>
    private string? ReadPlaylistFile()
    {
        var path = Path.Combine(Directory, PlaylistName);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException)
            {
                Thread.Sleep(20);
            }
        }

        return null;
    }

    /// <summary>A segment's file, by the name the playlist gives it; null for any other name or one that's gone.</summary>
    public string? SegmentPath(string name)
    {
        if (!SegmentName().IsMatch(name))
        {
            return null;
        }

        var path = Path.Combine(Directory, name);
        return Number(name) > _droppedThrough && File.Exists(path) ? path : null;
    }

    internal static int SegmentCount(string playlist) => playlist.Split('\n').Count(l => l.StartsWith("#EXTINF", StringComparison.Ordinal));

    public void Join(string viewer)
    {
        _viewers[viewer] = DateTime.UtcNow;
        EmptySince = null;
    }

    /// <summary>A viewer is still there (it fetched something, or its player said so).</summary>
    public void Touch(string viewer)
    {
        if (_viewers.ContainsKey(viewer))
        {
            _viewers[viewer] = DateTime.UtcNow;
        }
    }

    public void Leave(string viewer)
    {
        _viewers.TryRemove(viewer, out _);
        if (_viewers.IsEmpty)
        {
            EmptySince ??= DateTime.UtcNow;
        }
    }

    /// <summary>Lets go of viewers not heard from within <paramref name="idle"/>; returns who they were.</summary>
    public IReadOnlyList<string> DropIdle(TimeSpan idle)
    {
        var cutoff = DateTime.UtcNow - idle;
        var gone = _viewers.Where(v => v.Value < cutoff).Select(v => v.Key).ToList();
        foreach (var viewer in gone)
        {
            Leave(viewer);
        }

        return gone;
    }

    public async ValueTask DisposeAsync()
    {
        Process? process;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            process = _process;
        }

        if (process is not null)
        {
            ProcessSignals.KillTree(process);
            try
            {
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Killed; it'll go.
            }

            process.Dispose();
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Cleared with the rest of the cache later.
        }

        LogStopped(ChannelKey);
    }

    [GeneratedRegex(@"^seg\d{1,9}\.ts$")]
    private static partial Regex SegmentName();

    [LoggerMessage(Level = LogLevel.Information, Message = "Live channel {Channel} started ({Plan}){Resume}")]
    private partial void LogStartedCore(string channel, string plan, string resume);

    private void LogStarted(string channel, string plan, bool resume) => LogStartedCore(channel, plan, resume ? " after the source dropped" : "");

    [LoggerMessage(Level = LogLevel.Warning, Message = "Live channel {Channel}: ffmpeg exited with {Code}: {Error}")]
    private partial void LogExited(string channel, int code, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live channel {Channel}: dropped the oldest {Count} segments of its rewind window, keeping it to {Megabytes} MB on disk")]
    private partial void LogTrimmed(string channel, int count, long megabytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Live channel {Channel} stopped")]
    private partial void LogStopped(string channel);
}
