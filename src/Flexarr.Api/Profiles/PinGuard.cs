using System.Collections.Concurrent;
using Flexarr.Core.Media;

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
/// key, or a browser or device signed in as the main profile) isn't held up by guesses from anyone who can't. A right
/// PIN clears the caller's strikes for that profile, and after each quiet spell half of them are forgotten.
/// <para>
/// Callers that may manage the server do share one count per profile, which a right PIN doesn't clear: more of them
/// can be made at will (each TV paired is a new sign-in), so a script that got into the admin's page mustn't earn
/// fresh guesses at the main profile's PIN (which guards the API key) by pairing more. The owner rarely mistypes it, so
/// this count allows a few more slips than a caller's own.
/// </para>
/// <para>
/// Callers without a sign-in are told apart by address, which several people can share (a household's devices behind
/// one gateway). A browser that has given the main profile's right PIN before gets a count of its own for it, so
/// someone else's guesses from the same address don't lock it out. A browser that hasn't shares the address's count,
/// so throwing away its cookie doesn't earn a guesser fresh attempts. Only the main profile's browsers are remembered,
/// so knowing another profile's PIN doesn't let anyone crowd the owner's browser out of the list.
/// </para>
/// </summary>
public sealed class PinGuard(TimeProvider clock)
{
    private const int FreeAttempts = 4;

    /// <summary>Wrong PINs before the count shared by callers that may manage the server locks them all out.</summary>
    private const int ManagersFreeAttempts = 8;

    private const int MaxKnownBrowsers = 10_000;
    private static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestLockout = TimeSpan.FromHours(1);

    /// <summary>Once a profile has had several wrong PINs, from anyone: the most often it's tried.</summary>
    public static readonly TimeSpan ProfileThrottle = TimeSpan.FromSeconds(5);

    /// <summary>After this long without a wrong PIN (once any lockout is over), half the strikes are forgotten.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new(StringComparer.Ordinal);

    /// <summary>Browsers that have given the main profile's right PIN, and when they last did.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _knownBrowsers = new(StringComparer.Ordinal);

    private sealed class Strikes
    {
        public int Count;
        public DateTime LastWrong;
        public DateTime LockedUntil;

        /// <summary>When half the strikes were last forgotten.</summary>
        public DateTime HalvedAt;
    }

    /// <inheritdoc cref="CheckAsync(string, IReadOnlyList{string}, Func{bool}, CancellationToken, bool, string?)"/>
    public Task<(PinCheck Result, TimeSpan RetryAfter)> CheckAsync(string profileId, string caller, Func<bool> check, CancellationToken ct, bool mayManage = false,
        string? browser = null) => CheckAsync(profileId, [caller], check, ct, mayManage, browser);

    /// <summary>
    /// Runs <paramref name="check"/> for <paramref name="profileId"/> on behalf of a caller, unless it or the profile is
    /// held off. A caller may go by more than one name (a Tailscale login and the address it asks from): each gets the
    /// strike, and a lockout on any of them holds it off. <paramref name="mayManage"/> callers skip the profile-wide
    /// throttle and don't add to it, but share a count of their own. <paramref name="browser"/> is the asking browser's
    /// own id, when it has one but no sign-in.
    /// </summary>
    public async Task<(PinCheck Result, TimeSpan RetryAfter)> CheckAsync(string profileId, IReadOnlyList<string> callers, Func<bool> check, CancellationToken ct,
        bool mayManage = false, string? browser = null)
    {
        ArgumentNullException.ThrowIfNull(callers);
        ArgumentNullException.ThrowIfNull(check);
        var remembers = browser is not null && profileId == PlaybackState.DefaultUser;
        var known = remembers && _knownBrowsers.ContainsKey(browser!);
        // Per profile, so a right PIN for your own profile doesn't wipe your wrong ones at someone else's.
        string[] callerKeys = known ? [$"browser:{profileId}|{browser}"] : [.. callers.Select(c => $"caller:{profileId}|{c}")];
        var profileKey = $"profile:{profileId}";
        var managersKey = $"managers:{profileId}";

        // Guesses at one profile queue up behind each other. Callers exempt from the profile's count have a line of
        // their own, one for all of them, so their shared count can't be raced past.
        var gate = _gates.GetOrAdd(mayManage ? managersKey : profileKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var wait = callerKeys.Select(k => HeldOff(k, now, throttleOnly: false)).Max();
            var shared = mayManage ? HeldOff(managersKey, now, throttleOnly: false) : HeldOff(profileKey, now, throttleOnly: true);
            wait = shared > wait ? shared : wait;
            if (wait > TimeSpan.Zero)
            {
                return (PinCheck.LockedOut, wait);
            }

            if (check())
            {
                foreach (var key in callerKeys)
                {
                    _strikes.TryRemove(key, out _);
                }

                if (remembers)
                {
                    Remember(browser!, now);
                }

                return (PinCheck.Right, TimeSpan.Zero);
            }

            foreach (var key in callerKeys)
            {
                Strike(key, now, FreeAttempts);
            }

            Strike(mayManage ? managersKey : profileKey, now, mayManage ? ManagersFreeAttempts : FreeAttempts);

            // Inside the gate, so guesses at one profile can't run side by side.
            await Task.Delay(TimeSpan.FromSeconds(1), clock, ct);
            return (PinCheck.Wrong, TimeSpan.Zero);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Notes that a browser gave the main profile's PIN. When the list is full, the one longest unseen goes.</summary>
    private void Remember(string browser, DateTime now)
    {
        lock (_knownBrowsers)
        {
            if (!_knownBrowsers.ContainsKey(browser) && _knownBrowsers.Count >= MaxKnownBrowsers)
            {
                _knownBrowsers.TryRemove(_knownBrowsers.MinBy(b => b.Value).Key, out _); // it shares its address's count again
            }

            _knownBrowsers[browser] = now;
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

    private void Strike(string key, DateTime now, int freeAttempts)
    {
        var strikes = _strikes.GetOrAdd(key, _ => new Strikes());
        lock (strikes)
        {
            strikes.Count++;
            strikes.LastWrong = now;
            if (strikes.Count >= freeAttempts)
            {
                var lockout = FirstLockout * Math.Pow(2, Math.Min(strikes.Count - freeAttempts, 10));
                strikes.LockedUntil = now + (lockout < LongestLockout ? lockout : LongestLockout);
            }
        }
    }
}
