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
/// fresh guesses at the admin PIN (which guards the API key) by pairing more. The owner rarely mistypes it, so
/// this count allows a few more slips than a caller's own.
/// </para>
/// <para>
/// Callers without a sign-in are told apart by address, which several people can share (a household's devices behind
/// one gateway). A browser that has given a profile's right PIN before gets a count of its own for that profile, so
/// someone else's guesses from the same address don't lock it out. A browser that hasn't shares the address's count,
/// so throwing away its cookie doesn't earn a guesser fresh attempts. Each profile remembers its own browsers, so
/// knowing one profile's PIN doesn't let anyone crowd the owner's browser out of the admin PIN's list.
/// </para>
/// <para>
/// A caller that keeps changing address starts afresh each time, so every PIN (the admin PIN, which guards the API key,
/// and each profile's) also has a daily allowance of wrong guesses from callers told apart only by address: once it's
/// spent, none of them may try that PIN until the day is out. Callers that may manage the server still get in, and so
/// do those with a count they can't shed (a browser or device with a sign-in, a browser that has given that PIN right
/// before): they neither spend the allowance nor are held off by it, so a guesser can't use it to lock the profile's
/// owner out for the day.
/// </para>
/// </summary>
public sealed class PinGuard(TimeProvider clock)
{
    /// <summary>What the admin PIN is counted under, apart from the main profile's own PIN.</summary>
    public const string AdminKey = "admin";

    private const int FreeAttempts = 4;

    /// <summary>Wrong PINs before the count shared by callers that may manage the server locks them all out.</summary>
    private const int ManagersFreeAttempts = 8;

    private const int MaxKnownBrowsers = 10_000;
    private static readonly TimeSpan FirstLockout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LongestLockout = TimeSpan.FromHours(1);

    /// <summary>Once a profile has had several wrong PINs, from anyone: the most often it's tried.</summary>
    public static readonly TimeSpan ProfileThrottle = TimeSpan.FromSeconds(5);

    /// <summary>Wrong admin PINs a day from callers told apart only by address, after which none of them may try it.</summary>
    public const int AdminDailyAllowance = 100;

    /// <summary>Wrong PINs a day at one profile from callers told apart only by address, after which none of them may try it.</summary>
    public const int ProfileDailyAllowance = 100;

    /// <summary>
    /// Wrong PINs a day from one caller at one profile, after which it may not try that profile's PIN until the day is
    /// out. Strikes halve with each quiet spell, so without it a caller that paces itself would get well over a hundred
    /// guesses a day; a right PIN clears it, as it does the strikes.
    /// </summary>
    public const int CallerDailyAllowance = 20;

    /// <summary>After this long without a wrong PIN (once any lockout is over), half the strikes are forgotten.</summary>
    public static readonly TimeSpan QuietPeriod = TimeSpan.FromMinutes(30);

    /// <summary>
    /// A caller has just been locked out of a profile for the first time in a run of wrong PINs: the profile, and the
    /// names the caller goes by ("sign-in:…", "address:…", "tailscale:…"). See <see cref="Notifications.PinAlerts"/>.
    /// </summary>
    public event Action<string, IReadOnlyList<string>>? CallerLockedOut;

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Strikes> _strikes = new(StringComparer.Ordinal);

    /// <summary>Each PIN's wrong guesses today from callers told apart only by address.</summary>
    private readonly ConcurrentDictionary<string, Daily> _daily = new(StringComparer.Ordinal);

    /// <summary>For each PIN, the browsers that have given it right, and when they last did.</summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, DateTime>> _knownBrowsers = new(StringComparer.Ordinal);

    private sealed class Daily
    {
        public DateTime Since;
        public int Count;
    }

    private sealed class Strikes
    {
        public int Count;
        public DateTime LastWrong;
        public DateTime LockedUntil;

        /// <summary>When half the strikes were last forgotten.</summary>
        public DateTime HalvedAt;

        /// <summary>The first wrong PIN of the caller's day, and how many since; never halved.</summary>
        public DateTime DailySince;
        public int DailyCount;
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
        var known = Known(profileId, browser);
        var callerKeys = CallerKeys(profileId, callers, browser, known);
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
            var callerDay = callerKeys.Select(k => DayOut(k, now)).Max();
            wait = callerDay > wait ? callerDay : wait;
            var shared = mayManage ? HeldOff(managersKey, now, throttleOnly: false) : HeldOff(profileKey, now, throttleOnly: true);
            wait = shared > wait ? shared : wait;
            var daily = CountsDaily(mayManage, known, callers) ? _daily.GetOrAdd(profileId, _ => new Daily()) : null;
            if (daily is not null)
            {
                lock (daily)
                {
                    if (now - daily.Since >= TimeSpan.FromDays(1))
                    {
                        (daily.Since, daily.Count) = (now, 0);
                    }

                    var allowance = profileId == AdminKey ? AdminDailyAllowance : ProfileDailyAllowance;
                    if (daily.Count >= allowance && daily.Since + TimeSpan.FromDays(1) - now is var dayOut && dayOut > wait)
                    {
                        wait = dayOut;
                    }
                }
            }

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

