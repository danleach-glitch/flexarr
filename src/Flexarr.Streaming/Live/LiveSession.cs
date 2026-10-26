using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.RegularExpressions;
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
        LogStarted(ChannelKey, Plan.Describe(), resume);
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
                throw _lastError is { } failure ? LiveStreamException.FromError(failure) : new LiveStreamException("The channel couldn't be played.");
            }

            if (ReadPlaylist() is { } playlist && SegmentCount(playlist) >= segments)
            {
                return;
            }

            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        throw _lastError is { } error ? LiveStreamException.FromError(error) : new LiveStreamException("The channel didn't start sending in time.");
    }

    /// <summary>ffmpeg's playlist as it stands; it writes a new one beside it and renames, so a read never sees half.</summary>
    public string? ReadPlaylist()
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
        return File.Exists(path) ? path : null;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Live channel {Channel} stopped")]
    private partial void LogStopped(string channel);
}
