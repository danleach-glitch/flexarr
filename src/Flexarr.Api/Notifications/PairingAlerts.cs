using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <summary>
/// Tells the main profile's phones that a device is asking to connect, so approving it is a tap there rather than a
/// trip to the web app. Every phone on the main profile hears it, whatever news it's chosen: it's the household's
/// door, not news. Its Allow and Don't allow buttons ask for the phone to be unlocked, and the code is in the
/// notification to check against the device's screen. One notification per code: a device left showing its code keeps
/// the same one (see <see cref="Pairings.Status"/>), and the code a device asks for by itself just after "Don't allow" isn't announced.
/// </summary>
public sealed partial class PairingAlerts(IServiceScopeFactory scopes, ILogger<PairingAlerts> logger)
{
    public const string Category = "flexarr.pairing";

    /// <summary>Sent in the background: the device asking shouldn't wait on Apple or Google to see its code.</summary>
    public void Send(PairingDetailsDto pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        _ = Task.Run(async () =>
        {
            try
            {
                await SendAsync(pairing, CancellationToken.None);
            }
            catch (Exception ex)
            {
                LogFailed(ex);
            }
        });
    }

    public async Task<int> SendAsync(PairingDetailsDto pairing, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pairing);
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

        var message = Message(pairing);
        return await scope.ServiceProvider.GetRequiredService<Notifier>().SendAsync(db, phones, message, $"pairing-{pairing.Code}", ct);
    }

    public static PushMessage Message(PairingDetailsDto pairing)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var kind = Kind(pairing.DeviceType);
        return new PushMessage($"{pairing.DeviceName} wants to connect",
            $"{(kind is null ? "" : $"{kind} · ")}Allow it if its screen shows {pairing.Code}.", Category,
            Thread: Category, Open: new Dictionary<string, string> { ["pairing"] = pairing.Code });
    }

    /// <summary>The kind of device, as people call it.</summary>
    public static string? Kind(Authentication.DeviceType? type) => type switch
    {
        Authentication.DeviceType.AppleTv => "Apple TV",
        Authentication.DeviceType.IPhone => "iPhone",
        Authentication.DeviceType.IPad => "iPad",
        Authentication.DeviceType.AndroidPhone => "Android phone",
        Authentication.DeviceType.AndroidTablet => "Android tablet",
        Authentication.DeviceType.AndroidTv => "Android TV",
        _ => null,
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't tell the main profile's phones about a device asking to connect")]
    private partial void LogFailed(Exception ex);
}
