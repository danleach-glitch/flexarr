using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Flexarr.Api.Notifications;

namespace Flexarr.Api.Devices;

/// <param name="Token">The Live Activity's own push token (hex), from ActivityKit.</param>
/// <param name="Environment">"production" or "sandbox", as for notifications.</param>
/// <param name="ItemId">What the TV was playing when the phone started it: anything else ends it.</param>
public sealed record LiveActivityRequest(string Token, string Environment, Guid ItemId);

/// <summary>
/// What the phone's Live Activity shows, and all the relay carries: times, no titles (the phone puts those on itself
/// when it starts the activity, so they never pass through the relay readable).
/// </summary>
/// <param name="At">When <paramref name="Position"/> was so, in seconds since 1970.</param>
public sealed record LiveActivityState(bool Paused, double Position, double At, double Duration, double Rate);

/// <summary>
/// The Live Activities phones have on their Lock Screens for a TV that's playing ("Living room · Slow Horses"), kept
/// up to date through the relay while the phone is locked. What a TV is playing changing (<see cref="NowPlayingFeed"/>)
/// sends the times on, a moment later so a burst of seeks is one push, and only when they've changed more than the
/// activity's own clock would show; something else playing ends it, and so does nothing playing for a little while (a
/// TV moving on to the next episode stops one playback before it starts the next). Nothing is pushed while the phone
/// has Flexarr open: it updates the activity itself. Kept in memory: a restarted server forgets them until the phone
/// is next opened.
/// </summary>
public sealed partial class LiveActivities(NowPlayingFeed feed, DeviceCommands commands, ILogger<LiveActivities> logger)
{
    /// <summary>Enough for every phone in a house many times over; more is someone filling the server.</summary>
    private const int Limit = 200;

    /// <summary>One phone (sign-in) has at most this many: one per TV it's following, give or take.</summary>
    private const int LimitPerSignIn = 4;

    /// <summary>A position this far from where the last update's clock says it would be is a seek: sent.</summary>
    private const double Drift = 3;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DeviceNowPlayingDto> _latest = new(StringComparer.Ordinal);
    private readonly Channel<string> _changed = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>How long changes are gathered before they're sent, so a burst of seeks is one push.</summary>
    public TimeSpan Gather { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>How long nothing playing waits before the activity is ended (moving on to the next episode has a gap).</summary>
    public TimeSpan EndGrace { get; set; } = TimeSpan.FromSeconds(8);

    /// <summary>After the relay fails, how long updates wait before trying again (ends still go).</summary>
    public TimeSpan Backoff { get; set; } = TimeSpan.FromSeconds(30);

    private sealed class Entry(string deviceId, LiveActivityRequest request, string? owner)
    {
        public string DeviceId { get; } = deviceId;
        public LiveActivityRequest Request { get; } = request;

        /// <summary>The phone's sign-in; null for the API key.</summary>
        public string? Owner { get; } = owner;

        public LiveActivityState? Sent { get; set; }

        /// <summary>When the TV was first seen playing nothing since (null: it's playing).</summary>
        public DateTime? GoneSince { get; set; }

        public DateTime RetryAfter { get; set; }
    }

    public static bool IsToken(string? token) => token is not null && TokenShape().IsMatch(token);

    /// <summary>
    /// A phone (<paramref name="owner"/>) started a Live Activity for <paramref name="deviceId"/>; false when there are
    /// too many already. The TV is looked at straight away: the activity hears the times, or ends if the TV has moved on.
    /// </summary>
    public bool Start(string deviceId, LiveActivityRequest request, string? owner)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(request.Token);
        if (_entries.Count >= Limit || (owner is not null && _entries.Values.Count(e => e.Owner == owner) >= LimitPerSignIn))
        {
            return false;
        }

        _entries[request.Token] = new Entry(deviceId, request, owner);
        feed.Follow(deviceId);
        LogStarted(deviceId);
        feed.Look(deviceId);
        return true;
    }

    /// <summary>The relay says Apple no longer knows it, or the phone registered the token again.</summary>
    public void End(string token) => Remove(token);

    /// <summary>
    /// A phone ended its activity: only its own (<paramref name="caller"/> null, the API key, may end any), and only for
    /// <paramref name="deviceId"/>.
    /// </summary>
    public void End(string token, string deviceId, string? caller)
    {
        if (_entries.TryGetValue(token, out var entry) && entry.DeviceId == deviceId && (caller is null || entry.Owner is null || entry.Owner == caller))
        {
            Remove(token);
        }
    }

    private void Remove(string token)
    {
        if (_entries.TryRemove(token, out var entry))
        {
            feed.Unfollow(entry.DeviceId);
            LogEnded(entry.DeviceId);
        }
    }

    /// <summary>What a TV is playing changed: noted for <see cref="LiveActivityWorker"/>, if any activity follows it.</summary>
    public void Changed(DeviceNowPlayingDto change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (_entries.Values.Any(e => e.DeviceId == change.DeviceId))
        {
            _latest[change.DeviceId] = change;
            _changed.Writer.TryWrite(change.DeviceId);
        }
    }

