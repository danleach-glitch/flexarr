using System.Net;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class NetworkZoneTests
{
    [Theory]
    [InlineData("192.168.1.50", NetworkZone.Local)]
    [InlineData("10.0.0.5", NetworkZone.Local)]
    [InlineData("127.0.0.1", NetworkZone.Local)]
    [InlineData("100.64.218.96", NetworkZone.Tailnet)]
    [InlineData("fd7a:115c:a1e0::1", NetworkZone.Tailnet)]
    [InlineData("100.128.0.1", NetworkZone.Internet)] // just outside 100.64.0.0/10
    [InlineData("198.51.100.7", NetworkZone.Internet)]
    public void ClassifiesAddresses(string address, NetworkZone expected) =>
        Assert.Equal(expected, NetworkZones.Classify(IPAddress.Parse(address)));

    [Theory]
    [InlineData("CF-Connecting-IP")]
    [InlineData("CF-Ray")]
    [InlineData("Tailscale-Funnel-Request")]
    public void TunnelledRequestsFromLoopback_AreInternet(string header)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers[header] = "1";

        Assert.Equal(NetworkZone.Internet, NetworkZones.Of(http));
    }

    [Fact]
    public void UnresolvedForwardedFor_FromLoopback_IsInternet()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers["X-Forwarded-For"] = "203.0.113.9";

        Assert.Equal(NetworkZone.Internet, NetworkZones.Of(http));
    }

    [Theory]
    [InlineData(false, NetworkZone.Local)]
    [InlineData(true, NetworkZone.Internet)]
    public void PlainLoopback_CountsAsTheInternet_WhenAProxyHereMustSayWhoItsFor(bool loopbackNeedsForwarding, NetworkZone expected)
    {
        // What a bare `proxy_pass http://127.0.0.1:8383;` sends: nothing about the visitor.
        var services = new ServiceCollection().AddOptions().Configure<FlexarrOptions>(o => o.LoopbackNeedsForwarding = loopbackNeedsForwarding);
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Connection.RemoteIpAddress = IPAddress.Loopback;

        Assert.Equal(expected, NetworkZones.Of(http));
    }

    [Theory]
    [InlineData("100.101.102.103", NetworkZone.Tailnet)] // over Tailscale, to this machine's own tailnet address
    [InlineData("192.168.1.5", NetworkZone.Internet)] // some other network that uses the same range
    public void ATailnetAddress_IsOnlyBelieved_OverTailscale(string arrivedOn, NetworkZone expected)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse("100.64.218.96");
        http.Connection.LocalIpAddress = IPAddress.Parse(arrivedOn);

        Assert.Equal(expected, NetworkZones.Of(http));
    }

    [Fact]
    public void ATailnetAddress_IsBelieved_InAContainer()
    {
        // The host's port forwarding hands the connection over to the container's own address.
        var services = new ServiceCollection().AddSingleton<Updates.IInstallEnvironment>(new FakeInstall(Updates.InstallKind.Docker));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Connection.RemoteIpAddress = IPAddress.Parse("100.64.218.96");
        http.Connection.LocalIpAddress = IPAddress.Parse("172.17.0.2");

        Assert.Equal(NetworkZone.Tailnet, NetworkZones.Of(http));
    }

    [Theory]
    [InlineData(Updates.InstallKind.Docker, "172.17.0.1", true)]
    [InlineData(Updates.InstallKind.Docker, "192.168.1.20", false)] // someone at home, as Docker's own forwarding passes them on
    [InlineData(Updates.InstallKind.Other, "172.17.0.1", false)]
    public void TheContainersGateway_IsntTheHomeNetwork(Updates.InstallKind kind, string from, bool gateway)
    {
        var services = new ServiceCollection().AddSingleton<Updates.IInstallEnvironment>(new FakeInstall(kind));
        var http = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        http.Connection.RemoteIpAddress = IPAddress.Parse(from);
        var gateways = NetworkZones.Gateways([
            "Iface\tDestination\tGateway \tFlags\tRefCnt\tUse\tMetric\tMask\t\tMTU\tWindow\tIRTT",
            "eth0\t00000000\t010011AC\t0003\t0\t0\t0\t00000000\t0\t0\t0",
            "eth0\t000011AC\t00000000\t0001\t0\t0\t0\t0000FFFF\t0\t0\t0",
        ]);

        Assert.Equal([IPAddress.Parse("172.17.0.1")], gateways);
        Assert.Equal(gateway, NetworkZones.FromContainerGateway(http, gateways));
    }

    private sealed record FakeInstall(Updates.InstallKind Kind) : Updates.IInstallEnvironment
    {
        public string? Root => null;

        public string? DotnetRoot => null;
    }

    [Theory]
    [InlineData("192.168.1.10:8383", true)]
    [InlineData("[fd7a:115c:a1e0::1]:8443", true)]
    [InlineData("localhost:5173", true)]
    [InlineData("mini", true)]
    [InlineData("Mac-mini.local", true)]
    [InlineData("flexarr.tail1234.ts.net", true)]
    [InlineData("media.example.com", true)] // listed in KnownHosts
    [InlineData("attacker.example", false)]
    [InlineData("mini.local.attacker.example", false)]
    public void KnowsWhichNamesAreItsOwn(string host, bool known)
    {
        var http = new DefaultHttpContext();
        http.Request.Host = HostString.FromUriComponent(host);

        Assert.Equal(known, NetworkZones.AddressedToKnownHost(http, ["media.example.com"]));
    }
}
