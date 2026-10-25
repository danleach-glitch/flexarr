using System.Diagnostics;
using Flexarr.Streaming.Remux;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming;

/// <summary>
/// One viewer's HLS transcode of one file. Segments are produced by an ffmpeg process that is started at
/// whatever segment the player asks for and restarted when the player seeks outside what's been produced. ffmpeg
/// paces itself (<see cref="FfmpegCommand.RunAheadSeconds"/> ahead, then real time), so it never runs hours ahead of
/// the viewer. It's never paused with SIGSTOP: on macOS, .NET spins on a stopped child and can't start another
/// process until it's resumed, which froze every other stream.
/// </summary>
public sealed partial class TranscodeSession : IAsyncDisposable
{
    private const int SeekTolerance = 4;   // a request this far past what's produced waits rather than restarts

    /// <summary>How long a new or restarted session counts as active before ffmpeg is running.</summary>
    private static readonly TimeSpan StartingWindow = TimeSpan.FromSeconds(30);

    private readonly TranscodeSettings _settings;
    private readonly ILogger _logger;
    private readonly Action<TranscodeSession>? _reserve;

    // Held while ffmpeg is started or stopped. Never disposed: a request may still be waiting on it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _playlistLock = new();
    private volatile Process? _process;
    private int _startSegment;
    private bool _released;
    private volatile bool _disposed;
    private string? _lastError;

    // What ffmpeg's playlist listed when it was last read, and its size and time then (it's polled every 100 ms).
    private HashSet<int> _completed = [];

    // Repackaged runs: each fragment's place on the timeline (from its own timestamp), and which make up each segment.
    private readonly Dictionary<string, (double Start, double End)> _fragments = new(StringComparer.Ordinal);
    private readonly Dictionary<int, List<string>> _segmentFragments = [];
    private uint? _timescale;
    private int _highestCompleted = -1;
    private (long Length, DateTime Written) _playlistStamp;

    /// <param name="reserve">
    /// Called before ffmpeg starts again in a session where it isn't running; throws <see cref="TranscodeLimitException"/>
    /// when that would go over the server's limit.
    /// </param>
    public TranscodeSession(string id, string input, double durationSeconds, TranscodePlan plan, TranscodeSettings settings, ILogger logger,
        Action<TranscodeSession>? reserve = null)
    {
        Id = id;
        Input = input;
        DurationSeconds = durationSeconds;
        Plan = plan;
        _settings = settings;
        _logger = logger;
        _reserve = reserve;
        Directory = System.IO.Directory.CreateDirectory(Path.Combine(settings.CacheDirectory, "transcodes", id)).FullName;
    }

    public string Id { get; }

    /// <summary>The profile watching, for the per-profile share of the transcode limit.</summary>
    public string ProfileId { get; init; } = "";
    public string Input { get; }
    public double DurationSeconds { get; }
    public TranscodePlan Plan { get; }
    public string Directory { get; }
    public int SegmentCount => Plan.Repackage?.SegmentStarts.Count ?? FfmpegCommand.SegmentCount(DurationSeconds);

    /// <summary>The segment playing at <paramref name="seconds"/>.</summary>
    public int SegmentAt(double seconds)
    {
        if (Plan.Repackage is not { } repackage)
        {
            return Math.Clamp((int)(Math.Max(0, seconds) / FfmpegCommand.SegmentSeconds), 0, SegmentCount - 1);
        }

        var starts = repackage.SegmentStarts;
        var index = 0;
        while (index + 1 < starts.Count && starts[index + 1] <= seconds)
        {
            index++;
        }

        return index;
    }
    public DateTime Created { get; } = DateTime.UtcNow;
    public DateTime LastAccess { get; internal set; } = DateTime.UtcNow;
    public int LastRequested { get; private set; }
    public bool IsRunning => _process is { } process && IsAlive(process);

    /// <summary>When ffmpeg was last (about to be) started, or the session created.</summary>
    internal DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Counts towards the server's transcode limit: ffmpeg is running, or is about to.</summary>
    public bool IsActive => IsRunning || DateTime.UtcNow - StartedAt < StartingWindow;

    /// <summary>Where playback opens (a resume point), written into the playlist.</summary>
    public double StartSeconds { get; set; }

