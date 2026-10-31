using Flexarr.Api.Authentication;
using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <summary>Family invites news for the main profile's phones, sent in the background.</summary>
public sealed partial class GuestAlerts(IServiceScopeFactory scopes, ILogger<GuestAlerts> logger)
{
    public const string Category = "flexarr.guests";

    /// <summary>The tailnet policy stopped confining guests to Flexarr, so invites are paused.</summary>
    public void Paused() => Send(new PushMessage("Family invites paused",
        "Your Tailscale policy now lets guests reach more than Flexarr, and guests who've joined can too. Open Settings › Remote access to fix it.", Category, Thread: Category,
        Open: new Dictionary<string, string> { ["settings"] = "access" }), "guests-paused");

    /// <summary>
    /// A guest used an invite, so the owner hears straight away: one that went astray (forwarded, intercepted) shows up
    /// as someone unexpected joining, to be removed in Settings.
    /// </summary>
    public void Joined(string deviceName, string profileId) => Send(async (scope, ct) =>
    {
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        return JoinedMessage(deviceName, profile);
    }, $"guest-joined-{deviceName}");

    public static PushMessage JoinedMessage(string deviceName, string? profileName) => new($"{deviceName} joined",
        profileName is null ? "Someone used your invite. Not expected? Remove the device in Settings › Remote access." : $"Watching as {profileName}. Not expected? Remove it in Settings › Remote access.",
        Category, Thread: Category, Open: new Dictionary<string, string> { ["settings"] = "access" });

    private void Send(PushMessage message, string collapseId) => Send((_, _) => Task.FromResult(message), collapseId);

    private void Send(Func<IServiceScope, CancellationToken, Task<PushMessage>> make, string collapseId) => _ = Task.Run(async () =>
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
                var message = await make(scope, CancellationToken.None);
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
