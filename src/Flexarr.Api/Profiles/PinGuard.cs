using System.Collections.Concurrent;

namespace Flexarr.Api.Profiles;

public enum PinCheck
{
    Right,
    Wrong,
    LockedOut,
}

/// <summary>
/// Slows PIN guessing to a crawl without letting a guesser lock anyone else out. Attempts on a profile are taken one at
/// a time and each wrong one pauses a second. After a few wrong PINs, whoever is guessing is locked out for a while,
/// doubling each time (up to an hour); the profile itself is only throttled, to one attempt every few seconds, so
/// someone else with the right PIN gets in after a short wait. A caller that may already manage the server (the API
/// key, or a browser or device signed in as the main profile) isn't held up by other people's guesses at all. A right
/// PIN clears the caller's strikes, and after each quiet spell half of them are forgotten.
/// <para>
/// Callers without a sign-in are told apart by address, which several people can share (a household's devices behind
/// one gateway). A browser that has given a profile's right PIN before gets a count of its own for that profile, so
/// someone else's guesses from the same address don't lock it out. A browser that hasn't shares the address's count,
/// so throwing away its cookie doesn't earn a guesser fresh attempts.
/// </para>
/// </summary>
public sealed class PinGuard(TimeProvider clock)
{
    private const int FreeAttempts = 4;
    private const int MaxKnownBrowsers = 10_000;
    private static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestLockout = TimeSpan.FromHours(1);

    /// <summary>Once a profile has had several wrong PINs, from anyone: the most often it's tried.</summary>
    public static readonly TimeSpan ProfileThrottle = TimeSpan.FromSeconds(5);

    /// <summary>After this long without a wrong PIN (once any lockout is over), half the strikes are forgotten.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new(StringComparer.Ordinal);

    /// <summary>"{profile}|{browser}" for browsers that have given that profile's right PIN.</summary>
    private readonly ConcurrentDictionary<string, byte> _knownBrowsers = new(StringComparer.Ordinal);

    private sealed class Strikes
    {
        public int Count;
        public DateTime LastWrong;
        public DateTime LockedUntil;

        /// <summary>When half the strikes were last forgotten.</summary>
        public DateTime HalvedAt;
    }

    /// <summary>
    /// Runs <paramref name="check"/> for <paramref name="profileId"/> on behalf of <paramref name="caller"/>, unless
    /// either is held off. <paramref name="mayManage"/> callers skip the profile-wide throttle and don't add to it.
    /// <paramref name="browser"/> is the asking browser's own id, when it has one but no sign-in.
    /// </summary>
    public async Task<(PinCheck Result, TimeSpan RetryAfter)> CheckAsync(string profileId, string caller, Func<bool> check, CancellationToken ct, bool mayManage = false,
        string? browser = null)
    {
        ArgumentNullException.ThrowIfNull(check);
        var known = browser is not null && _knownBrowsers.ContainsKey($"{profileId}|{browser}");
        var callerKey = known ? $"browser:{browser}" : $"caller:{caller}";
        var profileKey = $"profile:{profileId}";

        // Guesses at one profile queue up behind each other; a caller exempt from the profile's count has its own line.
        var gate = _gates.GetOrAdd(mayManage ? callerKey : profileKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var wait = HeldOff(callerKey, now, throttleOnly: false);
            if (!mayManage)
            {
                var throttled = HeldOff(profileKey, now, throttleOnly: true);
                wait = throttled > wait ? throttled : wait;
            }

            if (wait > TimeSpan.Zero)
            {
                return (PinCheck.LockedOut, wait);
            }

            if (check())
            {
                _strikes.TryRemove(callerKey, out _);
                if (browser is not null && !known)
                {
                    if (_knownBrowsers.Count >= MaxKnownBrowsers)
                    {
                        _knownBrowsers.Clear(); // they just share their address's count again
                    }

                    _knownBrowsers.TryAdd($"{profileId}|{browser}", 0);
                }

                return (PinCheck.Right, TimeSpan.Zero);
            }

            Strike(callerKey, now);
            if (!mayManage)
            {
                Strike(profileKey, now);
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

    /// <summary>How long <paramref name="key"/> must wait: a lockout for a caller, at most the throttle for a profile.</summary>
    private TimeSpan HeldOff(string key, DateTime now, bool throttleOnly)
    {
        if (!_strikes.TryGetValue(key, out var strikes))
        {
            return TimeSpan.Zero;
        }

        lock (strikes)
        {
            // Half the strikes go with each quiet spell, so a patient guesser climbs back up the lockouts quickly.
            var quietSince = new[] { strikes.LockedUntil, strikes.LastWrong, strikes.HalvedAt }.Max();
            if (now - quietSince > QuietPeriod)
            {
                var spells = (int)Math.Min(31, (now - quietSince) / QuietPeriod);
                strikes.Count >>= spells;
                if (strikes.Count == 0)
                {
                    _strikes.TryRemove(key, out _);
                    return TimeSpan.Zero;
                }

                strikes.HalvedAt = quietSince + QuietPeriod * spells;
            }

            if (throttleOnly)
            {
                return strikes.Count >= FreeAttempts && strikes.LastWrong + ProfileThrottle > now ? strikes.LastWrong + ProfileThrottle - now : TimeSpan.Zero;
            }

            return strikes.LockedUntil > now ? strikes.LockedUntil - now : TimeSpan.Zero;
        }
    }

    private void Strike(string key, DateTime now)
    {
        var strikes = _strikes.GetOrAdd(key, _ => new Strikes());
        lock (strikes)
        {
            strikes.Count++;
            strikes.LastWrong = now;
            if (strikes.Count >= FreeAttempts)
            {
                var lockout = FirstLockout * Math.Pow(2, Math.Min(strikes.Count - FreeAttempts, 10));
                strikes.LockedUntil = now + (lockout < LongestLockout ? lockout : LongestLockout);
            }
        }
    }
}
