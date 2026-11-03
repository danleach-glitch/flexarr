using System.Net;
using Flexarr.Api.Playback;
using Microsoft.AspNetCore.Http;

namespace Flexarr.Api.Tests;

public class AwayFromHomeTests
{
    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("192.168.1.20", false)]
    [InlineData("10.0.2.2", false)]
    [InlineData("100.101.102.103", true)] // Tailscale: anywhere
    [InlineData("81.2.69.160", true)]
    public void RemoteIsAnythingButTheHomeNetwork(string address, bool remote)
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(address);
        Assert.Equal(remote, AwayFromHome.IsRemote(http));
    }

    [Fact]
    public void ARelayedRequestIsRemote_WhateverAddressItArrivesFrom()
    {
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Loopback;
        http.Request.Headers["CF-Connecting-IP"] = "81.2.69.160";
        Assert.True(AwayFromHome.IsRemote(http));
    }

    [Fact]
    public void Automatic_CapsARemoteFileThatNeedsMoreThanTheConnection()
    {
        // 10 Mbps measured: three quarters of it.
        Assert.Equal(7_500_000, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 40_000_000));
        // The file fits: left as it is.
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 6_000_000));
        // Its bitrate unknown: left as it is (it may fit, and a cap would give up repackaging).
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, null));
        // Never below the floor.
        Assert.Equal(AwayFromHome.MinBitrate, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 1_000_000, 8_000_000));
    }

    [Fact]
    public void HomeOriginalOrNoMeasurement_NeverCaps()
    {
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: false, 2_000_000, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Original, remote: true, 2_000_000, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, null, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 0, 40_000_000));
    }
}
