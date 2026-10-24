using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming;

/// <summary>
/// One viewer's HLS transcode of one file. Segments are produced by an ffmpeg process that is started at
/// whatever segment the player asks for, restarted when the player seeks outside what's been produced,
/// and paused while it's far ahead of playback.
/// </summary>
public sealed partial class TranscodeSession : IAsyncDisposable
{
    private const int RunAhead = 30;       // segments (3 minutes) produced beyond the last request before pausing
    private const int ResumeAt = 15;       // resume once playback is within this many segments
    private const int SeekTolerance = 4;   // a request this far past what's produced waits rather than restarts

    /// <summary>How long a new or restarted session counts as active before ffmpeg is running.</summary>
    private static readonly TimeSpan StartingWindow = TimeSpan.FromSeconds(30);

    private readonly TranscodeSettings _settings;
    private readonly ILogger _logger;
    private readonly Action<TranscodeSession>? _reserve;

    // Held while ffmpeg is started, stopped, paused or resumed. Never disposed: a request may still be waiting on it.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _playlistLock = new();
    private volatile Process? _process;
    private int _startSegment;
    private bool _paused;
    private bool _released;
    private volatile bool _disposed;
    private string? _lastError;

    // What ffmpeg's playlist listed when it was last read, and its size and time then (it's polled every 100 ms).
    private HashSet<int> _completed = [];
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
    public int SegmentCount => FfmpegCommand.SegmentCount(DurationSeconds);
    public DateTime Created { get; } = DateTime.UtcNow;
    public DateTime LastAccess { get; internal set; } = DateTime.UtcNow;
    public int LastRequested { get; private set; }
    public bool IsRunning => _process is { } process && IsAlive(process);

    /// <summary>When ffmpeg was last (about to be) started, or the session created.</summary>
    internal DateTime StartedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Counts towards the server's transcode limit: ffmpeg is running, or is about to.</summary>
    public bool IsActive => IsRunning || DateTime.UtcNow - StartedAt < StartingWindow;

    public string MediaPlaylist() => FfmpegCommand.MediaPlaylist(DurationSeconds);

    /// <summary>Returns the path of segment <paramref name="index"/> once it is complete, transcoding it if needed.</summary>
    /// <exception cref="TranscodeLimitException">ffmpeg had stopped, and starting it again would go over the server's limit.</exception>
    public async Task<string?> GetSegmentAsync(int index, CancellationToken ct)
    {
        if (index < 0 || index >= SegmentCount)
        {
            return null;
        }

        Touch(index);
        var path = Path.Combine(Directory, $"seg{index}.ts");

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
                else if (_paused)
                {
                    ResumeProcess();
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

        return path;
    }

    /// <summary>Called periodically: pauses ffmpeg when it's far ahead of the viewer, resumes it when they catch up.</summary>
    public void Throttle()
    {
        // Skip a tick rather than wait while a request is starting or stopping ffmpeg.
        if (!ProcessSignals.Supported || !_gate.Wait(0))
        {
            return;
        }

        try
        {
            if (_process is not { } process || !IsAlive(process))
            {
                return;
            }

            var ahead = HighestComplete() - LastRequested;
            if (!_paused && ahead > RunAhead)
            {
                ProcessSignals.Pause(process.Id);
                _paused = true;
            }
            else if (_paused && ahead < ResumeAt)
            {
                ResumeProcess();
            }
        }
        finally
        {
            _gate.Release();
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
            if (_paused)
            {
                ProcessSignals.Resume(process.Id);
            }

            ProcessSignals.KillTree(process);
        }

        _paused = false;
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
        _paused = false;
        _released = false;
        var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {_settings.FfmpegPath}");
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastError = e.Data;
            }
        };
        process.BeginErrorReadLine();

        // Swap in the new process before disposing the old, so nothing reads a disposed one from the field.
        _process = process;
        previous?.Dispose();
        LogStarted(Id, segment, Plan.TargetHeight, Plan.VideoBitrate / 1000);
        return Task.CompletedTask;
    }

    private void ResumeProcess()
    {
        if (_process is { } process && IsAlive(process))
        {
            ProcessSignals.Resume(process.Id);
        }

        _paused = false;
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

        var done = new HashSet<int>();
        var highest = -1;
        foreach (var line in lines)
        {
            if (line.StartsWith("seg", StringComparison.Ordinal) && line.EndsWith(".ts", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(3, line.Length - 6), System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                done.Add(n);
                highest = Math.Max(highest, n);
            }
        }

        _completed = done;
        _highestCompleted = highest;
        _playlistStamp = stamp;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session}: starting at segment {Segment} ({Height}p, {Kbps} kbps)")]
    private partial void LogStarted(string session, int segment, int height, long kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcode {Session}: segment {Segment} failed: {Error}")]
    private partial void LogSegmentFailed(string session, int segment, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session}: idle, stopped and deleted its segments until it's needed again")]
    private partial void LogReleased(string session);
}
