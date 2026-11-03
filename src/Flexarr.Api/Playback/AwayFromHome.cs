using System.Net;
using System.Net.Sockets;

namespace Flexarr.Api.Playback;

/// <summary>How a stream's quality is chosen for someone watching away from home (see docs/fast-start.md).</summary>
public enum AwayFromHomeQuality
{
    /// <summary>Lowered only when the file needs more than the viewer's connection carries, by the speed their app measured.</summary>
    Automatic,

    /// <summary>As at home: the file as it is, whenever the device can play it.</summary>
    Original,
}

/// <summary>Whether a viewer is away from home, and the cap that fits their connection then.</summary>
public static class AwayFromHome
{
    /// <summary>A stream is held to this share of the measured speed, leaving room for the speed to dip.</summary>
    public const double Headroom = 0.75;

    /// <summary>Never capped below this: lower than this looks worse than a short wait.</summary>
    public const long MinBitrate = 1_500_000;

    /// <summary>
    /// The cap for a stream of a <paramref name="fileBitrate"/> file, or null for none: only with Automatic, a
    /// <paramref name="remote"/> viewer and a speed their app measured that the file (when its bitrate is known) needs
    /// more than.
    /// </summary>
    public static long? Cap(AwayFromHomeQuality quality, bool remote, long? measuredBitrate, long? fileBitrate)
    {
        if (quality != AwayFromHomeQuality.Automatic || !remote || measuredBitrate is not > 0)
        {
            return null;
        }

        var fits = Math.Max(MinBitrate, (long)(measuredBitrate.Value * Headroom));
        return fileBitrate is > 0 and var bitrate && bitrate <= fits ? null : fits;
    }

    /// <summary>
    /// Away from home: the request (or, behind a proxy, the client it forwarded for) isn't from this machine or a
    /// private network. Tailscale's addresses (100.64.0.0/10) count as away: those devices are anywhere.
    /// </summary>
    public static bool IsRemote(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        return !IsHome(http.Connection.RemoteIpAddress) || !IsHome(Security.RequestPeer.Of(http));
    }

    public static bool IsHome(IPAddress? address)
    {
        if (address is null)
        {
            return true; // A test host, an in-process call.
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254);
    }
}
