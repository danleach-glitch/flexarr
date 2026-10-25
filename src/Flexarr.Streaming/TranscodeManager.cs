using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming;

/// <summary>Thrown when starting a transcode would go over <see cref="TranscodeSettings.MaxConcurrentTranscodes"/>.</summary>
public sealed class TranscodeLimitException(string? message = null)
    : Exception(message ?? "Flexarr is busy playing on other screens. Try again when one of them finishes.");

/// <summary>Owns all transcode sessions: creation, lookup, throttling and cleanup.</summary>
public sealed partial class TranscodeManager(Func<TranscodeSettings> settings, ILoggerFactory loggers) : IAsyncDisposable
{
    private static readonly TimeSpan StopAfterIdle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ReleaseAfterIdle = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RememberEndedFor = TimeSpan.FromHours(12);

    /// <summary>Why a viewer's transcode was ended to make way for another, for that viewer's player to show.</summary>
    public const string TakenOverBySameProfile =
        "Someone started watching on another screen, so this one had to stop.";
    public const string TakenOverByOtherProfile =
        "Someone on another profile started watching on another screen, so this one had to stop.";

    private readonly ConcurrentDictionary<string, TranscodeSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, (string Reason, DateTime At)> _endedForOthers = new(StringComparer.Ordinal);
    private readonly Lock _createLock = new();
    private readonly ILogger _logger = loggers.CreateLogger<TranscodeSession>();

    public IEnumerable<TranscodeSession> Sessions => _sessions.Values;

    /// <summary>Raised with a session's id when it's ended to make way for another (see <see cref="CreateAsync"/>).</summary>
    public event Action<string>? TakenOver;

    /// <summary>
    /// Transcodes using the machine: ffmpeg running (or paused for being far ahead), or just created or restarted.
    /// One left paused long enough for ffmpeg to stop, or created and never played, doesn't count.
    /// </summary>
    public int ActiveCount => _sessions.Values.Count(s => s.IsActive);

    /// <summary>
    /// Most transcodes one profile may hold once the server is full (0 = no limit): all but one of the server's, so
    /// one viewer (or one misbehaving client) can't keep everyone else out. While slots are free a profile may use them.
    /// </summary>
    public int PerProfileLimit => settings().MaxConcurrentTranscodes switch
    {
        <= 0 => 0,
        1 => 1,
        var limit => limit - 1,
    };

    /// <summary>
    /// Starts a transcode for <paramref name="profileId"/>. With every slot in use, the profile's oldest transcode
    /// makes way if it already holds its share; if it doesn't, the oldest of a profile holding more than its share does.
    /// A viewer <paramref name="restarting"/> a stream that stopped never takes anyone's place: that would just move
    /// the problem to another screen, whose own restart would take the next.
    /// </summary>
    /// <exception cref="TranscodeLimitException">Every slot is in use and nobody has to give one up.</exception>
    /// <param name="preparing">Getting the next episode ready: like a restart, it never takes anyone's place.</param>
    public async Task<TranscodeSession> CreateAsync(string profileId, string input, double durationSeconds, TranscodePlan plan, bool restarting = false,
        bool preparing = false)
    {
        var current = settings();
        var limit = current.MaxConcurrentTranscodes;
        TranscodeSession session;
        List<TranscodeSession> ended = [];
        lock (_createLock)
        {
            var active = _sessions.Values.Where(s => s.IsActive).ToList();
            if (limit > 0 && active.Count >= limit)
            {
                if (restarting)
                {
                    throw new TranscodeLimitException(
                        "The stream stopped, and Flexarr is now busy playing on other screens. Try again when one of them finishes.");
                }

                if (preparing)
                {
                    throw new TranscodeLimitException("Flexarr is busy playing on other screens, so the next episode wasn't got ready.");
                }

                var share = PerProfileLimit;
                var mine = active.Where(s => s.ProfileId == profileId).ToList();
                var (candidates, reason) = mine.Count >= share
                    ? (mine, TakenOverBySameProfile)
                    : (active.GroupBy(s => s.ProfileId).Where(g => g.Count() > share).SelectMany(g => g).ToList(), TakenOverByOtherProfile);
                var needed = active.Count - limit + 1;
                if (candidates.Count < needed)
                {
                    throw new TranscodeLimitException();
                }

                foreach (var oldest in candidates.OrderBy(s => s.Created).Take(needed))
                {
                    _sessions.TryRemove(oldest.Id, out _);
                    _endedForOthers[oldest.Id] = (reason, DateTime.UtcNow);
                    ended.Add(oldest);
                }
            }

            var id = NewId();
            session = new TranscodeSession(id, input, durationSeconds, plan, current, _logger, Reserve) { ProfileId = profileId };
            _sessions[id] = session;
        }

        foreach (var old in ended)
        {
            LogTakenOver(old.Id, old.ProfileId);
            TakenOver?.Invoke(old.Id);
            await old.DisposeAsync().ConfigureAwait(false);
        }

        return session;
    }

    /// <summary>Why this session was ended to make way for another viewer, if it was (remembered for a while).</summary>
    public string? TakenOverReason(string id) => _endedForOthers.TryGetValue(id, out var ended) ? ended.Reason : null;

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
                throw new TranscodeLimitException();
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
        foreach (var (id, ended) in _endedForOthers)
        {
            if (now - ended.At > RememberEndedFor)
            {
                _endedForOthers.TryRemove(id, out _);
            }
        }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Transcode {Session} for profile {Profile} ended to make way for another")]
    private partial void LogTakenOver(string session, string profile);

    [LoggerMessage(Level = LogLevel.Error, Message = "Transcode {Session}: cleanup failed")]
    private partial void LogSweepFailed(Exception ex, string session);
}
