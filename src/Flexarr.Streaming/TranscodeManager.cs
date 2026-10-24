using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming;

/// <summary>Thrown when starting a transcode would go over <see cref="TranscodeSettings.MaxConcurrentTranscodes"/>.</summary>
public sealed class TranscodeLimitException(int limit)
    : Exception($"The server is already converting {limit} {(limit == 1 ? "video" : "videos")} for other viewers, its limit. Try again when one of them stops.");

/// <summary>Owns all transcode sessions: creation, lookup, throttling and cleanup.</summary>
public sealed partial class TranscodeManager(Func<TranscodeSettings> settings, ILoggerFactory loggers) : IAsyncDisposable
{
    private static readonly TimeSpan StopAfterIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleaseAfterIdle = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, TranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly Lock _createLock = new();
    private readonly ILogger _logger = loggers.CreateLogger<TranscodeSession>();

    public IEnumerable<TranscodeSession> Sessions => _sessions.Values;

    /// <summary>
    /// Transcodes using the machine: ffmpeg running (or paused for being far ahead), or just created or restarted.
    /// One left paused long enough for ffmpeg to stop, or created and never played, doesn't count.
    /// </summary>
    public int ActiveCount => _sessions.Values.Count(s => s.IsActive);

    /// <summary>
    /// Most transcodes one profile may hold at once (0 = no limit): all but one of the server's, so one viewer
    /// (or one misbehaving client) can't take every slot. Starting another replaces that profile's oldest.
    /// </summary>
    public int PerProfileLimit => settings().MaxConcurrentTranscodes switch
    {
        <= 0 => 0,
        1 => 1,
        var limit => limit - 1,
    };

    /// <exception cref="TranscodeLimitException">The server is already running as many transcodes as it's allowed.</exception>
    public TranscodeSession Create(string input, double durationSeconds, TranscodePlan plan)
    {
        var current = settings();
        lock (_createLock)
        {
            if (current.MaxConcurrentTranscodes > 0 && ActiveCount >= current.MaxConcurrentTranscodes)
            {
                throw new TranscodeLimitException(current.MaxConcurrentTranscodes);
            }

            var id = NewId();
            var session = new TranscodeSession(id, input, durationSeconds, plan, current, _logger, Reserve);
            _sessions[id] = session;
            return session;
        }
    }

    /// <summary>
    /// Called by a session about to start ffmpeg again after it stopped (after a long pause, say): counts it as
    /// active again, unless the others are already using every slot.
    /// </summary>
    /// <exception cref="TranscodeLimitException">The others are using every slot.</exception>
    private void Reserve(TranscodeSession session)
    {
        var limit = settings().MaxConcurrentTranscodes;
        lock (_createLock)
        {
            if (limit > 0 && _sessions.Values.Count(s => s != session && s.IsActive) >= limit)
            {
                throw new TranscodeLimitException(limit);
            }

            session.StartedAt = DateTime.UtcNow;
        }
    }

    public TranscodeSession? Get(string id) => _sessions.GetValueOrDefault(id);

    public async Task StopAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Run every few seconds.</summary>
    public async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        foreach (var session in _sessions.Values)
        {
            try
            {
                var idle = now - session.LastAccess;
                if (idle > ReleaseAfterIdle)
                {
                    // Free the disk but keep the session, so a viewer back from a long pause carries on.
                    await session.ReleaseAsync(ReleaseAfterIdle).ConfigureAwait(false);
                }
                else if (idle > StopAfterIdle)
                {
                    await session.StopProcessAsync(StopAfterIdle).ConfigureAwait(false);
                }
                else
                {
                    session.Throttle();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(ex, session.Id);
            }
        }
    }

    /// <summary>Removes transcode folders left behind by a previous run of the server.</summary>
    public void DeleteOrphans()
    {
        var root = Path.Combine(settings().CacheDirectory, "transcodes");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var dir in Directory.EnumerateDirectories(root).Where(d => !_sessions.ContainsKey(Path.GetFileName(d))))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Retried on the next start.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _sessions.Keys)
        {
            await StopAsync(id).ConfigureAwait(false);
        }
    }

    /// <summary>Session ids double as capability tokens in stream URLs, so they must be unguessable.</summary>
    public static string NewId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcode {Session}: cleanup failed")]
    private partial void LogSweepFailed(Exception ex, string session);
}
