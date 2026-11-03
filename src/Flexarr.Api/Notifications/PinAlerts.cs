using Flexarr.Api.Authentication;
using Flexarr.Api.Localization;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <summary>
/// When wrong guesses at the admin PIN lock someone out, says so in the log and on the main profile's phones:
/// that PIN guards the server's settings, and guessing it quietly is the danger. Like <see cref="PairingAlerts"/>, every
/// phone on the main profile hears it, whatever news it's chosen.
/// </summary>
public sealed partial class PinAlerts(IServiceScopeFactory scopes, SignIns signIns, TimeProvider clock, ILogger<PinAlerts> logger)
{
    public const string Category = "flexarr.pin";

    /// <summary>One notice per spell of guessing: a guesser known by two names (a login and an address) is one person.</summary>
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(10);

    private readonly Lock _sending = new();
    private DateTimeOffset _lastSent = DateTimeOffset.MinValue;

    /// <summary>Listens to <paramref name="guard"/> for the rest of the server's life.</summary>
    public void Watch(PinGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        guard.CallerLockedOut += (profileId, callers) =>
        {
            if (profileId != PinGuard.AdminKey && profileId != PlaybackState.DefaultUser)
            {
                return;
            }

            // In English for the log; each phone's notice describes them again in its profile's language.
            using (Loc.Use(null))
            {
                LogLockedOut(Describe(callers));
            }

            var now = clock.GetUtcNow();
            lock (_sending)
            {
                if (now - _lastSent < Quiet)
                {
                    return;
                }

                _lastSent = now;
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(callers, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogFailed(ex);
                }
            });
        };
    }

    public async Task<int> SendAsync(IReadOnlyList<string> callers, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var relay = scope.ServiceProvider.GetRequiredService<PushRelay>();
        if (!relay.IsEnabled)
        {
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var phones = await PairingAlerts.MainProfilePhonesAsync(db, scope.ServiceProvider.GetRequiredService<SignIns>(), ct);
        if (phones.Count == 0)
        {
            return 0;
        }

        var languages = scope.ServiceProvider.GetRequiredService<ProfileLanguages>();
        var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
        var sent = 0;
        foreach (var profile in phones.GroupBy(p => p.ProfileId))
        {
            using var language = Loc.Use(await languages.OfAsync(profile.Key, ct));
            sent += await notifier.SendAsync(db, profile, Message(Describe(callers)), null, ct);
        }

        return sent;
    }

    /// <summary>In the current language (<see cref="Loc.Use"/>).</summary>
    public static PushMessage Message(string who) =>
        new(Loc.T("Someone's guessing your PIN"),
            Loc.T("{who} got the admin PIN or your profile's PIN wrong too many times and has to wait. If it wasn't you, change them on the web, in Settings.", new { who }),
            Category, Thread: Category);

    /// <summary>"Living Room TV", "the Tailscale account sam@example.com", or "a device at 192.168.1.20".</summary>
    internal string Describe(IReadOnlyList<string> callers)
    {
        foreach (var caller in callers)
        {
            if (caller.StartsWith("sign-in:", StringComparison.Ordinal) && signIns.List().FirstOrDefault(s => s.Id == caller["sign-in:".Length..]) is { } signIn)
            {
                return signIn.Name;
            }
        }

        if (callers.FirstOrDefault(c => c.StartsWith("tailscale:", StringComparison.Ordinal)) is { } login)
        {
            return Loc.T("The Tailscale account {login}", new { login = login["tailscale:".Length..] });
        }

        return callers.FirstOrDefault(c => c.StartsWith("address:", StringComparison.Ordinal)) is { } address
            ? Loc.T("A device at {address}", new { address = address["address:".Length..] })
            : Loc.T("Someone");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Who} got the admin PIN (or the main profile's own) wrong too many times and is locked out for a while")]
    private partial void LogLockedOut(string who);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell the main profile's phones about wrong PINs")]
    private partial void LogFailed(Exception ex);
}
