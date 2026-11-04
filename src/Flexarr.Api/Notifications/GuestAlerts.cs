using Flexarr.Api.Authentication;
using Flexarr.Api.Localization;
using Flexarr.Core.Data;
using Flexarr.Core.Localization;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Notifications;

/// <summary>Family invites news for the main profile's phones, sent in the background.</summary>
public sealed partial class GuestAlerts(IServiceScopeFactory scopes, ILogger<GuestAlerts> logger)
{
    public const string Category = "flexarr.guests";

    /// <summary>The tailnet policy stopped confining guests to Flexarr, so invites are paused.</summary>
    public void Paused() => Send(() => new PushMessage(Loc.T("Family invites paused"),
        Loc.T("Your Tailscale policy now lets guests reach more than Flexarr, and guests who’ve joined can too. Open Settings › Remote access to fix it."), Category,
        Thread: Category, Open: new Dictionary<string, string> { ["settings"] = "access" }), "guests-paused");

    /// <summary>
    /// A guest used an invite, so the owner hears straight away: one that went astray (forwarded, intercepted) shows up
    /// as someone unexpected joining, to be removed in Settings.
    /// </summary>
    public void Joined(string deviceName, string profileId) => Send(async (scope, ct) =>
    {
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = await db.Profiles.AsNoTracking().Where(p => p.Id == profileId).Select(p => p.Name).FirstOrDefaultAsync(ct);
        return () => JoinedMessage(deviceName, profile);
    }, $"guest-joined-{deviceName}");

    /// <summary>In the current language (<see cref="Loc.Use"/>).</summary>
    public static PushMessage JoinedMessage(string deviceName, string? profileName) => new(Loc.T("{device} joined", new { device = deviceName }),
        profileName is null
            ? Loc.T("Someone used your invite. Not expected? Remove the device in Settings › Remote access.")
            : Loc.T("Watching as {profile}. Not expected? Remove it in Settings › Remote access.", new { profile = profileName }),
        Category, Thread: Category, Open: new Dictionary<string, string> { ["settings"] = "access" });

    private void Send(Func<PushMessage> message, string collapseId) => Send((_, _) => Task.FromResult(message), collapseId);

    /// <param name="make">Looks up what the message needs, and gives what writes it: called once per profile, in its language.</param>
    private void Send(Func<IServiceScope, CancellationToken, Task<Func<PushMessage>>> make, string collapseId) => _ = Task.Run(async () =>
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
                var write = await make(scope, CancellationToken.None);
                var languages = scope.ServiceProvider.GetRequiredService<ProfileLanguages>();
                var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
                foreach (var profile in phones.GroupBy(p => p.ProfileId))
                {
                    using var language = Loc.Use(await languages.OfAsync(profile.Key, CancellationToken.None));
                    await notifier.SendAsync(db, profile, write(), collapseId, CancellationToken.None);
                }
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
