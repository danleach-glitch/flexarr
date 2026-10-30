using System.Collections.Concurrent;

namespace Flexarr.Api.Security;

/// <summary>
/// Browsers and devices where the main profile's PIN has been given lately, so they may manage the server. Choosing
/// the main profile lets you watch as it; managing the server takes the PIN as well. An unlock lasts
/// <see cref="Lasts"/> after it was given or after the last change made with it (see <see cref="AdminHandler"/>), until
/// another profile is chosen there or Lock is pressed. It's kept in memory, so restarting the server locks everything.
/// </summary>
public sealed class AdminUnlocks(TimeProvider clock)
{
    public static readonly TimeSpan Lasts = TimeSpan.FromMinutes(15);

    private readonly ConcurrentDictionary<string, DateTime> _until = new(StringComparer.Ordinal);

    /// <summary>Unlocks a sign-in; returns when it lapses.</summary>
    public DateTime Unlock(string signInId)
    {
        var until = clock.GetUtcNow().UtcDateTime + Lasts;
        _until[signInId] = until;
        return until;
    }

    /// <summary>When the sign-in's unlock lapses, or null if it isn't unlocked.</summary>
    public DateTime? UnlockedUntil(string? signInId)
    {
        if (signInId is null || !_until.TryGetValue(signInId, out var until))
        {
            return null;
        }

        if (until > clock.GetUtcNow().UtcDateTime)
        {
            return until;
        }

        _until.TryRemove(new KeyValuePair<string, DateTime>(signInId, until));
        return null;
    }

    public bool IsUnlocked(string? signInId) => UnlockedUntil(signInId) is not null;

    /// <summary>Pushes an unlock on, after a change made with it.</summary>
    public void Extend(string signInId)
    {
        // Only the unlock that's there now: one locked meanwhile (Lock, or a new PIN) stays locked.
        if (UnlockedUntil(signInId) is { } until)
        {
            _until.TryUpdate(signInId, clock.GetUtcNow().UtcDateTime + Lasts, until);
        }
    }

    public void Lock(string? signInId)
    {
        if (signInId is not null)
        {
            _until.TryRemove(signInId, out _);
        }
    }

    /// <summary>Locks every sign-in but <paramref name="keepId"/>: the PIN changed, so whoever knew the old one starts again.</summary>
    public void LockAllExcept(string? keepId)
    {
        foreach (var id in _until.Keys.Where(id => id != keepId))
        {
            _until.TryRemove(id, out _);
        }
    }
}
