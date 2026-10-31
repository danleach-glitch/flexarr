using System.Net;
using Flexarr.Api.Configuration;

namespace Flexarr.Api.Security;

/// <summary>Recognising a family guest's device: a tailnet device carrying the guest tag, connected straight to Flexarr.</summary>
public static class GuestDevices
{
    /// <summary>
    /// The tailnet address of the guest device this request came straight from, or null when it didn't come from one.
    /// Guests reach Flexarr directly on this server's tailnet address (never through <c>tailscale serve</c>, whose
    /// port the policy keeps from them), so the connection's own address is the device's.
    /// </summary>
    public static async Task<string?> AddressOfAsync(HttpContext http, ITailnetWhois whois, RemoteGuestOptions settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(whois);
        ArgumentNullException.ThrowIfNull(settings);
        var peer = RequestPeer.Of(http);
        if (peer is null)
        {
            return null;
        }

        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        if (!NetworkZones.IsTailnetAddress(address))
        {
            return null;
        }

        var tags = await whois.TagsAsync(address, http.RequestAborted);
        return tags.Contains(settings.Tag, StringComparer.OrdinalIgnoreCase) ? address.ToString() : null;
    }

    /// <summary>Whether an address is in Tailscale's ranges (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    public static bool IsTailnet(IPAddress address) => NetworkZones.IsTailnetAddress(address);
}