                if (browser is not null)
                {
                    Remember(profileId, browser, now);
                }

                return (PinCheck.Right, TimeSpan.Zero);
            }

            if (callerKeys.Select(key => Strike(key, now, FreeAttempts)).ToList().Any(count => count == FreeAttempts))
            {
                CallerLockedOut?.Invoke(profileId, callers);
            }

            _ = Strike(mayManage ? managersKey : profileKey, now, mayManage ? ManagersFreeAttempts : FreeAttempts);
            if (daily is not null)
            {
                lock (daily)
                {
                    daily.Count++;
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

    /// <summary>
    /// Takes back the wrong guess just counted at <paramref name="profileId"/> from these callers: the PIN they gave
    /// was the right one for another check (the admin PIN, given to choose the main profile).
    /// </summary>
    public void Forgive(string profileId, IReadOnlyList<string> callers, bool mayManage, string? browser = null)
    {
        ArgumentNullException.ThrowIfNull(callers);
        var known = Known(profileId, browser);
        if (CountsDaily(mayManage, known, callers) && _daily.TryGetValue(profileId, out var daily))
        {
            lock (daily)
            {
                daily.Count = Math.Max(0, daily.Count - 1);
            }
        }

        foreach (var key in CallerKeys(profileId, callers, browser, known).Append(mayManage ? $"managers:{profileId}" : $"profile:{profileId}"))
        {
            if (_strikes.TryGetValue(key, out var strikes))
            {
                lock (strikes)
                {
                    strikes.Count = Math.Max(0, strikes.Count - 1);
                    strikes.DailyCount = Math.Max(0, strikes.DailyCount - 1);
                    if (strikes.Count < FreeAttempts)
                    {
                        strikes.LockedUntil = default;
                    }
                }
            }
        }
    }

    /// <summary>Whether <paramref name="browser"/> has given <paramref name="profileId"/>'s PIN right before.</summary>
    private bool Known(string profileId, string? browser) =>
        browser is not null && _knownBrowsers.TryGetValue(profileId, out var known) && known.ContainsKey(browser);

    /// <summary>
    /// What a caller's guesses at <paramref name="profileId"/> count under. Per profile, so a right PIN for your own
    /// profile doesn't wipe your wrong ones at someone else's.
    /// </summary>
    private static string[] CallerKeys(string profileId, IReadOnlyList<string> callers, string? browser, bool known) =>
        known ? [$"browser:{profileId}|{browser}"] : [.. callers.Select(c => $"caller:{profileId}|{c}")];

    /// <summary>
    /// Whether a caller's wrong guesses count in the PIN's day's allowance: only callers that could start afresh from
    /// another address. A sign-in, or a browser known by its right PIN, keeps its own count wherever it asks from. A
    /// caller that also goes by a name that isn't a sign-in (a browser moved over from earlier versions' shared cookie,
    /// which could ask for another) doesn't.
    /// </summary>
    private static bool CountsDaily(bool mayManage, bool known, IReadOnlyList<string> callers) =>
        !mayManage && !known && !(callers.Count > 0 && callers.All(c => c.StartsWith("sign-in:", StringComparison.Ordinal)));

    /// <summary>Notes that a browser gave a PIN right. When that PIN's list is full, the one longest unseen goes.</summary>
    private void Remember(string profileId, string browser, DateTime now)
    {
        var known = _knownBrowsers.GetOrAdd(profileId, _ => new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal));
        lock (known)
        {
            if (!known.ContainsKey(browser) && known.Count >= MaxKnownBrowsers)
            {
                known.TryRemove(known.MinBy(b => b.Value).Key, out _); // it shares its address's count again
            }

            known[browser] = now;
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
                if (strikes.Count == 0 && now - strikes.DailySince >= TimeSpan.FromDays(1))
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

    /// <summary>How long a caller must wait for its day's allowance of wrong PINs (<see cref="CallerDailyAllowance"/>) to come back.</summary>
    private TimeSpan DayOut(string key, DateTime now)
    {
        if (!_strikes.TryGetValue(key, out var strikes))
        {
            return TimeSpan.Zero;
        }

        lock (strikes)
        {
            var dayOut = strikes.DailySince + TimeSpan.FromDays(1) - now;
            return strikes.DailyCount >= CallerDailyAllowance && dayOut > TimeSpan.Zero ? dayOut : TimeSpan.Zero;
        }
    }

    /// <summary>Counts a wrong PIN; returns how many there are now.</summary>
    private int Strike(string key, DateTime now, int freeAttempts)
    {
        var strikes = _strikes.GetOrAdd(key, _ => new Strikes());
        lock (strikes)
        {
            if (now - strikes.DailySince >= TimeSpan.FromDays(1))
            {
                strikes.DailySince = now;
                strikes.DailyCount = 0;
            }

            strikes.DailyCount++;
            strikes.Count++;
            strikes.LastWrong = now;
            if (strikes.Count >= freeAttempts)
            {
                var lockout = FirstLockout * Math.Pow(2, Math.Min(strikes.Count - freeAttempts, 10));
                strikes.LockedUntil = now + (lockout < LongestLockout ? lockout : LongestLockout);
            }

            return strikes.Count;
        }
    }
}
