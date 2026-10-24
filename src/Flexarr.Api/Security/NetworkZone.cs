using System.Net;
using System.Net.Sockets;

namespace Flexarr.Api.Security;

/// <summary>Where a request comes from, which decides how much proof of identity it needs.</summary>
public enum NetworkZone
{
    /// <summary>This machine or the home network (private addresses, no proxy in between).</summary>
    Local,

    /// <summary>One of your own devices over Tailscale (100.64.0.0/10, fd7a:115c:a1e0::/48).</summary>
    Tailnet,

    /// <summary>Anyone else, including everything arriving through Cloudflare Tunnel or Tailscale Funnel.</summary>
    Internet,
}

public static class NetworkZones
{
    /// <summary>Headers that only appear on traffic relayed from the public internet.</summary>
    private static readonly string[] PublicRelayHeaders = ["CF-Connecting-IP", "CF-Ray", "Tailscale-Funnel-Request"];

    /// <summary>
    /// Classifies a request. Run after the forwarded-headers middleware, so the address is the real client's rather
    /// than the local proxy's. Relay markers always mean internet: a tunnel delivers requests from 127.0.0.1, and
    /// mistaking that for "this machine" would let anyone on the internet past local-network rules.
    /// </summary>
    public static NetworkZone Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        if (PublicRelayHeaders.Any(h => http.Request.Headers.ContainsKey(h)))
        {
            return NetworkZone.Internet;
        }

        // A proxy that forwarded for a client we couldn't resolve (an untrusted proxy, or a chain) is not local either.
        if (http.Request.Headers.ContainsKey("X-Forwarded-For") && IsLoopback(http.Connection.RemoteIpAddress))
        {
            return NetworkZone.Internet;
        }

        return Classify(http.Connection.RemoteIpAddress);
    }

    public static NetworkZone Classify(IPAddress? address)
    {
        if (address is null)
        {
            return NetworkZone.Internet;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IsTailnet(address))
        {
            return NetworkZone.Tailnet;
        }

        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
        {
            return NetworkZone.Local;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            if (b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254))
            {
                return NetworkZone.Local;
            }
        }

        return NetworkZone.Internet;
    }

    private static bool IsTailnet(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 100 && (b[1] & 0xC0) == 64 // 100.64.0.0/10, Tailscale's (CGNAT) range
            : b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    private static bool IsLoopback(IPAddress? address) => address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);
}
