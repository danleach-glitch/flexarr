using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>
/// The door for family guests' devices. Anything from a tailnet device carrying the guest tag is refused unless that
/// device has a live guest sign-in, whatever it asks for (the web app, artwork, pages that need no sign-in); the one
/// exception is redeeming an invite. So removing a guest's device in Settings shuts it out completely, even while it
/// stays on the tailnet until the owner deletes it there.
/// </summary>
public static class GuestGate
{
    public const string RedeemPath = "/api/v1/invites/redeem";

    public static IApplicationBuilder UseFlexarrGuestGate(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var peer = RequestPeer.Of(http);
        if (peer is null || !NetworkZones.IsTailnetAddress(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer))
        {
            await next(http);
            return;
        }

        // Nothing to guard on a server that has never had family invites set up: no lookup for the household's own
        // devices. Once they have been, the gate stays, so a removed guest is shut out even after invites are switched off.
        var settings = http.RequestServices.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue.RemoteGuests;
        var signIns = http.RequestServices.GetRequiredService<SignIns>();
        if (!settings.Enabled && !settings.HasClient && !signIns.HasAnyGuest)
        {
            await next(http);
            return;
        }

        var whois = http.RequestServices.GetRequiredService<ITailnetWhois>();
        var (kind, address) = await GuestDevices.LookUpAsync(http, whois, settings);
        if (kind == GuestLookup.NotGuest
            || signIns.HasGuestAt(address!)
            || kind == GuestLookup.Guest && HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals(RedeemPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        if (kind == GuestLookup.Unknown)
        {
            // Tailscale couldn't say whose device this is (it's slow or restarting). It might be a removed guest's, so
            // it waits rather than being let in; answers are kept, so this only happens before the first one.
            http.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            http.Response.Headers.RetryAfter = "5";
            await http.Response.WriteAsJsonAsync(new { title = "Flexarr couldn't check this device just now. Try again in a moment.", status = 503 }, http.RequestAborted);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        // guestRemoved lets the apps tell this from any other refusal, and sign out rather than show an error.
        await http.Response.WriteAsJsonAsync(new { title = "This device was removed from the server. Ask its owner for a new invite.", status = 403, guestRemoved = true },
            http.RequestAborted);
    });
}
