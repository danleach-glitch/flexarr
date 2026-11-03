using System.Net;
using Flexarr.Api.Playback;

namespace Flexarr.Api.Tests;

public class AwayFromHomeTests
{
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.2.2", true)]
    [InlineData("172.16.4.1", true)]
    [InlineData("172.32.4.1", false)]
    [InlineData("169.254.3.3", true)]
    [InlineData("fd7a:115c:a1e0::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("::ffff:192.168.1.9", true)]
    [InlineData("100.101.102.103", false)] // Tailscale: anywhere
    [InlineData("81.2.69.160", false)]
    [InlineData("2a00:1450:4009:81f::200e", false)]
    public void HomeIsThisMachineAndPrivateNetworks(string address, bool home) =>
        Assert.Equal(home, AwayFromHome.IsHome(IPAddress.Parse(address)));

    [Fact]
    public void Automatic_CapsARemoteFileThatNeedsMoreThanTheConnection()
    {
        // 10 Mbps measured: three quarters of it.
        Assert.Equal(7_500_000, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 40_000_000));
        // The file fits: left as it is.
        Assert.Null(AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, 6_000_000));
        // Its bitrate unknown: capped, and the server's decision checks the file against it.
        Assert.Equal(7_500_000, AwayFromHome.Cap(AwayFromHomeQuality.Automatic, remote: true, 10_000_000, null));
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
