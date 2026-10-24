using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A server with the home-network shortcut on, where tests choose which address each request comes from.</summary>
public sealed class RemoteAccessFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("flexarr-remote-").FullName;

    public FakeWhois Whois { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("Flexarr:ApiKey", FlexarrApiFactory.ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:AuthenticationRequired", "DisabledForLocalAddresses");
        builder.UseSetting("Flexarr:TrustedProxies:0", "10.9.0.0/16");
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, PeerFromHeader>();
            services.AddSingleton<Flexarr.Api.Security.ITailnetWhois>(Whois);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataDirectory))
        {
            Directory.Delete(DataDirectory, recursive: true);
        }
    }
}

public class RemoteAccessTests(RemoteAccessFactory factory) : IClassFixture<RemoteAccessFactory>
{
    private const string Status = "/api/v1/system/status";
    private static readonly (string, string) Lan = ("X-Test-Peer", "192.168.1.50");

    private Task<HttpStatusCode> GetAsync(string path, params (string Name, string Value)[] headers) => SendAsync(HttpMethod.Get, path, null, headers);

    private async Task<HttpStatusCode> SendAsync(HttpMethod method, string path, object? body, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        foreach (var (name, value) in headers)
        {
            if (name == "Host")
            {
                request.Headers.Host = value;
            }
            else
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }
        }

