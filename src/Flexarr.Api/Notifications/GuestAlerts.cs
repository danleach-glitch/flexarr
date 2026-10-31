using Flexarr.Api.Authentication;
using Flexarr.Core.Data;

namespace Flexarr.Api.Notifications;

/// <summary>Family invites news for the main profile's phones, sent in the background.</summary>
public sealed partial class GuestAlerts(IServiceScopeFactory scopes, ILogger<GuestAlerts> logger)
{
    public const string Category = "flexarr.guests";

    /// <summary>The tailnet policy stopped confining guests to Flexarr, so invites are paused.</summary>
    public void Paused() => Send(new PushMessage("Family invites paused",
        "Your Tailscale policy now lets guests reach more than Flexarr. Open Settings › Access to fix it.", Category, Thread: Category,
        Open: new Dictionary<string, string> { ["settings"] = "access" }), "guests-paused");

    /// <summary>A guest used an invite. Defined with c-12; a no-op until then.</summary>
    public void Joined(string deviceName, string profileId)
    {
    }

    private void Send(PushMessage message, string collapseId) => _ = Task.Run(async () =>
    {
        try
        {
            using var scope = scopes.CreateScope();
            var relay = scope.ServiceProvider.GetRequiredService<PushRelay>();
            if (!relay.IsEnabled)
            {
                return;
            }

            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var phones = await PairingAlerts.MainProfilePhonesAsync(db, scope.ServiceProvider.GetRequiredService<SignIns>(), CancellationToken.None);
            if (phones.Count > 0)
            {
                await scope.ServiceProvider.GetRequiredService<Notifier>().SendAsync(db, phones, message, collapseId, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            LogFailed(ex);
        }
    });

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't send a family invites notification")]
    private partial void LogFailed(Exception ex);
}
