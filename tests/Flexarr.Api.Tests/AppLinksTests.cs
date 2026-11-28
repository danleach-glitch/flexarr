using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

/// <summary>Where a browser is sent to open Sonarr, Radarr and the other apps, for each way Flexarr and they are set up.</summary>
public class AppLinksTests
{
    [Theory]
    // On the server itself, at home, over Tailscale by address or MagicDNS name, by the machine's .local name.
    [InlineData("localhost", "http://localhost:8989/", "http://localhost:8989/activity/queue")]
    [InlineData("192.168.1.20", "http://localhost:8989/", "http://192.168.1.20:8989/activity/queue")]
    [InlineData("100.64.0.1", "http://127.0.0.1:8989", "http://100.64.0.1:8989/activity/queue")]
    [InlineData("dans-mac-mini", "http://localhost:8989", "http://dans-mac-mini:8989/activity/queue")]
    [InlineData("dans-mac-mini.local", "http://[::1]:8989", "http://dans-mac-mini.local:8989/activity/queue")]
    [InlineData("flexarr.example.com", "http://0.0.0.0:8989", "http://flexarr.example.com:8989/activity/queue")]
    // A URL base is kept.
    [InlineData("192.168.1.20", "http://localhost:8989/sonarr/", "http://192.168.1.20:8989/sonarr/activity/queue")]
    // An app on another machine is opened where it is.
    [InlineData("192.168.1.20", "http://192.168.1.50:8989", "http://192.168.1.50:8989/activity/queue")]
    [InlineData("192.168.1.20", "https://sonarr.example.com", "https://sonarr.example.com/activity/queue")]
    // Outside Docker a one-word name is another machine on the network.
    [InlineData("192.168.1.20", "http://nas:8989", "http://nas:8989/activity/queue")]
    public void ALink_GoesWhereTheBrowserCanReachTheApp(string browserHost, string baseUrl, string expected)
    {
        Assert.Equal(expected, new AppLinks(browserHost, container: false).Open(baseUrl, null, "activity/queue"));
    }

    [Theory]
    [InlineData("http://sonarr:8989", "http://192.168.1.20:8989/activity/queue")]
    [InlineData("http://host.docker.internal:8989", "http://192.168.1.20:8989/activity/queue")]
    [InlineData("http://172.17.0.1:8989", "http://192.168.1.20:8989/activity/queue")]
    [InlineData("http://192.168.1.50:8989", "http://192.168.1.50:8989/activity/queue")]
    public void InDocker_AnotherContainerOrTheHostIsThisServer(string baseUrl, string expected)
    {
        Assert.Equal(expected, new AppLinks("192.168.1.20", container: true).Open(baseUrl, null, "activity/queue"));
    }

    [Fact]
    public void AnAddressSetForBrowsers_IsUsedAsItIs()
    {
        var links = new AppLinks("flexarr.example.com", container: true);

        Assert.Equal("https://sonarr.example.com/tv/series/severance",
            links.Open("http://sonarr:8989", "https://sonarr.example.com/tv/", "series/severance"));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("")]
    [InlineData(null)]
    public void NothingButAWebAddress_IsLinked(string? baseUrl)
    {
        var links = new AppLinks("192.168.1.20", container: false);

        Assert.Null(links.Open(baseUrl));
        Assert.Equal("http://192.168.1.20:8989/", links.Open("http://localhost:8989", webUrl: baseUrl)); // Not as the browser's address either.
    }

    [Fact]
    public void WithoutABrowser_AnAppKeepsItsOwnAddress()
    {
        Assert.Equal("http://localhost:8989/activity/queue", AppLinks.None.Open("http://localhost:8989", null, "activity/queue"));
    }

    [Theory]
    [InlineData("dans-mac-mini.tail1234.ts.net", "100.64.0.1")] // Browsers force HTTPS on .ts.net; Sonarr answers HTTP.
    [InlineData("DANS-MAC-MINI.TAIL1234.TS.NET.", "100.64.0.1")]
    [InlineData("192.168.1.20", "192.168.1.20")]
    [InlineData("flexarr.lan", "flexarr.lan")]
    public async Task TheTailscaleName_GivesWayToTheTailnetAddress(string host, string expected)
    {
        Assert.Equal(expected, await AppLinks.BrowserHostAsync(host, new FakeTailnet(), CancellationToken.None));
    }

    [Fact]
    public async Task WithoutTailscaleAnswering_TheShortMagicDnsNameIsUsed()
    {
        Assert.Equal("dans-mac-mini", await AppLinks.BrowserHostAsync("dans-mac-mini.tail1234.ts.net", new FakeTailnet { Snapshot = null }, CancellationToken.None));
    }
}

/// <summary>An address for browsers, saved with a Sonarr or Radarr connection in Settings.</summary>
public sealed class AppLinkSettingsTests
{
    [Fact]
    public async Task AnAddressForBrowsers_IsSaved_KeptWhenOmitted_AndClearedWhenEmpty()
    {
        using var server = new FlexarrApiFactory();
        using var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        async Task<JsonElement> SonarrAsync(Func<JsonElement, bool> until)
        {
            for (var i = 0; ; i++)
            {
                var arr = (await admin.GetFromJsonAsync<JsonElement>("/api/v1/settings")).GetProperty("arr");
                if ((arr.GetArrayLength() > 0 && until(arr[0])) || i == 50)
                {
                    return arr[0];
                }

                await Task.Delay(100);
            }
        }

        static string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        object Save(string? webUrl) => new { arr = new[] { new { name = "Sonarr", kind = "Sonarr", baseUrl = "http://localhost:8989", apiKey = "k", webUrl } } };

        Assert.True((await admin.PutAsJsonAsync("/api/v1/settings", Save("https://sonarr.example.com"))).IsSuccessStatusCode);
        var sonarr = await SonarrAsync(s => Text(s, "webUrl") is not null);
        Assert.Equal("https://sonarr.example.com/", Text(sonarr, "webUrl"));
        Assert.Equal("https://sonarr.example.com/", Text(sonarr, "link"));

        Assert.True((await admin.PutAsJsonAsync("/api/v1/settings", Save(null))).IsSuccessStatusCode);
        await Task.Delay(300);
        Assert.Equal("https://sonarr.example.com/", Text(await SonarrAsync(_ => true), "webUrl"));

        Assert.True((await admin.PutAsJsonAsync("/api/v1/settings", Save(""))).IsSuccessStatusCode);
        sonarr = await SonarrAsync(s => Text(s, "webUrl") is null);
        Assert.Null(Text(sonarr, "webUrl"));
        Assert.Equal("http://localhost:8989/", Text(sonarr, "link")); // The test client reaches Flexarr at localhost.

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/settings", Save("javascript:alert(1)"))).StatusCode);
    }
}
