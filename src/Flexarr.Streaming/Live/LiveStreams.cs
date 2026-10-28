using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming.Live;

/// <summary>
/// The channels being watched, one <see cref="LiveSession"/> each, started by their first viewer and stopped a little
/// after their last leaves (so flicking away and back doesn't lose the rewind window).
/// </summary>
public sealed class LiveStreams(Func<TranscodeSettings> settings, ILoggerFactory loggers) : IAsyncDisposable
{
    /// <summary>Segments a new channel has before its first viewer is given it: enough not to stall straight away.</summary>
    private const int SegmentsToStart = 2;

    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, LiveSession> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _starting = new(1, 1);
    private readonly ILogger _logger = loggers.CreateLogger<LiveSession>();
    private long _stoppedAt = long.MinValue / 2;

    public IReadOnlyCollection<LiveSession> All => [.. _sessions.Values];

    public LiveSession? Find(string channelKey) => _sessions.TryGetValue(channelKey, out var session) ? session : null;

    /// <summary>
    /// Adds <paramref name="viewer"/> to the channel, starting it with the plan <paramref name="plan"/> makes when nobody
    /// is watching it yet, and returns once it can be played.
    /// </summary>
    /// <exception cref="LiveStreamException">The channel couldn't be started; the message says why.</exception>
    public async Task<LiveSession> JoinAsync(string channelKey, LiveSource source, Func<CancellationToken, Task<LivePlan>> plan, TimeSpan rewind,
        string viewer, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await JoinOnceAsync(channelKey, source, plan, rewind, viewer, ct).ConfigureAwait(false);
            }
            catch (LiveStreamException ex) when (ex.Busy && attempt < 4 && Environment.TickCount64 - Interlocked.Read(ref _stoppedAt) < 15_000)
            {
                // A channel was stopped moments ago: the source takes a few seconds to free its connection.
                await Task.Delay(TimeSpan.FromSeconds(1.5 * attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<LiveSession> JoinOnceAsync(string channelKey, LiveSource source, Func<CancellationToken, Task<LivePlan>> plan, TimeSpan rewind,
        string viewer, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);
        LiveSession session;
        await _starting.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_sessions.TryGetValue(channelKey, out var running) && (running.Failed || running.Ended))
            {
                await StopAsync(running).ConfigureAwait(false);
                running = null;
            }

            if (running is null)
            {
                // A channel nobody watches only lingers for a quick return; it gives way to one somebody wants, as
                // the provider may allow a single connection.
                foreach (var idle in _sessions.Values.Where(s => s.ViewerCount == 0).ToList())
                {
                    await StopAsync(idle).ConfigureAwait(false);
                }

                var made = await plan(ct).ConfigureAwait(false);
                running = new LiveSession(channelKey, source, made, rewind, settings(), _logger);
                running.Start();
                _sessions[channelKey] = running;
            }

            session = running;
            session.Join(viewer);
        }
        finally
        {
            _starting.Release();
        }

        try
        {
            await session.WaitUntilPlayableAsync(SegmentsToStart, StartTimeout, ct).ConfigureAwait(false);
            return session;
        }
        catch
        {
            await LeaveAsync(session, viewer).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops a channel now, whoever's watching (a recording needs its stream).</summary>
    public Task EndAsync(LiveSession session) => StopAsync(session ?? throw new ArgumentNullException(nameof(session)));

    /// <summary>Stops a channel and forgets it, noting when, as its connection takes the source a moment to free.</summary>
    private async Task StopAsync(LiveSession session)
    {
        if (_sessions.TryRemove(new KeyValuePair<string, LiveSession>(session.ChannelKey, session)))
        {
            await session.DisposeAsync().ConfigureAwait(false);
            Interlocked.Exchange(ref _stoppedAt, Environment.TickCount64);
        }
    }

    /// <summary>Takes a viewer off a channel; a channel that failed and has nobody left is stopped at once.</summary>
    public async Task LeaveAsync(LiveSession session, string viewer)
    {
        ArgumentNullException.ThrowIfNull(session);
        session.Leave(viewer);
        if (session is { ViewerCount: 0, Failed: true })
        {
            await StopAsync(session).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Lets go of viewers not heard from within <paramref name="viewerIdle"/> and stops channels nobody has watched for
    /// <paramref name="linger"/>. Returns the viewers let go.
    /// </summary>
    public async Task<IReadOnlyList<string>> SweepAsync(TimeSpan viewerIdle, TimeSpan linger)
    {
        var dropped = new List<string>();
        foreach (var session in _sessions.Values)
        {
            dropped.AddRange(session.DropIdle(viewerIdle));
            if (session.EmptySince is { } since && (DateTime.UtcNow - since > linger || session.Failed))
            {
                await StopAsync(session).ConfigureAwait(false);
            }
        }

        return dropped;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var session in _sessions.Values)
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }

        _sessions.Clear();
        _starting.Dispose();
    }
}
