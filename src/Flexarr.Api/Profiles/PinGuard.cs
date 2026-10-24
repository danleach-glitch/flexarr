using System.Collections.Concurrent;

namespace Flexarr.Api.Profiles;

public enum PinCheck
{
    Right,
    Wrong,
    LockedOut,
}

/// <summary>
/// Slows PIN guessing to a crawl. Attempts on a profile are taken one at a time, each wrong one pauses a second, and
/// after a few wrong PINs both the profile and whoever is guessing are locked out for a while, doubling each time
/// (up to an hour). A right PIN clears both.
/// </summary>
public sealed class PinGuard(TimeProvider clock)
{
    private const int FreeAttempts = 4;
    private static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestLockout = TimeSpan.FromHours(1);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new(StringComparer.Ordinal);

    private sealed class Strikes
    {
        public int Count;
        public DateTime LockedUntil;
    }

    /// <summary>Runs <paramref name="check"/> for <paramref name="profileId"/> on behalf of <paramref name="caller"/>, unless either is locked out.</summary>
    public async Task<(PinCheck Result, TimeSpan RetryAfter)> CheckAsync(string profileId, string caller, Func<bool> check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);
        var gate = _gates.GetOrAdd(profileId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var keys = new[] { $"profile:{profileId}", $"caller:{caller}" };
            var now = clock.GetUtcNow().UtcDateTime;
            var wait = keys.Select(k => _strikes.TryGetValue(k, out var s) ? s.LockedUntil - now : TimeSpan.Zero).Max();
            if (wait > TimeSpan.Zero)
            {
                return (PinCheck.LockedOut, wait);
            }

            if (check())
            {
                foreach (var key in keys)
                {
                    _strikes.TryRemove(key, out _);
                }

                return (PinCheck.Right, TimeSpan.Zero);
            }

            foreach (var key in keys)
            {
                var strikes = _strikes.GetOrAdd(key, _ => new Strikes());
                lock (strikes)
                {
                    strikes.Count++;
                    if (strikes.Count >= FreeAttempts)
                    {
                        var lockout = FirstLockout * Math.Pow(2, Math.Min(strikes.Count - FreeAttempts, 10));
                        strikes.LockedUntil = now + (lockout < LongestLockout ? lockout : LongestLockout);
                    }
                }
            }

            // Inside the gate, so guesses at one profile can't run side by side.
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            return (PinCheck.Wrong, TimeSpan.Zero);
        }
        finally
        {
            gate.Release();
        }
    }
}
