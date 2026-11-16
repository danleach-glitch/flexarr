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
        // 10 Mbps measured: busy stretches held to three quarters of it, so an average of 7.5 / 1.5 = 5 Mbps.
        Assert.Equal(5_000_000, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 40_000_000));
        // The file fits, busy stretches and all: left as it is.
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 5_000_000));
        // Its bitrate unknown: left as it is (it may fit, and a cap would give up repackaging).
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, null));
        // Never below the floor.
        Assert.Equal(AwayFromHome.MinBitrate, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 1_000_000, 8_000_000));
    }

    [Theory]
    // #539: a 4.9 Mbps 1080p episode (peaking near 10) over Tailscale that carried 5–9 Mbps. The old apps' upper
    // quartile (9) and the new ones' median (7) both convert it, below its own average so the conversion happens.
    [InlineData(9_000_000L, 4_500_000L)]
    [InlineData(7_000_000L, 3_500_000L)]
    // A link with plenty of room plays it as it is.
    [InlineData(20_000_000L, null)]
    public void Automatic_JudgesAFileByItsBusyStretches_NotItsAverage(long measured, long? cap)
    {
        const long file = 4_900_000;
        Assert.Equal(cap, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, measured, file));
        if (cap is { } held)
        {
            Assert.True(held < file);
        }
    }

    [Fact]
    public void HomeOriginalOrNoMeasurement_NeverCaps()
    {
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: false, 2_000_000, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Original, remote: true, 2_000_000, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, null, 40_000_000));
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 0, 40_000_000));
    }

    [Fact]
    public void WithNoReading_ARemoteStartIsHeldTo4Mbps_WhenTheFileNeedsMore()
    {
        // Taken to carry 8 Mbps: busy stretches within 6, so 4 Mbps (720p).
        Assert.Equal(4_000_000, AwayFromHome.StartCap(AwayFromHomeQuality.Automatic, remote: true, null, 20_000_000));
        // A file that fits that plays as it is.
        Assert.Null(AwayFromHome.StartCap(AwayFromHomeQuality.Automatic, remote: true, null, 3_500_000));
        // A reading wins over the guess.
        Assert.Null(AwayFromHome.StartCap(AwayFromHomeQuality.Automatic, remote: true, 100_000_000, 20_000_000));
        // At home or on Original, never.
        Assert.Null(AwayFromHome.StartCap(AwayFromHomeQuality.Automatic, remote: false, null, 20_000_000));
        Assert.Null(AwayFromHome.StartCap(AwayFromHomeQuality.Original, remote: true, null, 20_000_000));
    }
}
