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

    private readonly TranscodeSettings _settings;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Process? _process;
    private int _startSegment;
    private bool _paused;
    private string? _lastError;

    public TranscodeSession(string id, string input, double durationSeconds, TranscodePlan plan, TranscodeSettings settings, ILogger logger)
    {
        Id = id;
        Input = input;
        DurationSeconds = durationSeconds;
        Plan = plan;
        _settings = settings;
        _logger = logger;
        Directory = System.IO.Directory.CreateDirectory(Path.Combine(settings.CacheDirectory, "transcodes", id)).FullName;
    }

    public string Id { get; }
    public string Input { get; }
    public double DurationSeconds { get; }
    public TranscodePlan Plan { get; }
    public string Directory { get; }
    public int SegmentCount => FfmpegCommand.SegmentCount(DurationSeconds);
    public DateTime LastAccess { get; private set; } = DateTime.UtcNow;
    public int LastRequested { get; private set; }
    public bool IsRunning => _process is { HasExited: false };

    public string MediaPlaylist() => FfmpegCommand.MediaPlaylist(DurationSeconds);

    /// <summary>Returns the path of segment <paramref name="index"/> once it is complete, transcoding it if needed.</summary>
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
            if (!IsComplete(index))
            {
                var produced = HighestComplete();
                var coming = IsRunning && index >= _startSegment && index <= Math.Max(produced, _startSegment) + SeekTolerance;
                if (!coming)
                {
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
            if (DateTime.UtcNow > deadline || (_process is { HasExited: true } && !IsComplete(index)))
            {
                LogSegmentFailed(Id, index, _lastError ?? "timed out");
                return null;
            }

            await Task.Delay(100, ct).ConfigureAwait(false);
        }

        return path;
    }

    /// <summary>Called periodically: pauses ffmpeg when it's far ahead of the viewer, resumes it when they catch up.</summary>
    public void Throttle()
    {
        if (!IsRunning || !ProcessSignals.Supported)
        {
            return;
        }

        var ahead = HighestComplete() - LastRequested;
        if (!_paused && ahead > RunAhead)
        {
            ProcessSignals.Pause(_process!.Id);
            _paused = true;
        }
        else if (_paused && ahead < ResumeAt)
        {
            ResumeProcess();
        }
    }

    /// <summary>Stops ffmpeg but keeps finished segments (a paused viewer may come back).</summary>
    public void StopProcess()
    {
        if (_process is { HasExited: false } process)
        {
            if (_paused)
            {
                ProcessSignals.Resume(process.Id);
            }

            process.Kill(entireProcessTree: true);
        }

        _paused = false;
    }

    public ValueTask DisposeAsync()
    {
        StopProcess();
        _process?.Dispose();
        _gate.Dispose();
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (IOException)
        {
            // Files may still be open briefly on some platforms; the cleanup sweep retries.
        }

        return ValueTask.CompletedTask;
    }

    private Task StartAsync(int segment)
    {
        StopProcess();
        _process?.Dispose();

        // Segments from the previous run past this point may be from a different encode; start clean.
        foreach (var stale in System.IO.Directory.EnumerateFiles(Directory))
        {
            File.Delete(stale);
        }

        var start = new ProcessStartInfo(_settings.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardInput = false };
        foreach (var arg in FfmpegCommand.Hls(Input, Plan, segment, Directory, _settings))
        {
            start.ArgumentList.Add(arg);
        }

        _lastError = null;
        _startSegment = segment;
        _paused = false;
        _process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {_settings.FfmpegPath}");
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _lastError = e.Data;
            }
        };
        _process.BeginErrorReadLine();
        LogStarted(Id, segment, Plan.TargetHeight, Plan.VideoBitrate / 1000);
        return Task.CompletedTask;
    }

    private void ResumeProcess()
    {
        if (_process is { HasExited: false } process)
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

    private bool IsComplete(int index) => CompletedSegments().Contains(index);

    private int HighestComplete()
    {
        var done = CompletedSegments();
        return done.Count == 0 ? _startSegment - 1 : done.Max();
    }

    /// <summary>Segments ffmpeg has listed in its own playlist, which it only does once a segment is fully written.</summary>
    private HashSet<int> CompletedSegments()
    {
        var playlist = Path.Combine(Directory, "ffmpeg.m3u8");
        var done = new HashSet<int>();
        if (!File.Exists(playlist))
        {
            return done;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(playlist);
        }
        catch (IOException)
        {
            return done; // Being rewritten; try again on the next poll.
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("seg", StringComparison.Ordinal) && line.EndsWith(".ts", StringComparison.Ordinal)
                && int.TryParse(line.AsSpan(3, line.Length - 6), System.Globalization.CultureInfo.InvariantCulture, out var n))
            {
                done.Add(n);
            }
        }

        return done;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session}: starting at segment {Segment} ({Height}p, {Kbps} kbps)")]
    private partial void LogStarted(string session, int segment, int height, long kbps);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Transcode {Session}: segment {Segment} failed: {Error}")]
    private partial void LogSegmentFailed(string session, int segment, string error);
}
