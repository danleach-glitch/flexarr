using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Authentication;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Per-browser and per-device sign-ins, who may manage the server, and the API key itself.</summary>
public class SignInTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A browser, signed in with the API key: it holds only the session cookie afterwards.</summary>
    private async Task<(HttpClient Browser, string Cookie)> SignInBrowserAsync()
    {
        var client = factory.CreateClient();
        using var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session"));
        signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.SendAsync(signIn);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie")).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        return (client, cookie);
    }

    private async Task<string> DeviceTokenAsync(string name = "Apple TV")
    {
        var response = await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = name });
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;
    }

    private HttpClient Device(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        return client;
    }

    private async Task<string> CreateProfileAsync(string name, string? pin = null)
    {
        var created = await (await Admin().PostAsJsonAsync(Api("/profiles"), new { name, pin })).Content.ReadFromJsonAsync<JsonElement>();
        return created.GetProperty("id").GetString()!;
    }

    [Fact]
    public async Task Devices_SayWhatTheyAre_SoListsCanNameAndDrawThem()
    {
        using var asking = new HttpRequestMessage(HttpMethod.Post, Api("/session/device")) { Content = JsonContent.Create(new { deviceName = "Pixel 9" }) };
        asking.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        asking.Headers.Add(DeviceTypes.Header, "AndroidPhone");
        var issued = await (await factory.CreateClient().SendAsync(asking)).Content.ReadFromJsonAsync<JsonElement>();
        var phoneId = issued.GetProperty("id").GetString()!;

        // An app from before 0.54 says nothing, until it's updated and does; nonsense changes nothing.
        var tvToken = await DeviceTokenAsync("Living room");
        var tv = Device(tvToken);
        async Task<JsonElement> Listed(string name) =>
            (await Admin().GetFromJsonAsync<JsonElement[]>(Api("/sign-ins")))!.First(s => s.GetProperty("name").GetString() == name);
        Assert.Equal("AndroidPhone", (await Listed("Pixel 9")).GetProperty("deviceType").GetString());
        Assert.Equal(JsonValueKind.Null, (await Listed("Living room")).GetProperty("deviceType").ValueKind);

        tv.DefaultRequestHeaders.Add(DeviceTypes.Header, "AppleTv");
        await tv.GetAsync(Api("/system/status"));
        var updatedTv = Device(tvToken);
        updatedTv.DefaultRequestHeaders.Add(DeviceTypes.Header, "3");
        await updatedTv.GetAsync(Api("/system/status"));
        Assert.Equal("AppleTv", (await Listed("Living room")).GetProperty("deviceType").GetString());

        // Play on's list says it too.
        var devices = await Device(tvToken).GetFromJsonAsync<JsonElement[]>(Api("/devices"));
        Assert.Equal("AndroidPhone", devices!.First(d => d.GetProperty("id").GetString() == phoneId).GetProperty("deviceType").GetString());
    }

    [Fact]
    public async Task EachBrowser_GetsItsOwnSession_ThatSigningOutEnds()
    {
        var (first, cookie) = await SignInBrowserAsync();
        var (second, otherCookie) = await SignInBrowserAsync();

        Assert.NotEqual(cookie, otherCookie);
        Assert.DoesNotContain(FlexarrApiFactory.ApiKey, cookie, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await first.GetAsync(Api("/settings"))).StatusCode);

        var signedOut = await first.DeleteAsync(Api("/session"));

        Assert.Equal(HttpStatusCode.NoContent, signedOut.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await first.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync(Api("/system/status"))).StatusCode);
    }

    [Fact]
    public async Task SignInsAreListed_AndCanBeSignedOutFromSettings()
    {
        var (browser, _) = await SignInBrowserAsync();
        var token = await DeviceTokenAsync("Bedroom TV");

        var listed = await browser.GetFromJsonAsync<JsonElement>(Api("/sign-ins"));
        var tv = listed.EnumerateArray().Single(s => s.GetProperty("name").GetString() == "Bedroom TV");
        Assert.Contains(listed.EnumerateArray(), s => s.GetProperty("isThisOne").GetBoolean() && s.GetProperty("kind").GetString() == "Browser");
        Assert.Equal(HttpStatusCode.OK, (await Device(token).GetAsync(Api("/system/status"))).StatusCode);

        var revoked = await browser.DeleteAsync(Api($"/sign-ins/{tv.GetProperty("id").GetString()}"));

        Assert.Equal(HttpStatusCode.NoContent, revoked.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Device(token).GetAsync(Api("/system/status"))).StatusCode);
    }

    [Fact]
    public async Task ABrowserFromAnEarlierVersion_IsMovedToItsOwnSession()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Cookie", $"{ApiKeyAuthenticationHandler.SessionCookieName}={ApiKeyAuthenticationHandler.LegacySessionValue(FlexarrApiFactory.ApiKey)}");

        var response = await client.GetAsync(Api("/system/status"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var renewed = Assert.Single(response.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain(ApiKeyAuthenticationHandler.LegacySessionValue(FlexarrApiFactory.ApiKey), renewed, StringComparison.Ordinal);
    }

    /// <summary>A profile token as earlier versions signed it, without the PIN.</summary>
    private static string LegacyProfileToken(string id) =>
        $"{id}.{Convert.ToHexStringLower(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(FlexarrApiFactory.ApiKey), System.Text.Encoding.UTF8.GetBytes($"profile:{id}")))[..32]}";

    [Fact]
    public async Task EarlierVersionsSignIns_StopWorking_AfterTheirMonth()
    {
        var kid = await CreateProfileAsync("Legacy kid");
        var withinTheMonth = Admin();
        withinTheMonth.DefaultRequestHeaders.Add("X-Flexarr-Profile", LegacyProfileToken(kid));
        Assert.Equal(kid, (await withinTheMonth.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());

        using var server = new FlexarrApiFactory();
        using var later = server.WithWebHostBuilder(b => b.UseSetting("Flexarr:LegacySignInsUntil", "2020-01-01T00:00:00Z"));
        var oldCookie = later.CreateClient();
        oldCookie.DefaultRequestHeaders.Add("Cookie", $"{ApiKeyAuthenticationHandler.SessionCookieName}={ApiKeyAuthenticationHandler.LegacySessionValue(FlexarrApiFactory.ApiKey)}");
        var admin = later.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var laterKid = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Legacy kid" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        admin.DefaultRequestHeaders.Add("X-Flexarr-Profile", LegacyProfileToken(laterKid));

        Assert.Equal(HttpStatusCode.Unauthorized, (await oldCookie.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal("owner", (await admin.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task WritesWithTheCookie_FromAnotherWebsite_AreRefused()
    {
        var (browser, cookie) = await SignInBrowserAsync();
        var forged = factory.CreateClient();
        forged.DefaultRequestHeaders.Add("Cookie", cookie);
        forged.DefaultRequestHeaders.Add("Origin", "https://attacker.example");

        var fromElsewhere = await forged.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { });
        var fromTheApp = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { confirm = true });

        Assert.Equal(HttpStatusCode.Forbidden, fromElsewhere.StatusCode);
        Assert.Equal(FlexarrApiFactory.ApiKey, (await fromTheApp.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("apiKey").GetString());
        // Reading it is no longer a GET that any script or tag on the page could make.
        Assert.NotEqual(HttpStatusCode.OK, (await browser.GetAsync(Api("/settings/api-key"))).StatusCode);
    }

    [Fact]
    public async Task ShowingTheKey_AsksAgain_ForTheMainProfilesPinWhenItHasOne()
    {
        var (browser, _) = await SignInBrowserAsync();
        var unconfirmed = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { });

        await Admin().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "1593" });
        var justConfirmed = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { confirm = true });
        var wrongPin = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { pin = "0000" });
        var rightPin = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { pin = "1593" });
        await factory.SetMainPinAsync(null);

        Assert.Equal(HttpStatusCode.BadRequest, unconfirmed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, justConfirmed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, rightPin.StatusCode);
    }

    [Fact]
    public async Task RotatingTheKey_CanSignOutEveryOtherBrowserAndDevice()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString());
        var minted = server.CreateClient(); // what someone holding a leaked key could have made for themselves
        minted.DefaultRequestHeaders.Add("X-Api-Key", (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Not mine" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString());

        var rotated = await (await admin.PostAsJsonAsync(Api("/settings/api-key/rotate"), new { signOutOthers = true })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, rotated.GetProperty("signedOut").GetInt32());
        Assert.Equal(HttpStatusCode.Unauthorized, (await tv.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await minted.GetAsync(Api("/system/status"))).StatusCode);
    }

    [Fact]
    public async Task ASignIn_CanBeRenamed()
    {
        var (browser, _) = await SignInBrowserAsync();
        await DeviceTokenAsync("Apple TV 4K");
        var tv = (await browser.GetFromJsonAsync<JsonElement>(Api("/sign-ins"))).EnumerateArray().First(s => s.GetProperty("name").GetString() == "Apple TV 4K");
        var id = tv.GetProperty("id").GetString();

        var renamed = await browser.PatchAsJsonAsync(Api($"/sign-ins/{id}"), new { name = "  Kitchen TV  " });
        var blank = await browser.PatchAsJsonAsync(Api($"/sign-ins/{id}"), new { name = " " });
        var unknown = await browser.PatchAsJsonAsync(Api("/sign-ins/nope"), new { name = "Hall" });
        var forged = await browser.PatchAsJsonAsync(Api($"/sign-ins/{id}"), new { name = "Kitchen\nRevoked: everything" });

        Assert.Equal(HttpStatusCode.NoContent, renamed.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        var listed = await browser.GetFromJsonAsync<JsonElement>(Api("/sign-ins"));
        Assert.Equal("Kitchen TV", listed.EnumerateArray().Single(s => s.GetProperty("id").GetString() == id).GetProperty("name").GetString());
    }

    [Fact]
    public async Task SigningOutEveryOtherBrowserAndTv_KeepsTheOneAsking()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        async Task<HttpClient> Browser()
        {
            var client = server.CreateClient();
            using var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session"));
            signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            client.DefaultRequestHeaders.Add("Cookie", (await client.SendAsync(signIn)).Headers.GetValues("Set-Cookie").Single().Split(';')[0]);
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
            return client;
        }

        var mine = await Browser();
        var other = await Browser();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString());

        var result = await (await mine.PostAsJsonAsync(Api("/sign-ins/sign-out-others"), new { })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(2, result.GetProperty("signedOut").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await mine.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await other.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await tv.GetAsync(Api("/system/status"))).StatusCode);
    }

    [Fact]
    public async Task TheWebhookSecret_CanBeReplaced()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var old = (await admin.GetFromJsonAsync<JsonElement>(Api("/settings"))).GetProperty("webhookSecret").GetString();

        var fresh = (await (await admin.PostAsJsonAsync(Api("/settings/webhook-secret/rotate"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("webhookSecret").GetString();
        var anonymous = server.CreateClient();

        Assert.NotEqual(old, fresh);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsJsonAsync(Api($"/webhooks/radarr?secret={old}"), new { eventType = "Test" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anonymous.PostAsJsonAsync(Api($"/webhooks/radarr?secret={fresh}"), new { eventType = "Test" })).StatusCode);
    }

    [Fact]
    public async Task ADevice_WatchingAsAnotherProfile_CantManageTheServer_UntilTheMainProfileIsChosenAgain()
    {
        await Admin().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "4826" });
        var kid = await CreateProfileAsync("Kid on TV");
        var tv = Device(await DeviceTokenAsync("Kid's TV"));

        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode); // signed in with the key, so unlocked
        await tv.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null });

        // The choice is kept on the server: dropping the profile header doesn't make it the main profile again.
        var me = await tv.GetFromJsonAsync<JsonElement>(Api("/me"));
        Assert.Equal((kid, false), (me.GetProperty("profileId").GetString(), me.GetProperty("isAdmin").GetBoolean()));
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api("/settings"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/pairing/code/ABC-DEF/approve"), new { })).StatusCode);

        // Watching as the main profile doesn't take its PIN on a device the owner paired; managing the server does.
        var withoutPin = await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = (string?)null });
        var settingsLocked = await tv.GetAsync(Api("/settings"));
        var wrongPin = await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "0000" });
        var unlocked = await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "4826" });

        Assert.Equal(HttpStatusCode.OK, withoutPin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, settingsLocked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode);
        await factory.SetMainPinAsync(null);
    }

    [Fact]
    public async Task TheApiKeyItself_StaysAdmin_WhicheverProfileItWatchesAs()
    {
        var kid = await CreateProfileAsync("Kid with key");
        var token = (await (await Admin().PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var client = Admin();
        client.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api("/settings"))).StatusCode);
    }

    [Fact]
    public async Task ARemovedProfile_SendsItsDevicesBackToThePicker_WithoutErrors()
    {
        var gone = await CreateProfileAsync("Leaving");
        var tv = Device(await DeviceTokenAsync("Spare TV"));
        await tv.PostAsJsonAsync(Api($"/profiles/{gone}/select"), new { pin = (string?)null });
        var headerToken = (await (await Admin().PostAsJsonAsync(Api($"/profiles/{gone}/select"), new { pin = (string?)null })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();

        await Admin().DeleteAsync(Api($"/profiles/{gone}"));
        var withHeader = Admin();
        withHeader.DefaultRequestHeaders.Add("X-Flexarr-Profile", headerToken);

        var me = await tv.GetFromJsonAsync<JsonElement>(Api("/me"));
        Assert.Equal(("owner", false), (me.GetProperty("profileId").GetString(), me.GetProperty("isAdmin").GetBoolean()));
        Assert.Equal("owner", (await withHeader.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task ChangingAPin_RetiresTokensIssuedBefore()
    {
        var id = await CreateProfileAsync("Repinned", pin: "1357");
        var token = (await (await Admin().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { pin = "1357" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var client = Admin();
        client.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);
        var before = (await client.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString();

        await Admin().PutAsJsonAsync(Api($"/profiles/{id}"), new { name = "Repinned", pin = "9753" });

        Assert.Equal(id, before);
        Assert.Equal("owner", (await client.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());
    }


    [Fact]
    public async Task RotatingTheKey_KeepsOwnSignIns_ButRetiresTheOldKey()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var device = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();

        var rotated = await (await admin.PostAsJsonAsync(Api("/settings/api-key/rotate"), new { })).Content.ReadFromJsonAsync<JsonElement>();
        var newKey = rotated.GetProperty("apiKey").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", device);
        var withNewKey = server.CreateClient();
        withNewKey.DefaultRequestHeaders.Add("X-Api-Key", newKey);

        Assert.NotEqual(FlexarrApiFactory.ApiKey, newKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await admin.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await withNewKey.GetAsync(Api("/system/status"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/system/status"))).StatusCode);
    }

    [Fact]
    public async Task Webhooks_TakeTheirOwnSecret()
    {
        var secret = (await Admin().GetFromJsonAsync<JsonElement>(Api("/settings"))).GetProperty("webhookSecret").GetString();
        var anonymous = factory.CreateClient();

        var withSecret = await anonymous.PostAsJsonAsync(Api($"/webhooks/radarr?secret={secret}"), new { eventType = "Test" });
        var wrong = await anonymous.PostAsJsonAsync(Api("/webhooks/radarr?secret=nope"), new { eventType = "Test" });
        var secretAsKey = factory.CreateClient();
        secretAsKey.DefaultRequestHeaders.Add("X-Api-Key", secret);

        Assert.Equal(HttpStatusCode.OK, withSecret.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await secretAsKey.GetAsync(Api("/settings"))).StatusCode); // it's good for nothing else
    }

    [Fact]
    public async Task TestingAConnection_OnlyReusesTheSavedKey_ForTheSameServer()
    {
        await Admin().PutAsJsonAsync(Api("/settings"), new { arr = new[] { new { name = "Sonarr", kind = "Sonarr", baseUrl = "http://127.0.0.1:1", apiKey = "sonarr-key" } } });

        var elsewhere = await Admin().PostAsJsonAsync(Api("/arr/test"), new { name = "Sonarr", kind = "Sonarr", baseUrl = "http://10.0.0.99:8989" });
        var notWeb = await Admin().PostAsJsonAsync(Api("/arr/test"), new { name = "Sonarr", kind = "Sonarr", baseUrl = "file:///etc/passwd", apiKey = "x" });
        var saveNotWeb = await Admin().PutAsJsonAsync(Api("/settings"), new { arr = new[] { new { name = "Sonarr", kind = "Sonarr", baseUrl = "ftp://nas/", apiKey = "x" } } });

        Assert.Equal(HttpStatusCode.BadRequest, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notWeb.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, saveNotWeb.StatusCode);
        await Admin().PutAsJsonAsync(Api("/settings"), new { arr = Array.Empty<object>() });
    }

    [Fact]
    public async Task TheApiDescription_IsntPublished()
    {
        var response = await factory.CreateClient().GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        Assert.NotEqual("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task ProfilePictures_AreOnlyServedAtTheSignedUrl()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var owner = await db.Profiles.FindAsync("owner");
            owner!.ImageUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            var path = scope.ServiceProvider.GetRequiredService<ProfileImages>().PathFor("owner");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, [0xFF, 0xD8, 0xFF]);
        }

        var profiles = await Admin().GetFromJsonAsync<JsonElement>(Api("/profiles"));
        var url = profiles.EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean()).GetProperty("image").GetString()!;
        var anonymous = factory.CreateClient();

        Assert.Equal(HttpStatusCode.OK, (await anonymous.GetAsync(new Uri(url, UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(Api("/profiles/owner/image"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(new Uri(url[..^4] + "0000", UriKind.Relative))).StatusCode);
    }
}
