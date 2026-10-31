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

        var settings = http.RequestServices.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue.RemoteGuests;
        var whois = http.RequestServices.GetRequiredService<ITailnetWhois>();
        if (await GuestDevices.AddressOfAsync(http, whois, settings) is not { } address
            || http.RequestServices.GetRequiredService<SignIns>().HasGuestAt(address)
            || HttpMethods.IsPost(http.Request.Method) && http.Request.Path.Equals(RedeemPath, StringComparison.OrdinalIgnoreCase))
        {
            await next(http);
            return;
        }

        http.Response.StatusCode = StatusCodes.Status403Forbidden;
        await http.Response.WriteAsJsonAsync(new { title = "This device was removed from the server. Ask its owner for a new invite.", status = 403 },
            http.RequestAborted);
    });
}
