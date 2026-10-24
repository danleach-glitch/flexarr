using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming;

/// <summary>Owns all transcode sessions: creation, lookup, throttling and cleanup.</summary>
public sealed class TranscodeManager(Func<TranscodeSettings> settings, ILoggerFactory loggers) : IAsyncDisposable
{
    private static readonly TimeSpan StopAfterIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan DeleteAfterIdle = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, TranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly ILogger _logger = loggers.CreateLogger<TranscodeSession>();

    public IEnumerable<TranscodeSession> Sessions => _sessions.Values;

    public TranscodeSession Create(string input, double durationSeconds, TranscodePlan plan)
    {
        var id = NewId();
        var session = new TranscodeSession(id, input, durationSeconds, plan, settings(), _logger);
        _sessions[id] = session;
        return session;
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
            var idle = now - session.LastAccess;
            if (idle > DeleteAfterIdle)
            {
                await StopAsync(session.Id).ConfigureAwait(false);
            }
            else if (idle > StopAfterIdle)
            {
                session.StopProcess();
            }
            else
            {
                session.Throttle();
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
}
