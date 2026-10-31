using Flexarr.Api.Configuration;
using Flexarr.Api.Notifications;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>
/// Checks the tailnet policy every hour while family invites are set up, so a policy widened later pauses invites
/// (and the guests' redeeming) and tells the owner once, rather than waiting for the next invite to notice.
/// </summary>
public sealed partial class GuestPolicyWatcher(GuestPolicy policy, GuestAlerts alerts, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<GuestPolicyWatcher> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        bool? wasSafe = null;
        using var timer = new PeriodicTimer(Interval, clock);
        do
        {
            var settings = options.CurrentValue.RemoteGuests;
            if (!settings.Enabled || !settings.HasClient)
            {
                wasSafe = null;
                continue;
            }

            GuestPolicyCheck check;
            try
            {
                check = await policy.CheckAsync(stoppingToken, fresh: true);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                // Never let a check that went wrong stop the server (a background service's exception stops the host).
                LogCheckFailed(ex);
                continue;
            }

            // Only the policy itself pauses invites with an alert; Tailscale being unreachable for a moment doesn't.
            if (!check.Validated)
            {
                continue;
            }

            if (wasSafe == true && !check.Safe)
            {
                alerts.Paused();
            }

            wasSafe = check.Safe;
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't check the tailnet policy for family invites")]
    private partial void LogCheckFailed(Exception ex);
}