    public string MediaPlaylist() => Plan.Repackage is { } repackage
        ? FfmpegCommand.MediaPlaylist(repackage.SegmentStarts, DurationSeconds, StartSeconds)
        : FfmpegCommand.MediaPlaylist(DurationSeconds, StartSeconds, Plan.Fmp4);

    private string SegmentExtension => Plan.Fmp4 ? ".m4s" : ".ts";

    private byte[]? _init;

    /// <summary>
    /// The init segment as sent to players: a copied HEVC stream's configuration record is cut down to its parameter
    /// sets (<see cref="Mp4Fragments.TrimHevcConfig"/>), which an Apple TV insists on for HDR. Read once.
    /// </summary>
    public async Task<byte[]?> GetInitBytesAsync(CancellationToken ct)
    {
        if (_init is not null)
        {
            return _init;
        }

        if (await GetInitAsync(ct).ConfigureAwait(false) is not { } path)
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        return _init = Plan.Repackage is null ? bytes : Mp4Fragments.TrimHevcConfig(bytes);
    }

    /// <summary>
    /// The fragmented MP4 initialisation segment (codecs and, for Dolby Digital Plus, its Atmos), which ffmpeg writes as
    /// it starts. Players fetch it before any segment, so ffmpeg is started at the opening segment if it isn't running,
    /// and the init segment sent as soon as it's written: players give up on it sooner than on a media segment.
    /// </summary>
    public async Task<string?> GetInitAsync(CancellationToken ct)
    {
        var path = Path.Combine(Directory, "init.mp4");
        if (!Plan.Fmp4)
        {
            return null;
        }

        Task<string?>? opening = null;
        if (!File.Exists(path))
        {
            opening = GetSegmentAsync(SegmentAt(StartSeconds), ct);
        }

        // Written in one go once ffmpeg has its codecs; done when its size stops changing.
        var deadline = DateTime.UtcNow.AddSeconds(45);
        long last = -1;
        while (DateTime.UtcNow < deadline && !_disposed)
        {
            var size = File.Exists(path) ? new FileInfo(path).Length : 0;
            if (size > 0 && size == last)
            {
                return path;
            }

            if (opening is { IsCompleted: true } && size == 0)
            {
                await opening.ConfigureAwait(false); // ffmpeg failed: let its exception (or null) through
                return File.Exists(path) ? path : null;
            }

            last = size;
            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Returns the path of segment <paramref name="index"/> once it is complete, transcoding it if needed.</summary>
    /// <exception cref="TranscodeLimitException">ffmpeg had stopped, and starting it again would go over the server's limit.</exception>
    public async Task<string?> GetSegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= SegmentCount)
        {
            return null;
        }

        Touch(index);
        var path = Path.Combine(Directory, $"seg{index}{SegmentExtension}");

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return null;
            }

