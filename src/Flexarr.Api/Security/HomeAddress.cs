using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Flexarr.Api.Configuration;
using Flexarr.Api.Updates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>
/// The server's address on the home network, told to household devices that joined by invite so they can use it while
/// they're home and their tunnel when they aren't (docs/remote-guests.md, Household devices at home). Never told to
/// guests.
/// </summary>
public sealed class HomeAddress(IServer server, IInstallEnvironment install, TimeProvider clock, IOptionsMonitor<FlexarrOptions> options)
{
    private static readonly TimeSpan Lasts = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private (Uri? Url, DateTimeOffset At)? _cached;

    /// <summary>
    /// <see cref="FlexarrOptions.HomeUrl"/> when set; else <c>http://&lt;LAN IPv4&gt;:&lt;port&gt;</c>, or null when it can't
    /// be known: in a container (its own address isn't the host's), before the server is listening, or with no home
    /// network.
    /// </summary>
    public Uri? Url()
    {
        // Set by hand (Docker, or a machine with several networks): that, as long as it's an http address.
        if (options.CurrentValue.HomeUrl is { Length: > 0 } configured)
        {
            return Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var set) && set.Scheme is "http" or "https" && !set.IsLoopback ? set : null;
        }

        var now = clock.GetUtcNow();
        lock (_lock)
        {
            if (_cached is { } cached && now - cached.At < Lasts)
            {
                return cached.Url;
            }
        }

        var url = install.Kind == InstallKind.Docker || ServerPort.Of(server) is not { } port || Lan() is not { } address
            ? null
            : new Uri($"http://{address}:{port}");
        lock (_lock)
        {
            _cached = (url, now);
        }

        return url;
    }

    /// <summary>
    /// The private IPv4 address of a running Ethernet or Wi-Fi interface with a gateway (the one the home network routes
    /// to), ahead of any other kind, and a VPN's or a virtual machine's adapter last. Null on any trouble reading them: the
    /// device then stays on its tunnel, and nothing asking for it fails.
    /// </summary>
    private static IPAddress? Lan()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp))
                .Select(n => (Interface: n, Properties: n.GetIPProperties()))
                .Where(n => n.Properties.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                // A VPN's or virtual machine's adapter only when there's nothing else (Windows' Hyper-V switch can carry
                // the home network itself).
                .OrderBy(n => Virtual(n.Interface) ? 2 : n.Interface.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.GigabitEthernet ? 0 : 1)
                .SelectMany(n => n.Properties.UnicastAddresses.Select(u => u.Address))
                .FirstOrDefault(IsHome);
        }
#pragma warning disable CA1031 // Any failure means "not known": /me and joining must never fail over it.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>VPNs' and virtual machines' adapters: seldom where the home network reaches this machine.</summary>
    internal static bool Virtual(NetworkInterface n) => Virtual(n.Name) || Virtual(n.Description);

    internal static bool Virtual(string name) =>
        VirtualNames.Any(v => name.StartsWith(v, StringComparison.OrdinalIgnoreCase))
        || name.Contains("virtual", StringComparison.OrdinalIgnoreCase) || name.Contains("vpn", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] VirtualNames = ["utun", "tun", "tap", "wg", "tailscale", "zt", "docker", "br-", "veth", "virbr", "vmnet", "vboxnet", "bridge", "awdl", "llw", "ipsec", "ppp"];

    /// <summary>A private IPv4 address (10/8, 172.16/12, 192.168/16): not loopback, link-local or Tailscale's range.</summary>
    internal static bool IsHome(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }
}
