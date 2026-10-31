using Flexarr.Api.Configuration;
using Flexarr.Api.Notifications;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>
/// Checks the tailnet policy every hour while family invites are set up, so a policy widened later pauses invites
/// (and the guests' redeeming) and tells the owner once, rather than waiting for the next invite to notice.
/// </summary>
public sealed class GuestPolicyWatcher(GuestPolicy policy, GuestAlerts alerts, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock) : BackgroundService
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

            var check = await policy.CheckAsync(stoppingToken, fresh: true);
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
}
