using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Streaming;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class MacAppHostTests
{
    [Fact]
    public void WithoutTheMacApp_IsNotAvailable()
    {
        Assert.False(new MacAppHost(new ConfigurationBuilder().Build()).IsAvailable);
    }

    [Theory]
    [InlineData(true, "from-input\n", "from-input")]
    [InlineData(true, "", "from-environment")]
    [InlineData(false, "from-input\n", "from-environment")]
    public void TheToken_ComesOnStandardInput_WhenTheAppSaysSo_ElseFromTheEnvironment(bool onInput, string input, string expected)
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Flexarr:MacApp:Port"] = "1",
            ["Flexarr:MacApp:Token"] = "from-environment",
            [MacAppHost.TokenOnInputKey] = onInput ? "true" : null,
        });
        using var reader = new StringReader(input);

        MacAppHost.ReadTokenFromInput(configuration, reader);

        Assert.Equal(expected, configuration["Flexarr:MacApp:Token"]);
        // Not read at all unless the app said it wrote there.
        Assert.Equal(onInput ? -1 : input.Length > 0 ? 'f' : -1, reader.Peek());
    }

    [Fact]
    public void WithTheTokenOnlyOnStandardInput_IsAvailable()
    {
        var configuration = new ConfigurationManager();
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Flexarr:MacApp:Port"] = "1", [MacAppHost.TokenOnInputKey] = "true" });

        MacAppHost.ReadTokenFromInput(configuration, new StringReader("secret\n"));

        Assert.True(new MacAppHost(configuration).IsAvailable);
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

/// <summary>The Mac app's Reset Admin PIN, sealed with its launch token so neither the API key nor the PIN travels.</summary>
public class MacAppPinResetTests
{
    private const string Token = "4C1F7E2A-9B3D-4E5F-8A6B-1C2D3E4F5A6B";

    /// <summary>What AdminPinReset.swift sends: the PIN and the time, sealed as CryptoKit's AES.GCM combined box.</summary>
    private static object Seal(string token, string pin, DateTimeOffset at, byte[]? nonce = null)
    {
        nonce ??= System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        var key = System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, System.Text.Encoding.UTF8.GetBytes(token), 32, nonce,
            MacAppHost.PinResetInfo);
        var plaintext = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new { pin, at = at.ToUnixTimeSeconds() });
        var box = new byte[12 + plaintext.Length + 16];
        System.Security.Cryptography.RandomNumberGenerator.Fill(box.AsSpan(0, 12));
        using var aes = new System.Security.Cryptography.AesGcm(key, 16);
        aes.Encrypt(box.AsSpan(0, 12), plaintext, box.AsSpan(12, plaintext.Length), box.AsSpan(12 + plaintext.Length));
        return new { nonce = Convert.ToBase64String(nonce), @sealed = Convert.ToBase64String(box) };
    }

    private static HttpClient From(WebApplicationFactory<Program> app, string peer)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", peer);
        return client;
    }

    [Fact]
    public async Task TheMacApp_SetsANewAdminPin_WithItsLaunchToken_AndNothingElseDoes()
    {
        using var factory = new FlexarrApiFactory();
        using var app = factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:MacApp:Port", "1").UseSetting("Flexarr:MacApp:Token", Token));
        var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        await admin.PostAsJsonAsync(new Uri("/api/v1/admin/pin", UriKind.Relative), new { pin = "5319" });
        var mac = From(app, "127.0.0.1");
        var url = new Uri("/api/v1/admin/pin/mac-app", UriKind.Relative);
        var now = DateTimeOffset.UtcNow;

        // Sealed with another token, or too old, or from another machine: refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await mac.PostAsJsonAsync(url, Seal("someone-else", "1111", now))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await mac.PostAsJsonAsync(url, Seal(Token, "1111", now - TimeSpan.FromMinutes(10)))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await From(app, "192.168.1.30").PostAsJsonAsync(url, Seal(Token, "1111", now))).StatusCode);

        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(32);
        Assert.Equal(HttpStatusCode.NoContent, (await mac.PostAsJsonAsync(url, Seal(Token, "2468", now, nonce))).StatusCode);
        // The same request again (a replay) is refused.
        Assert.Equal(HttpStatusCode.Forbidden, (await mac.PostAsJsonAsync(url, Seal(Token, "1357", now, nonce))).StatusCode);

        // The new PIN unlocks; the old one doesn't.
        var main = app.Services.CreateScope().ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>().Profiles.Single(p => p.Id == Flexarr.Core.Media.PlaybackState.DefaultUser);
        Assert.True(main.CheckAdminPin("2468"));
        Assert.False(main.CheckAdminPin("5319"));
    }

    [Fact]
    public async Task OutsideTheMacApp_ThereIsNoResetWithAToken()
    {
        using var factory = new FlexarrApiFactory();
        var response = await From(factory, "127.0.0.1").PostAsJsonAsync(new Uri("/api/v1/admin/pin/mac-app", UriKind.Relative), Seal(Token, "2468", DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
