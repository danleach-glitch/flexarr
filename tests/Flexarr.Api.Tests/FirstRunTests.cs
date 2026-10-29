using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
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

    private string Code => _factory.Services.GetRequiredService<FirstRun>().Code!;

    private static JsonContent Claim(string? code) => JsonContent.Create(new { code });

    private static async Task<bool> Open(HttpClient client) =>
        (await client.GetFromJsonAsync<JsonElement>(Api("/setup"))).GetProperty("open").GetBoolean();

    [Fact]
    public async Task TheFirstBrowserOnTheHomeNetwork_SetsTheServerUp_AndNobodyElseCan()
    {
        var first = From("192.168.1.20");
        var second = From("192.168.1.21");

        var openBefore = await Open(first);
        var code = Code;
        var codeFile = Path.Combine(_factory.DataDirectory, FirstRun.CodeFileName);
        var saved = (await File.ReadAllTextAsync(codeFile)).Trim();
        // Typed as people do: lower case, without the dash.
        var claimed = await first.PostAsync(Api("/setup/claim"), Claim(code.ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal)));
        var cookie = claimed.Headers.GetValues("Set-Cookie").Single();
        var openAfter = await Open(second);
        var again = await second.PostAsync(Api("/setup/claim"), Claim(code));

        Assert.True(openBefore);
        Assert.Equal(code, saved);
        Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}$", code);
        Assert.Equal(HttpStatusCode.NoContent, claimed.StatusCode);
        Assert.False(File.Exists(codeFile));
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
        Assert.Equal(HttpStatusCode.Forbidden, (await internet.PostAsync(Api("/setup/claim"), Claim(Code))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await otherSite.PostAsync(Api("/setup/claim"), Claim(Code))).StatusCode);
        Assert.True(Options.SetupOpen);
    }

    [Fact]
    public async Task WithoutTheCode_NobodyCanClaimIt_AndGuessingGetsANewCode()
    {
        // Behind a proxy or Docker's port forwarding, the internet can look like the home network: the code is what
        // only someone with the machine has.
        var looksLocal = From("192.168.1.20");
        var first = Code;

        var none = await looksLocal.PostAsync(Api("/setup/claim"), null);
        var wrong = await looksLocal.PostAsync(Api("/setup/claim"), Claim("AAAA-AAAA"));
        for (var i = 2; i < FirstRun.TriesPerCode; i++)
        {
            await looksLocal.PostAsync(Api("/setup/claim"), Claim("AAAA-AAAA"));
        }

        var stale = await looksLocal.PostAsync(Api("/setup/claim"), Claim(first));

        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.NotEqual(first, Code);
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        Assert.True(Options.SetupOpen);
        Assert.Equal(HttpStatusCode.NoContent, (await looksLocal.PostAsync(Api("/setup/claim"), Claim(Code))).StatusCode);
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
    public async Task AnyoneCanReadTheServersId_WhichIsTheOneTheSignedInStatusGives()
    {
        // An app that's told another address for its server asks there, without its sign-in, before trusting it.
        var stranger = From("203.0.113.9", webApp: false);
        var app = From("192.168.1.30", webApp: false);
        app.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var id = (await stranger.GetFromJsonAsync<JsonElement>(Api("/setup"))).GetProperty("serverId").GetString();
        var again = (await stranger.GetFromJsonAsync<JsonElement>(Api("/setup"))).GetProperty("serverId").GetString();
        var status = await app.GetFromJsonAsync<JsonElement>(Api("/system/status"));

        Assert.Matches("^[0-9a-f]{32}$", id);
        Assert.Equal(id, again);
        Assert.Equal(id, status.GetProperty("serverId").GetString());
        Assert.Contains($"\"serverId\": \"{id}\"", await File.ReadAllTextAsync(ServerConfigStore.PathFor(_factory.DataDirectory)), StringComparison.Ordinal);
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
