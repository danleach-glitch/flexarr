using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>A new server is set up by the first browser on the home network, without the API key.</summary>
public sealed class FirstRunTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();

    /// <summary>
    /// What a new server writes when it makes its key. Settings reload from config.json (the server rewrites it as it
    /// starts), so it goes there rather than only into the options in memory.
    /// </summary>
    public FirstRunTests()
    {
        ServerConfigStore.Update(_factory.DataDirectory, config => config["setupOpen"] = true);
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!Options.SetupOpen && DateTime.UtcNow < until)
        {
            Thread.Sleep(50);
        }

        Assert.True(Options.SetupOpen);
    }

    public void Dispose() => _factory.Dispose();

    private FlexarrOptions Options => _factory.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient From(string address, bool webApp = true)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", address);
        if (webApp)
        {
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        return client;
    }

    private static async Task<bool> Open(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Api("/setup"))).GetProperty("open").GetBoolean();

    [Fact]
    public async Task TheFirstBrowserOnTheHomeNetwork_SetsTheServerUp_AndNobodyElseCan()
    {
        var first = From("192.168.1.20");
        var second = From("192.168.1.21");

        var openBefore = await Open(first);
        var claimed = await first.PostAsync(Api("/setup/claim"), null);
        var cookie = claimed.Headers.GetValues("Set-Cookie").Single();
        var openAfter = await Open(second);
        var again = await second.PostAsync(Api("/setup/claim"), null);

        Assert.True(openBefore);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
        Assert.StartsWith("flexarr.session=", cookie, StringComparison.Ordinal);
        Assert.False(openAfter);
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        Assert.False(Options.SetupOpen);
        Assert.Contains("\"setupOpen\": false", await File.ReadAllTextAsync(ServerConfigStore.PathFor(_factory.DataDirectory)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheInternet_AndOtherWebsites_CantClaimIt()
    {
        var internet = From("203.0.113.9");
        var otherSite = From("192.168.1.20", webApp: false);

        Assert.False(await Open(internet));
        Assert.Equal(HttpStatusCode.Forbidden, (await internet.PostAsync(Api("/setup/claim"), null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherSite.PostAsync(Api("/setup/claim"), null)).StatusCode);
        Assert.True(Options.SetupOpen);
    }

    [Fact]
    public async Task ADevice_StillWaitsForItsCode_WhileTheServerIsBeingSetUp()
    {
        // The browser that sets the server up is where media gets added and codes get approved; a phone can't do either.
        var phone = From("192.168.1.30", webApp: false);

        var started = await (await phone.PostAsJsonAsync(Api("/pairing"), new { deviceName = "iPhone" })).Content.ReadFromJsonAsync<JsonElement>();
        var status = await phone.GetFromJsonAsync<JsonElement>(Api($"/pairing/{started.GetProperty("id").GetString()}"));

        Assert.Equal("waiting", status.GetProperty("status").GetString());
        Assert.True(await Open(phone));
    }

    [Fact]
    public async Task SigningInWithTheKey_ClosesSetup()
    {
        var browser = From("192.168.1.20");
        browser.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        Assert.Equal(HttpStatusCode.NoContent, (await browser.PostAsync(Api("/session"), null)).StatusCode);
        Assert.False(await Open(From("192.168.1.21")));
    }
}
