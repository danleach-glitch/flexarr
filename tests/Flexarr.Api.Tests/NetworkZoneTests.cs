using System.Net;
using Flexarr.Api.Security;
using Microsoft.AspNetCore.Http;

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
