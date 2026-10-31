using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

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

    /// <summary>Headers a proxy adds to say who it's forwarding for.</summary>
    private static readonly string[] ForwardingHeaders = ["X-Forwarded-For", "Forwarded", "X-Real-IP"];

    /// <summary>
    /// Names that only resolve on the home network or tailnet (suffixes nobody can register, and Tailscale's own), so
    /// another website can't point them at you.
    /// </summary>
    private static readonly string[] PrivateSuffixes =
        [".local", ".localhost", ".lan", ".home", ".home.arpa", ".internal", ".localdomain", ".intranet", ".private", ".corp", ".ts.net"];

    /// <summary>
    /// Classifies a request. Run after the forwarded-headers middleware, so the address is the real client's rather
    /// than the local proxy's. Relay markers always mean internet: a tunnel delivers requests from 127.0.0.1, and
    /// mistaking that for "this machine" would let anyone on the internet past local-network rules. So does any
    /// forwarded request Flexarr can't trace to its client: one from a proxy that isn't trusted (in Docker, or on
    /// another machine), or one whose proxy said who it was forwarding for in a way Flexarr doesn't read.
    /// </summary>
    public static NetworkZone Of(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);

        var headers = http.Request.Headers;
        if (PublicRelayHeaders.Any(headers.ContainsKey))
        {
            return NetworkZone.Internet;
        }

        var peer = RequestPeer.Of(http);
        var resolved = !Equals(Normalize(peer), Normalize(http.Connection.RemoteIpAddress));
        if (resolved || ForwardingHeaders.Any(headers.ContainsKey))
        {
            if (!IsTrustedProxy(http, peer))
            {
                return NetworkZone.Internet;
            }

            // The middleware removes X-Forwarded-For once it has found the client. Left over means it couldn't (a
            // chain of proxies); X-Real-IP or Forwarded on their own aren't read at all.
            if (headers.ContainsKey("X-Forwarded-For") || !resolved)
            {
                return NetworkZone.Internet;
            }
        }
        else if (Normalize(peer) is { } direct && IPAddress.IsLoopback(direct)
            && http.RequestServices?.GetService<IOptionsMonitor<Configuration.FlexarrOptions>>()?.CurrentValue.LoopbackNeedsForwarding == true)
        {
            // A reverse proxy here that doesn't say who it's forwarding for makes every visitor look like this machine.
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

        if (IsTailnetAddress(address))
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

    /// <summary>This machine, or a proxy listed in <c>TrustedProxies</c>.</summary>
    public static bool IsTrustedProxy(HttpContext http, IPAddress? peer)
    {
        ArgumentNullException.ThrowIfNull(http);
        peer = Normalize(peer);
        if (peer is null)
        {
            return false;
        }

        if (IPAddress.IsLoopback(peer))
        {
            return true;
        }

        var forwarding = http.RequestServices?.GetService<IOptions<ForwardedHeadersOptions>>()?.Value;
        return forwarding is not null && (forwarding.KnownProxies.Any(p => Equals(Normalize(p), peer)) || forwarding.KnownIPNetworks.Any(n => n.Contains(peer)));
    }

    /// <summary>
    /// Whether the request was addressed to a name this server goes by. A web page can make its own domain name point
    /// at your server (DNS rebinding), and then a browser on your network would talk to Flexarr as that page; the Host
    /// header still carries the page's name, so the home-network and Tailscale sign-ins only apply to names that
    /// can't belong to someone else: IP addresses, single-word names, private suffixes such as .local, this machine's
    /// name, and any listed in <c>KnownHosts</c>.
    /// </summary>
    public static bool AddressedToKnownHost(HttpContext http, IReadOnlyCollection<string> knownHosts)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(knownHosts);

        var host = RequestPeer.HostOf(http).Host.Trim('[', ']').TrimEnd('.').ToLowerInvariant();
        if (host.Length == 0)
        {
            return false;
        }

        if (IPAddress.TryParse(host, out _) || !host.Contains('.', StringComparison.Ordinal))
        {
            return true;
        }

        if (PrivateSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)))
        {
            return true;
        }

        return string.Equals(host, Dns.GetHostName(), StringComparison.OrdinalIgnoreCase)
            || knownHosts.Any(k => string.Equals(k.Trim().TrimEnd('.'), host, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsTailnetAddress(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return address.AddressFamily == AddressFamily.InterNetwork
            ? b[0] == 100 && (b[1] & 0xC0) == 64 // 100.64.0.0/10, Tailscale's (CGNAT) range
            : b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }

    private static IPAddress? Normalize(IPAddress? address) => address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
