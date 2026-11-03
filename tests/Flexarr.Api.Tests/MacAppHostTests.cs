using System.Net;
using System.Net.Sockets;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Streaming;
using Microsoft.Extensions.Configuration;

namespace Flexarr.Api.Tests;

public class MacAppHostTests
{
    [Fact]
    public void WithoutTheMacApp_IsNotAvailable()
    {
        Assert.False(new MacAppHost(new ConfigurationBuilder().Build()).IsAvailable);
    }

    [Theory]
    [InlineData(null, "secret choose", "/Volumes/Media/Films", "/Volumes/Media/Films")]
    [InlineData("~/Library/Application Support/Plex Media Server", "secret choose\t~/Library/Application Support/Plex Media Server", "", null)]
    public async Task ChooseFolder_SendsTheToken_AndReadsTheAppsAnswer(string? start, string sent, string answer, string? expected)
    {
        using var app = new TcpListener(IPAddress.Loopback, 0);
        app.Start();
        var port = ((IPEndPoint)app.LocalEndpoint).Port;
        var host = new MacAppHost(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Flexarr:MacApp:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Flexarr:MacApp:Token"] = "secret",
        }).Build());

        var choosing = host.ChooseFolderAsync(start, CancellationToken.None);
        using var connection = await app.AcceptTcpClientAsync(CancellationToken.None);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var request = await reader.ReadLineAsync(CancellationToken.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), CancellationToken.None);

        Assert.Equal(sent, request);
        Assert.Equal(expected, await choosing);
    }

    [Theory]
    [InlineData(null, "secret login-item", "on", StartAtLogin.On)]
    [InlineData(true, "secret login-item\ton", "approval", StartAtLogin.NeedsApproval)]
    [InlineData(false, "secret login-item\toff", "off", StartAtLogin.Off)]
    public async Task StartAtLogin_AsksTheApp_AndReadsItsAnswer(bool? on, string sent, string answer, StartAtLogin expected)
    {
        using var app = new TcpListener(IPAddress.Loopback, 0);
        app.Start();
        var host = new MacAppHost(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Flexarr:MacApp:Port"] = ((IPEndPoint)app.LocalEndpoint).Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Flexarr:MacApp:Token"] = "secret",
        }).Build());

        var asking = host.StartAtLoginAsync(on, CancellationToken.None);
        using var connection = await app.AcceptTcpClientAsync(CancellationToken.None);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var request = await reader.ReadLineAsync(CancellationToken.None);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(answer + "\n"), CancellationToken.None);

        Assert.Equal(sent, request);
        Assert.Equal(expected, await asking);
    }
}

public class MacAppTranscodingTests
{
    private static MacAppHost Host(bool inMacApp) => new(new ConfigurationBuilder().AddInMemoryCollection(inMacApp
        ? new Dictionary<string, string?> { ["Flexarr:MacApp:Port"] = "1", ["Flexarr:MacApp:Token"] = "t" }
        : []).Build());

    [Theory]
    [InlineData(HardwareAcceleration.None, HardwareAcceleration.Auto)]
    [InlineData(HardwareAcceleration.Vaapi, HardwareAcceleration.Auto)]
    [InlineData(HardwareAcceleration.VideoToolbox, HardwareAcceleration.VideoToolbox)]
    [InlineData(HardwareAcceleration.Auto, HardwareAcceleration.Auto)]
    public void InTheMacApp_OnlyVideoToolboxOrAutoStand(HardwareAcceleration saved, HardwareAcceleration expected)
    {
        var options = new FlexarrOptions { Transcoding = { HardwareAcceleration = saved } };

        new MacAppTranscoding(Host(inMacApp: true)).PostConfigure(null, options);

        Assert.Equal(expected, options.Transcoding.HardwareAcceleration);
    }

    [Fact]
    public void ElsewhereTheSettingIsLeftAlone()
    {
        var options = new FlexarrOptions { Transcoding = { HardwareAcceleration = HardwareAcceleration.None } };

        new MacAppTranscoding(Host(inMacApp: false)).PostConfigure(null, options);

        Assert.Equal(HardwareAcceleration.None, options.Transcoding.HardwareAcceleration);
    }
}

public class MacAppPlexAvatarTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task InTheMacApp_PlexPictures_SayWhyTheyCantBeBroughtOver_RatherThanRunAnything()
    {
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:MacApp:Port", "1").UseSetting("Flexarr:MacApp:Token", "t"));
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        // Plex's database where macOS keeps it: no Preferences.xml beside it, so its sign-in is in macOS's settings.
        var database = Path.Combine(Path.GetTempPath(), $"plex-{Guid.NewGuid():N}", "Plug-in Support", "Databases", "com.plexapp.plugins.library.db");

        var response = await client.PostAsync(new Uri($"/api/v1/import/plex/avatars?path={Uri.EscapeDataString(database)}", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("The Mac app can’t read Plex’s sign-in", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }
}