    internal ChannelReader<string> Pending => _changed.Reader;

    internal DeviceNowPlayingDto? Latest(string deviceId) => _latest.TryRemove(deviceId, out var change) ? change : null;

    /// <summary>Sends the Live Activities following <paramref name="change"/>'s TV what's news to them.</summary>
    public async Task SendAsync(DeviceNowPlayingDto change, PushRelay relay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(relay);
        var now = DateTime.UtcNow;
        foreach (var (token, entry) in _entries.Where(e => e.Value.DeviceId == change.DeviceId).ToList())
        {
            var playing = change.NowPlaying;
            bool ended;
            if (playing is null)
            {
                // Nothing playing: it may be moving on to the next episode. Looked at again once the grace is over.
                entry.GoneSince ??= now;
                if (now - entry.GoneSince < EndGrace)
                {
                    _ = LookAgainAsync(change.DeviceId, EndGrace - (now - entry.GoneSince.Value), ct);
                    continue;
                }

                ended = true;
            }
            else
            {
                entry.GoneSince = null;
                ended = playing.Item.Id != entry.Request.ItemId;
            }

            LiveActivityState state;
            if (ended)
            {
                // Its time now: one long paused would be refused as too old.
                state = (entry.Sent ?? new LiveActivityState(true, 0, 0, 0, 1)) with { Paused = true, At = Seconds(now) };
            }
            else
            {
                state = new LiveActivityState(playing!.Paused, playing.PositionSeconds, Seconds(playing.ReportedAt ?? now), playing.DurationSeconds, playing.Rate ?? 1);
                // The phone has Flexarr open and updates the activity itself; or the relay failed a moment ago.
                if (!IsNews(entry.Sent, state) || (entry.Owner is { } owner && commands.IsOnline(owner)) || now < entry.RetryAfter)
                {
                    continue;
                }
            }

            // A pause, a resume or the end is worth Apple's high priority; the rest (a seek) isn't.
            var urgent = ended || entry.Sent is null || entry.Sent.Paused != state.Paused;
            var (outcome, reason) = await relay.SendActivityAsync(entry.Request.Token, entry.Request.Environment, ended ? "end" : "update", state, urgent, ct);
            if (ended || outcome == PushOutcome.Gone)
            {
                Remove(token);
            }
            else if (outcome == PushOutcome.Sent)
            {
                entry.Sent = state;
            }
            else
            {
                entry.RetryAfter = now + Backoff;
                LogNotSent(reason);
            }
        }
    }

    private async Task LookAgainAsync(string deviceId, TimeSpan after, CancellationToken ct)
    {
        try
        {
            await Task.Delay(after + TimeSpan.FromMilliseconds(100), ct);
            feed.Look(deviceId);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    /// <summary>Whether <paramref name="next"/> shows anything the activity's clock, running from <paramref name="sent"/>, wouldn't.</summary>
    public static bool IsNews(LiveActivityState? sent, LiveActivityState next)
    {
        ArgumentNullException.ThrowIfNull(next);
        if (sent is null || sent.Paused != next.Paused || Math.Abs(sent.Rate - next.Rate) > 0.01 || Math.Abs(sent.Duration - next.Duration) > 1)
        {
            return true;
        }

        var expected = sent.Paused ? sent.Position : sent.Position + (next.At - sent.At) * sent.Rate;
        return Math.Abs(next.Position - expected) > Drift;
    }

    private static double Seconds(DateTime at) => (at.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;

    // Longer than a notification's: a Live Activity's token is 128 bytes.
    [GeneratedRegex("^[0-9a-fA-F]{64,512}$")]
    private static partial Regex TokenShape();

    [LoggerMessage(Level = LogLevel.Information, Message = "A phone's Live Activity follows device {Device}")]
    private partial void LogStarted(string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "A Live Activity for device {Device} ended")]
    private partial void LogEnded(string device);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't update a Live Activity: {Reason}")]
    private partial void LogNotSent(string? reason);
}

/// <summary>
/// Sends Live Activity updates through the relay, off the now-playing path, so a slow relay never holds up what the
/// phones hear. Changes are gathered for a moment first (see <see cref="LiveActivities.Gather"/>).
/// </summary>
public sealed partial class LiveActivityWorker(LiveActivities activities, IServiceScopeFactory scopes, ILogger<LiveActivityWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (await activities.Pending.WaitToReadAsync(stoppingToken))
        {
            await Task.Delay(activities.Gather, stoppingToken);
            var devices = new HashSet<string>(StringComparer.Ordinal);
            while (activities.Pending.TryRead(out var device))
            {
                devices.Add(device);
            }

            await using var scope = scopes.CreateAsyncScope();
            var relay = scope.ServiceProvider.GetRequiredService<PushRelay>();
            foreach (var device in devices)
            {
                if (activities.Latest(device) is not { } change)
                {
                    continue;
                }

                try
                {
                    await activities.SendAsync(change, relay, stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogFailed(ex, device);
                }
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't update the Live Activities for device {Device}")]
    private partial void LogFailed(Exception ex, string device);
}
