using System.Collections.Concurrent;
using System.Text.RegularExpressions;
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
/// up to date through the relay while the phone is locked: what a TV is playing changing (<see cref="NowPlayingFeed"/>)
/// sends the times on, only when they've changed more than the activity's own clock would show (a pause, a seek, a new
/// speed), and something else playing, or nothing, ends it. Kept in memory: a restarted server lets them go stale.
/// </summary>
public sealed partial class LiveActivities(NowPlayingFeed feed, ILogger<LiveActivities> logger)
{
    /// <summary>Enough for every phone in a house many times over; more is someone filling the server.</summary>
    private const int Limit = 200;

    /// <summary>A position this far from where the last update's clock says it would be is a seek: sent.</summary>
    private const double Drift = 3;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    private sealed class Entry(string deviceId, LiveActivityRequest request)
    {
        public string DeviceId { get; } = deviceId;
        public LiveActivityRequest Request { get; } = request;
        public LiveActivityState? Sent { get; set; }
    }

    public static bool IsToken(string? token) => token is not null && TokenShape().IsMatch(token);

    /// <summary>A phone started a Live Activity for <paramref name="deviceId"/>; false when there are too many already.</summary>
    public bool Start(string deviceId, LiveActivityRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        End(request.Token);
        if (_entries.Count >= Limit)
        {
            return false;
        }

        _entries[request.Token] = new Entry(deviceId, request);
        feed.Follow(deviceId);
        return true;
    }

    /// <summary>The phone ended it (or the relay says Apple no longer knows it).</summary>
    public void End(string token)
    {
        if (_entries.TryRemove(token, out var entry))
        {
            feed.Unfollow(entry.DeviceId);
        }
    }

    /// <summary>What a TV is playing changed: the Live Activities following it hear, if it's news to them.</summary>
    public async Task ChangedAsync(DeviceNowPlayingDto change, PushRelay relay, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(relay);
        foreach (var (token, entry) in _entries.Where(e => e.Value.DeviceId == change.DeviceId).ToList())
        {
            var playing = change.NowPlaying;
            var ended = playing is null || playing.Item.Id != entry.Request.ItemId;
            LiveActivityState state;
            if (playing is null || ended)
            {
                state = entry.Sent ?? new LiveActivityState(true, 0, Now(), 0, 1);
            }
            else
            {
                state = new LiveActivityState(playing.Paused, playing.PositionSeconds, Seconds(playing.ReportedAt ?? DateTime.UtcNow), playing.DurationSeconds,
                    playing.Rate ?? 1);
                if (!IsNews(entry.Sent, state))
                {
                    continue;
                }
            }

            var (outcome, reason) = await relay.SendActivityAsync(entry.Request.Token, entry.Request.Environment, ended ? "end" : "update", state, ct);
            if (ended || outcome == PushOutcome.Gone)
            {
                End(token);
            }
            else if (outcome == PushOutcome.Sent)
            {
                entry.Sent = state;
            }
            else
            {
                LogNotSent(reason);
            }
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

    private static double Now() => Seconds(DateTime.UtcNow);

    private static double Seconds(DateTime at) => (at.ToUniversalTime() - DateTime.UnixEpoch).TotalSeconds;

    [GeneratedRegex("^[0-9a-fA-F]{64,200}$")]
    private static partial Regex TokenShape();

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't update a Live Activity: {Reason}")]
    private partial void LogNotSent(string? reason);
}
