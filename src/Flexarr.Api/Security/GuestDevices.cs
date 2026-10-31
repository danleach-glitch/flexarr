using System.Net;
using Flexarr.Api.Configuration;

namespace Flexarr.Api.Security;

/// <summary>What Tailscale says about the device a request came from.</summary>
public enum GuestLookup
{
    NotGuest,
    Guest,

    /// <summary>A tailnet device Tailscale couldn't identify just now.</summary>
    Unknown,
}

/// <summary>Recognising a family guest's device: a tailnet device carrying the guest tag, connected straight to Flexarr.</summary>
public static class GuestDevices
{
    /// <summary>
    /// The tailnet address of the guest device this request came straight from, or null when it didn't come from one.
    /// Guests reach Flexarr directly on this server's tailnet address (never through <c>tailscale serve</c>, whose
    /// port the policy keeps from them), so the connection's own address is the device's.
    /// </summary>
    public static async Task<string?> AddressOfAsync(HttpContext http, ITailnetWhois whois, RemoteGuestOptions settings) =>
        await LookUpAsync(http, whois, settings) is (GuestLookup.Guest, var address) ? address : null;

    /// <summary>
    /// Whether this request came straight from a guest device, from some other device, or from a tailnet device
    /// Tailscale couldn't identify just now (which must never be taken for a household one). The address is the
    /// connection's tailnet address, for Guest and Unknown.
    /// </summary>
    public static async Task<(GuestLookup Kind, string? Address)> LookUpAsync(HttpContext http, ITailnetWhois whois, RemoteGuestOptions settings)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(whois);
        ArgumentNullException.ThrowIfNull(settings);
        var peer = RequestPeer.Of(http);
        if (peer is null)
        {
            return (GuestLookup.NotGuest, null);
        }

        var address = peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer;
        if (!NetworkZones.IsTailnetAddress(address))
        {
            return (GuestLookup.NotGuest, null);
        }

        return await whois.TagsAsync(address, http.RequestAborted) switch
        {
            null => (GuestLookup.Unknown, address.ToString()),
            var tags when tags.Contains(settings.Tag, StringComparer.OrdinalIgnoreCase) => (GuestLookup.Guest, address.ToString()),
            _ => (GuestLookup.NotGuest, null),
        };
    }

    /// <summary>Whether an address is in Tailscale's ranges (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    public static bool IsTailnet(IPAddress address) => NetworkZones.IsTailnetAddress(address);
}