        return (await factory.CreateClient().SendAsync(request)).StatusCode;
    }

    /// <summary>A request through <c>tailscale serve</c> on this machine, from a tailnet device whose owner Tailscale confirms.</summary>
    private (string, string)[] ViaTailscale(string address, string login, string? claimedLogin = null)
    {
        factory.Whois.Owners[address] = login;
        return [("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", address), ("Tailscale-User-Login", claimedLogin ?? login)];
    }

    [Fact]
    public async Task HomeNetwork_IsLetIn_WithoutAKey() =>
        Assert.Equal(HttpStatusCode.OK, await GetAsync(Status, Lan));

    [Theory]
    [InlineData("CF-Connecting-IP", "203.0.113.7")]
    [InlineData("Tailscale-Funnel-Request", "?1")]
    public async Task TunnelledInternetTraffic_IsNotTreatedAsHome(string header, string value) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", "127.0.0.1"), (header, value)));

    [Fact]
    public async Task ForwardedFromAPublicAddress_ThroughTheLocalProxy_IsNotHome() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", "203.0.113.7")));

    [Theory]
    [InlineData("172.17.0.1", "X-Forwarded-For", "203.0.113.7")] // a reverse proxy in Docker
    [InlineData("192.168.1.2", "X-Forwarded-For", "192.168.1.60")] // or on another machine, even claiming a home address
    [InlineData("127.0.0.1", "X-Real-IP", "203.0.113.7")] // a proxy here that only says X-Real-IP
    [InlineData("192.168.1.2", "Forwarded", "for=203.0.113.7")]
    public async Task ForwardedRequests_FromProxiesFlexarrDoesntTrust_AreNotHome(string proxy, string header, string value) =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", proxy), (header, value)));

    [Fact]
    public async Task ATrustedProxy_IsBelievedAboutItsClient()
    {
        Assert.Equal(HttpStatusCode.OK, await GetAsync(Status, ("X-Test-Peer", "10.9.0.4"), ("X-Forwarded-For", "192.168.1.60")));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ("X-Test-Peer", "10.9.0.4"), ("X-Forwarded-For", "203.0.113.7")));
    }

    [Theory]
    [InlineData("192.168.1.10:8383")]
    [InlineData("mini.local:8383")]
    [InlineData("mini:8383")]
    [InlineData("localhost")]
    public async Task HomeNetwork_AddressedToThisServersNames_IsLetIn(string host) =>
        Assert.Equal(HttpStatusCode.OK, await GetAsync(Status, Lan, ("Host", host)));

    [Fact]
    public async Task HomeNetwork_AddressedToSomeoneElsesName_NeedsTheKey() =>
        // A page on attacker.example that pointed its own name at this server (DNS rebinding).
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, Lan, ("Host", "attacker.example:8383")));

    [Fact]
    public async Task HomeNetwork_RefusesChangesStartedByOtherWebsites()
    {
        var test = "/api/v1/arr/test";
        var body = new { name = "Sonarr", kind = "Sonarr", baseUrl = "not a url" };

        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, test, body, Lan, ("Origin", "https://attacker.example")));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, test, body, Lan, ("Sec-Fetch-Site", "cross-site")));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(HttpMethod.Post, test, body, Lan, ("Sec-Fetch-Site", "same-origin")));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(HttpMethod.Post, test, body, Lan, ("Origin", "http://localhost")));
        Assert.Equal(HttpStatusCode.BadRequest, await SendAsync(HttpMethod.Post, test, body, Lan, ("X-Requested-With", "flexarr")));
    }

    [Fact]
    public async Task TailscaleIdentity_ViaTailscaleServe_SignsInAsTheLinkedProfile()
    {
        await LinkAsync("sam@example.com", "Sam");

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profiles");
        foreach (var (name, value) in ViaTailscale("100.70.1.2", "sam@example.com"))
        {
            request.Headers.Add(name, value);
        }

        var response = await factory.CreateClient().SendAsync(request);
        var profiles = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Sam", profiles.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("name").GetString());
    }

    [Fact]
    public async Task TailscaleIdentity_ThatTailscaleDoesntConfirm_IsIgnored()
    {
        await LinkAsync("owner-ts@example.com", null);

        // Another program on the server claiming to be the owner, for an address that's someone else's (or nobody's).
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ViaTailscale("100.70.1.9", "guest@example.com", claimedLogin: "owner-ts@example.com")));
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status,
            ("X-Test-Peer", "127.0.0.1"), ("X-Forwarded-For", "100.70.1.10"), ("Tailscale-User-Login", "owner-ts@example.com")));
    }

    [Fact]
    public async Task UnlinkedTailscaleAccount_IsRefused() =>
        Assert.Equal(HttpStatusCode.Unauthorized, await GetAsync(Status, ViaTailscale("100.70.1.3", "stranger@example.com")));

    [Fact]
    public async Task TailscaleHeader_FromALanDevice_IsIgnored()
    {
        await LinkAsync("dan@example.com", null);

        // A LAN device can't claim to be someone over Tailscale; it just gets the home-network rule (main profile).
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/profiles");
        request.Headers.Add("X-Test-Peer", "192.168.1.50");
        request.Headers.Add("Tailscale-User-Login", "sam-forged@example.com");
        var profiles = await (await factory.CreateClient().SendAsync(request)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(profiles.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("isMain").GetBoolean());
    }

    [Fact]
    public async Task OnlyTheMainProfile_CanChangeSettings()
    {
        await LinkAsync("kid@example.com", "Kid");
        var tailnet = ViaTailscale("100.70.1.4", "kid@example.com");

        Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/libraries", tailnet));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", tailnet));
        Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/v1/settings/api-key/reveal", null, [.. tailnet, ("X-Requested-With", "flexarr")]));
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/filesystem", tailnet));

        // Webhooks start scans of any path they name: the *arr apps send them with the API key.
        using var webhook = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/sonarr")
        {
            Content = JsonContent.Create(new { eventType = "Download", series = new { title = "Andor", path = "/tv/Andor" } }),
        };
        foreach (var (name, value) in tailnet)
        {
            webhook.Headers.TryAddWithoutValidation(name, value);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await factory.CreateClient().SendAsync(webhook)).StatusCode);
    }

    [Fact]
    public async Task ATailscaleGuest_CantChooseTheMainProfile_UntilItHasAPin()
    {
        await LinkAsync("guest@example.com", "Guest");
        var guest = ViaTailscale("100.70.1.5", "guest@example.com").Append(("X-Requested-With", "flexarr")).ToArray();

        var withoutPin = await SendAsync(HttpMethod.Post, "/api/v1/profiles/owner/select", new { pin = (string?)null }, guest);
        // The home network is trusted with the server, so there it can.
        var fromHome = await SendAsync(HttpMethod.Post, "/api/v1/profiles/owner/select", new { pin = (string?)null }, Lan, ("X-Requested-With", "flexarr"));

        Assert.Equal(HttpStatusCode.Forbidden, withoutPin);
        Assert.Equal(HttpStatusCode.OK, fromHome);
    }

    [Fact]
    public async Task OnTheHomeNetwork_ChoosingAnotherProfile_GivesUpManagingTheServer()
    {
        var kid = await AddProfileAsync("Home kid");
        using var select = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/profiles/{kid}/select") { Content = JsonContent.Create(new { pin = (string?)null }) };
        select.Headers.Add("X-Test-Peer", "192.168.1.50");
        select.Headers.Add("X-Requested-With", "flexarr");
        var token = (await (await factory.CreateClient().SendAsync(select)).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", Lan, ("X-Flexarr-Profile", token)));
        Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/settings", Lan));
    }

    [Fact]
    public async Task TheOwnersTailscaleIdentity_Alone_DoesntManageTheServer_UntilTheMainProfilesPinIsGiven()
    {
        // Another program on the server could claim this identity, given one of the owner's tailnet addresses.
        await LinkAsync("owner-pin@example.com", null);
        var owner = ViaTailscale("100.70.1.20", "owner-pin@example.com").Append(("X-Requested-With", "flexarr")).ToArray();
        await SetMainPinAsync("2468");
        try
        {
            var (cookie, token) = await ChooseMainWithPinAsync(owner, "2468");

            Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/libraries", owner)); // still watches as the main profile
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", owner));
            // The PIN signed this browser in; the profile token, the same in every browser, isn't what manages the server.
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", [.. owner, ("X-Flexarr-Profile", token)]));
            Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/settings", [.. owner, ("Cookie", cookie)]));
        }
        finally
        {
            await SetMainPinAsync(null);
        }
    }

    [Fact]
    public async Task OverTailscale_ThePinsSignIn_IsListed_AndSigningItOutEndsManagingTheServer()
    {
        await LinkAsync("owner-revoke@example.com", null);
        var owner = ViaTailscale("100.70.1.21", "owner-revoke@example.com").Append(("X-Requested-With", "flexarr")).ToArray();
        await SetMainPinAsync("1357");
        try
        {
            var (cookie, _) = await ChooseMainWithPinAsync(owner, "1357");
            var browser = factory.CreateClient();
            foreach (var (name, value) in owner.Append(("Cookie", cookie)))
            {
                browser.DefaultRequestHeaders.TryAddWithoutValidation(name, value);
            }

            var listed = await browser.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/sign-ins", UriKind.Relative));
            var mine = listed.EnumerateArray().Single(s => s.GetProperty("isThisOne").GetBoolean());
            Assert.Equal("Tailnet", mine.GetProperty("zone").GetString());

            var signedOut = await browser.DeleteAsync(new Uri($"/api/v1/sign-ins/{mine.GetProperty("id").GetString()}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(new Uri("/api/v1/settings", UriKind.Relative))).StatusCode);
            // Back to the Tailscale identity, which still watches as the main profile.
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(new Uri("/api/v1/libraries", UriKind.Relative))).StatusCode);
        }
        finally
        {
            await SetMainPinAsync(null);
        }
    }

    [Fact]
    public async Task TheMainProfilesToken_CopiedToAnotherTailscaleLogin_DoesntManageTheServer()
    {
        await LinkAsync("owner-copied@example.com", null);
        await LinkAsync("teen@example.com", "Teen");
        var owner = ViaTailscale("100.70.1.22", "owner-copied@example.com").Append(("X-Requested-With", "flexarr")).ToArray();
        var teen = ViaTailscale("100.70.1.23", "teen@example.com");
        await SetMainPinAsync("9731");
        try
        {
            var (_, token) = await ChooseMainWithPinAsync(owner, "9731");

            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", [.. teen, ("X-Flexarr-Profile", token)]));
            Assert.Equal(HttpStatusCode.Forbidden, await SendAsync(HttpMethod.Post, "/api/v1/settings/api-key/reveal", new { pin = "9731" },
                [.. teen, ("X-Flexarr-Profile", token), ("X-Requested-With", "flexarr")]));
        }
        finally
        {
            await SetMainPinAsync(null);
        }
    }

    [Fact]
    public async Task ChangingTheMainPin_SignsOutBrowsersThatSignedInWithTheOldOne()
    {
        await LinkAsync("owner-repin@example.com", null);
        var owner = ViaTailscale("100.70.1.24", "owner-repin@example.com").Append(("X-Requested-With", "flexarr")).ToArray();
        await SetMainPinAsync("4680");
        var (cookie, _) = await ChooseMainWithPinAsync(owner, "4680");

        var changed = await SendAsync(HttpMethod.Put, "/api/v1/profiles/owner", new { name = "Owner", removePin = true }, ("X-Api-Key", FlexarrApiFactory.ApiKey));

        Assert.Equal(HttpStatusCode.OK, changed);
        Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", [.. owner, ("Cookie", cookie)]));
    }

    [Fact]
    public async Task ThePinsSignIn_OnlyWorksWithTheTailscaleLoginThatEarnedIt_WhileItsLinked()
    {
        await LinkAsync("owner-bound@example.com", null);
        await LinkAsync("teen-bound@example.com", "Teen bound");
        var owner = ViaTailscale("100.70.1.25", "owner-bound@example.com").Append(("X-Requested-With", "flexarr")).ToArray();
        var teen = ViaTailscale("100.70.1.26", "teen-bound@example.com");
        await SetMainPinAsync("8024");
        try
        {
            var (cookie, _) = await ChooseMainWithPinAsync(owner, "8024");

            // tailscale serve sends the login on every request, so the owner's own browser carries on as before.
            Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/settings", [.. owner, ("Cookie", cookie)]));
            Assert.Equal(HttpStatusCode.OK, await GetAsync("/api/v1/settings", [.. owner, ("Cookie", cookie)]));
            // Copied to someone else's device, it's ignored.
            Assert.Equal(HttpStatusCode.Forbidden, await GetAsync("/api/v1/settings", [.. teen, ("Cookie", cookie)]));

            // Unlinked, it no longer manages the server, even from the owner's own device.
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                await db.Profiles.Where(p => p.TailscaleLogin == "owner-bound@example.com").ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, (string?)null));
            }

            Assert.NotEqual(HttpStatusCode.OK, await GetAsync("/api/v1/settings", [.. owner, ("Cookie", cookie)]));
        }
        finally
        {
            await SetMainPinAsync(null);
        }
    }

    /// <summary>Chooses the main profile with its PIN; returns the sign-in cookie that sets, and the profile token.</summary>
    private async Task<(string Cookie, string Token)> ChooseMainWithPinAsync((string Name, string Value)[] headers, string pin)
    {
        using var select = new HttpRequestMessage(HttpMethod.Post, "/api/v1/profiles/owner/select") { Content = JsonContent.Create(new { pin }) };
        foreach (var (name, value) in headers)
        {
            select.Headers.TryAddWithoutValidation(name, value);
        }

        var response = await factory.CreateClient().SendAsync(select);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cookie = response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).Single(c => c.StartsWith("flexarr.session=", StringComparison.Ordinal));
        return (cookie, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!);
    }

    private async Task SetMainPinAsync(string? pin)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!.SetPin(pin);
        await db.SaveChangesAsync();
    }

    /// <summary>Links a Tailscale login to a new profile, or to the main profile when no name is given.</summary>
    private async Task LinkAsync(string login, string? profileName)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = profileName is null
            ? await db.Profiles.FindAsync(PlaybackState.DefaultUser)
            : db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = profileName }).Entity;
        profile!.TailscaleLogin = login;
        await db.SaveChangesAsync();
    }

    private async Task<string> AddProfileAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = name }).Entity;
        await db.SaveChangesAsync();
        return profile.Id;
    }
}
