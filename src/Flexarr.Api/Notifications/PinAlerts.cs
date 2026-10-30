using Flexarr.Api.Authentication;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <summary>
/// When wrong guesses at the main profile's PIN lock someone out, says so in the log and on the main profile's phones:
/// that PIN guards the server's settings, and guessing it quietly is the danger. Like <see cref="PairingAlerts"/>, every
/// phone on the main profile hears it, whatever news it's chosen.
/// </summary>
public sealed partial class PinAlerts(IServiceScopeFactory scopes, SignIns signIns, ILogger<PinAlerts> logger)
{
    public const string Category = "flexarr.pin";

    /// <summary>Listens to <paramref name="guard"/> for the rest of the server's life.</summary>
    public void Watch(PinGuard guard)
    {
        ArgumentNullException.ThrowIfNull(guard);
        guard.CallerLockedOut += (profileId, callers) =>
        {
            if (profileId != PlaybackState.DefaultUser)
            {
                return;
            }

            var who = Describe(callers);
            LogLockedOut(who);
            _ = Task.Run(async () =>
            {
                try
                {
                    await SendAsync(who, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    LogFailed(ex);
                }
            });
        };
    }

    public async Task<int> SendAsync(string who, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var relay = scope.ServiceProvider.GetRequiredService<PushRelay>();
        if (!relay.IsEnabled)
        {
            return 0;
        }

        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var phones = await db.NotificationDevices.Where(d => d.ProfileId == PlaybackState.DefaultUser).ToListAsync(ct);
        if (phones.Count == 0)
        {
            return 0;
        }

        return await scope.ServiceProvider.GetRequiredService<Notifier>().SendAsync(db, phones, Message(who), null, ct);
    }

    public static PushMessage Message(string who) =>
        new("Someone's guessing your PIN", $"{who} got the main profile's PIN wrong too many times and has to wait. If it wasn't you, change the PIN in Settings › Profiles.",
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
            return $"The Tailscale account {login["tailscale:".Length..]}";
        }

        return callers.FirstOrDefault(c => c.StartsWith("address:", StringComparison.Ordinal)) is { } address
            ? $"A device at {address["address:".Length..]}"
            : "Someone";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Who} got the main profile's PIN wrong too many times and is locked out for a while")]
    private partial void LogLockedOut(string who);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell the main profile's phones about wrong PINs")]
    private partial void LogFailed(Exception ex);
}
