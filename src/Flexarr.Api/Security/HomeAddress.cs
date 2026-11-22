using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Flexarr.Api.Updates;
using Microsoft.AspNetCore.Hosting.Server;

namespace Flexarr.Api.Security;

/// <summary>
/// The server's address on the home network, told to household devices that joined by invite so they can use it while
/// they're home and their tunnel when they aren't (docs/remote-guests.md, Household devices at home). Never told to
/// guests.
/// </summary>
public sealed class HomeAddress(IServer server, IInstallEnvironment install, TimeProvider clock)
{
    private static readonly TimeSpan Lasts = TimeSpan.FromMinutes(1);
    private readonly Lock _lock = new();
    private (Uri? Url, DateTimeOffset At)? _cached;

    /// <summary>
    /// <c>http://&lt;LAN IPv4&gt;:&lt;port&gt;</c>, or null when it can't be known: in a container (its own address isn't
    /// the host's), before the server is listening, or with no home network.
    /// </summary>
    public Uri? Url()
    {
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

    /// <summary>The first private IPv4 address of a running interface with a gateway (the one the home network routes to).</summary>
    private static IPAddress? Lan()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
                .SelectMany(p => p.UnicastAddresses.Select(u => u.Address))
                .FirstOrDefault(IsHome);
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

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
