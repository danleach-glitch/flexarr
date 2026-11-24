using Flexarr.Api.Security;

namespace Flexarr.Api.Tests;

/// <summary>
/// What /system/status says of the address for away from home: off only when remote access really is, never while
/// Tailscale is still starting (the apps would forget the address they have).
/// </summary>
public sealed class RemoteAnswerTests
{
    private static TailscaleState State(bool installed = true, bool running = true, bool privateAccess = false, bool serveRead = true) =>
        new(installed, running, "mini.tail0.ts.net", null, privateAccess, false, [], null, serveRead);

    [Fact]
    public void Served_GivesTheAddress()
    {
        var answer = TailscaleService.RemoteOf(State(privateAccess: true));
        Assert.Equal($"https://mini.tail0.ts.net:{TailscaleService.Port}", answer.Url);
        Assert.False(answer.Off);
        Assert.True(answer.Known);
    }

    [Fact]
    public void ConnectedWithoutFlexarrServed_IsOff()
    {
        var answer = TailscaleService.RemoteOf(State());
        Assert.Null(answer.Url);
        Assert.True(answer.Off);
    }

    [Fact]
    public void NotInstalled_IsOff() => Assert.True(TailscaleService.RemoteOf(State(installed: false, running: false, serveRead: false)).Off);

    [Fact]
    public void NotConnectedYet_IsUnsure()
    {
        var answer = TailscaleService.RemoteOf(State(running: false, serveRead: false));
        Assert.Null(answer.Url);
        Assert.False(answer.Off);
        Assert.False(answer.Known);
    }

    [Fact]
    public void ServeUnreadable_IsUnsure() => Assert.False(TailscaleService.RemoteOf(State(serveRead: false)).Off);
}