            if (!IsComplete(index))
            {
                var produced = HighestComplete();
                var coming = IsRunning && index >= _startSegment && index <= Math.Max(produced, _startSegment) + SeekTolerance;
                if (!coming)
                {
                    // Back from a long pause (or a crash), this is a new transcode as far as the limit goes.
                    if (!IsRunning)
                    {
                        _reserve?.Invoke(this);
                    }

                    await StartAsync(index).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        // Wait for ffmpeg to finish the segment (it's listed in its playlist only once fully written).
        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (!IsComplete(index))
        {
            // A restart for another request swaps the process; only give up if the one that died is still current.
            var process = _process;
            var died = process is not null && !IsAlive(process) && ReferenceEquals(process, _process) && !IsComplete(index);
            if (_disposed || DateTime.UtcNow > deadline || died)
            {
                LogSegmentFailed(Id, index, _lastError ?? (_disposed ? "stopped" : "timed out"));
                return null;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return Plan.Repackage is null ? path : await JoinFragmentsAsync(index, path, ct).ConfigureAwait(false);
    }

    /// <summary>A repackaged segment: its keyframe fragments one after another (each a moof and its mdat), written once.</summary>
    private async Task<string?> JoinFragmentsAsync(int index, string path, CancellationToken ct)
    {
        if (File.Exists(path))
        {
            return path;
        }

        List<string> parts;
        lock (_playlistLock)
        {
            if (!_segmentFragments.TryGetValue(index, out var names))
            {
                return null;
            }

            parts = [.. names];
        }

        var partial = path + ".part";
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                foreach (var part in parts)
                {
                    await using var input = File.OpenRead(Path.Combine(Directory, part));
                    await input.CopyToAsync(output, ct).ConfigureAwait(false);
                }
            }

            File.Move(partial, path, overwrite: true);
            return path;
        }
        catch (IOException)
        {
            // A restart deleted the fragments meanwhile: the player asks again and gets the new run's.
            return null;
        }
    }

    /// <summary>
    /// Stops ffmpeg but keeps finished segments (a paused viewer may come back). With <paramref name="ifIdleFor"/>,
    /// only if nothing has asked for a segment for that long, checked once nothing else is starting or stopping it.
    /// </summary>
    public async Task StopProcessAsync(TimeSpan? ifIdleFor = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (WasUsedWithin(ifIdleFor))
            {
                return;
            }

            StopProcess();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops ffmpeg and deletes its segments to free the disk, keeping the session: when the viewer comes back,
    /// the next segment they ask for starts a new transcode from there. With <paramref name="ifIdleFor"/>, only if
    /// nothing has asked for a segment for that long: a request that came in meanwhile may be about to serve one.
    /// </summary>
    public async Task ReleaseAsync(TimeSpan? ifIdleFor = null)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_released || _disposed || WasUsedWithin(ifIdleFor))
            {
                return;
            }

            StopProcess();
            DeleteSegments();
            _released = true;
            LogReleased(Id);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopProcess();
            var process = _process;
            _process = null;
            process?.Dispose();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Files may still be open briefly on some platforms; the cleanup sweep retries.
        }
    }

    /// <summary>Call with the gate held.</summary>
    private void StopProcess()
    {
        if (_process is { } process && IsAlive(process))
        {
            ProcessSignals.KillTree(process);
        }
    }

    /// <summary>Call with the gate held.</summary>
    private Task StartAsync(int segment)
    {
        StopProcess();
        var previous = _process;

        // Segments from the previous run past this point may be from a different encode; start clean.
        DeleteSegments();

        var start = new ProcessStartInfo(_settings.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = false };
        foreach (var arg in FfmpegCommand.Hls(Input, Plan, segment, Directory, _settings))
        {
            start.ArgumentList.Add(arg);
        }

        _lastError = null;
        StartedAt = DateTime.UtcNow;
        _startSegment = segment;
        _released = false;
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {_settings.FfmpegPath}");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastError = e.Data;
            }
        };
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) =>
        {
            // A run that ends with an error (not one stopped on purpose, which is killed) says why.
            try
            {
                if (process.ExitCode is not (0 or 137 or -1) && !_disposed)
                {
                    LogExited(Id, segment, process.ExitCode, _lastError ?? "no message");
                }
            }
            catch (InvalidOperationException)
            {
                // Disposed meanwhile.
            }
        };
        process.BeginErrorReadLine();

        // Swap in the new process before disposing the old, so nothing reads a disposed one from the field.
        _process = process;
        previous?.Dispose();
        LogStarted(Id, segment, Plan.TargetHeight, Plan.VideoBitrate / 1000);
        return Task.CompletedTask;
    }

    private void Touch(int index)
    {
        LastAccess = DateTime.UtcNow;
        LastRequested = index;
    }

    private bool WasUsedWithin(TimeSpan? span) => span is { } idle && DateTime.UtcNow - LastAccess <= idle;

    private void DeleteSegments()
    {
        foreach (var stale in System.IO.Directory.EnumerateFiles(Directory))
        {
            File.Delete(stale);
        }

        lock (_playlistLock)
        {
            _completed = [];
            _highestCompleted = -1;
            _playlistStamp = default;
            _fragments.Clear();
            _segmentFragments.Clear();
            _timescale = null;
        }
    }

    /// <summary>A process replaced (and disposed) by another request's restart counts as no longer running.</summary>
    private static bool IsAlive(Process process)
    {
        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private bool IsComplete(int index)
    {
        lock (_playlistLock)
        {
            RefreshCompleted();
            return _completed.Contains(index);
        }
    }

    private int HighestComplete()
    {
        lock (_playlistLock)
        {
            RefreshCompleted();
            return _completed.Count == 0 ? _startSegment - 1 : _highestCompleted;
        }
    }

    /// <summary>
    /// Reads the segments ffmpeg has listed in its own playlist, which it only does once a segment is fully written.
    /// The file is re-read only when its size or time has changed. Call with the playlist lock held.
    /// </summary>
    private void RefreshCompleted()
    {
        var playlist = new FileInfo(Path.Combine(Directory, "ffmpeg.m3u8"));
        if (!playlist.Exists)
        {
            _completed = [];
            _highestCompleted = -1;
            _playlistStamp = default;
            return;
        }

        var stamp = (playlist.Length, playlist.LastWriteTimeUtc);
        if (stamp == _playlistStamp)
        {
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(playlist.FullName);
        }
        catch (IOException)
        {
            return; // Being rewritten; try again on the next poll.
        }

        if (Plan.Repackage is { } repackage)
        {
            RefreshFragments(lines, repackage);
            _playlistStamp = stamp;
            return;
        }

        var done = new HashSet<int>();
        var highest = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith("seg", StringComparison.Ordinal) && line.EndsWith(SegmentExtension, StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(3, line.Length - 3 - SegmentExtension.Length), System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                done.Add(n);
                highest = Math.Max(highest, n);
            }
        }

        _completed = done;
        _highestCompleted = highest;
        _playlistStamp = stamp;
    }

    /// <summary>
    /// Places each fragment ffmpeg has finished by its own start time, then marks a segment complete once fragments run
    /// unbroken from its start to the next one's (or the end of the film). Call with the playlist lock held.
    /// </summary>
    private void RefreshFragments(string[] lines, Repackage repackage)
    {
        // Allows for a keyframe's decode time running a little ahead of its presentation time (B-frames).
        const double Slack = 0.5;
        if (_timescale is null)
        {
            try
            {
                _timescale = Mp4Fragments.Timescale(File.ReadAllBytes(Path.Combine(Directory, "init.mp4")));
            }
            catch (IOException)
            {
                return;
            }

            if (_timescale is null or 0)
            {
                _timescale = null;
                return;
            }
        }

        double duration = 0;
        var ended = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var text = line.AsSpan(8).TrimEnd(',');
                duration = double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : 0;
            }
            else if (line.StartsWith("frag", StringComparison.Ordinal) && !_fragments.ContainsKey(line))
            {
                try
                {
                    using var file = File.OpenRead(Path.Combine(Directory, line));
                    var head = new byte[Math.Min(file.Length, 64 * 1024)];
                    file.ReadExactly(head);
                    if (Mp4Fragments.DecodeTime(head) is { } decodeTime)
                    {
                        var start = decodeTime / (double)_timescale.Value;
                        _fragments[line] = (start, start + duration);
                    }
                }
                catch (IOException)
                {
                    return; // Try again on the next poll.
                }
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal))
            {
                ended = true;
            }
        }

        var starts = repackage.SegmentStarts;
        var byStart = _fragments.OrderBy(f => f.Value.Start).ToList();
        var done = new HashSet<int>();
        var highest = -1;
        _segmentFragments.Clear();
        for (var i = 0; i < starts.Count; i++)
        {
            var from = starts[i];
            var to = i + 1 < starts.Count ? starts[i + 1] : DurationSeconds;
            var parts = byStart.Where(f => f.Value.Start >= from - Slack && f.Value.Start < to - Slack).ToList();
            if (parts.Count == 0 || parts[0].Value.Start > from + 0.05)
            {
                continue;
            }

            var reaches = parts[^1].Value.End >= to - Slack || (ended && i == starts.Count - 1);
            var unbroken = parts.Zip(parts.Skip(1)).All(p => p.Second.Value.Start - p.First.Value.End < 0.1);
            if (reaches && unbroken)
            {
                done.Add(i);
                highest = Math.Max(highest, i);
                _segmentFragments[i] = [.. parts.Select(p => p.Key)];
            }
        }

        _completed = done;
        _highestCompleted = highest;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session}: starting at segment {Segment} ({Height}p, {Kbps} kbps)")]
    private partial void LogStarted(string session, int segment, int height, long kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcode {Session}: ffmpeg started at segment {Segment} exited with {Code}: {Error}")]
    private partial void LogExited(string session, int segment, int code, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcode {Session}: segment {Segment} failed: {Error}")]
    private partial void LogSegmentFailed(string session, int segment, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session}: idle, stopped and deleted its segments until it's needed again")]
    private partial void LogReleased(string session);
}
