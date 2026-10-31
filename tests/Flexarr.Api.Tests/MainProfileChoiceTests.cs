using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// Choosing the main profile once its own PIN is apart from the admin PIN: where the owner didn't sign in, only the
/// admin PIN chooses it; on the owner's devices either does, counted once.
/// </summary>
public class MainProfileChoiceTests
{
    private const string AdminPin = "2580";
    private const string OwnPin = "1357";

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task SetUpAsync(IServiceProvider services, string? guestLogin = null, bool kids = false, bool ownPin = true, bool samePin = false)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!;
        main.SetAdminPin(AdminPin);
        main.SetPin(ownPin ? OwnPin : null);
        if (samePin)
        {
            main.PinHash = main.AdminPinHash; // as a server upgraded with the PIN asked for keeps it
        }

        if (guestLogin is not null)
        {
            db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = "Teen", TailscaleLogin = guestLogin });
        }

        if (kids)
        {
            db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = "Ada", MaxRatingAge = 8, IsKids = true });
        }

        await db.SaveChangesAsync();
    }

    private static HttpClient Guest(RemoteAccessFactory server, string address, string login)
    {
        server.Whois.Owners[address] = login;
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", "127.0.0.1");
        client.DefaultRequestHeaders.Add("X-Forwarded-For", address);
        client.DefaultRequestHeaders.Add("Tailscale-User-Login", login);
        client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        return client;
    }

    private static Task<HttpResponseMessage> ChooseMainAsync(HttpClient client, string? pin) =>
        client.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin });

    private static async Task<string?> ProfileIdAsync(HttpClient client) => (await client.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString();

    [Fact]
    public async Task AGuestWhoKnowsTheMainProfilesOwnPin_CantChooseIt_OnlyTheAdminPinDoes()
    {
        using var server = new RemoteAccessFactory();
        await SetUpAsync(server.Services, guestLogin: "teen@example.com");
        var guest = Guest(server, "100.70.2.10", "teen@example.com");

        var profiles = await guest.GetFromJsonAsync<JsonElement>(Api("/profiles"));
        var main = profiles.EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean());
        var ownPin = await ChooseMainAsync(guest, OwnPin);
        var adminPin = await ChooseMainAsync(guest, AdminPin);

        Assert.True(main.GetProperty("asksAdminPin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, ownPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminPin.StatusCode);
    }

    [Fact]
    public async Task AGuestsMainProfileToken_AloneDoesntChooseIt_SoChangingTheAdminPinCutsThemOff()
    {
        using var server = new RemoteAccessFactory();
        await SetUpAsync(server.Services, guestLogin: "teen2@example.com");
        var guest = Guest(server, "100.70.2.11", "teen2@example.com");
        var chosen = await ChooseMainAsync(guest, AdminPin);
        var token = (await chosen.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        // The admin PIN gave the browser a sign-in of its own (the cookie); the token without it is ignored.
        var tokenOnly = Guest(server, "100.70.2.11", "teen2@example.com");
        tokenOnly.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        Assert.Contains(chosen.Headers.GetValues("Set-Cookie"), c => c.StartsWith("flexarr.session=", StringComparison.Ordinal));
        Assert.NotEqual(PlaybackState.DefaultUser, await ProfileIdAsync(tokenOnly));
    }

    [Fact]
    public async Task AtHomeWithKids_ABrowserWithNoSignIn_ChoosesTheMainProfileOnlyWithTheAdminPin()
    {
        using var server = new RemoteAccessFactory();
        await SetUpAsync(server.Services, kids: true, ownPin: false);
        var browser = server.CreateClient();
        browser.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.60");
        browser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");

        var freely = await ChooseMainAsync(browser, null);
        var withAdminPin = await ChooseMainAsync(browser, AdminPin);

        Assert.Equal(HttpStatusCode.Forbidden, freely.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withAdminPin.StatusCode);
    }

    [Fact]
    public async Task OnTheOwnersDevice_TheAdminPinChoosesTheMainProfile_AgainAndAgain_WithoutLockingItOut()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 6; i++)
        {
            statuses.Add((await ChooseMainAsync(tv, i % 2 == 0 ? AdminPin : OwnPin)).StatusCode);
        }

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
    }

    [Fact]
    public async Task UpgradedWithThePinAskedFor_ChoosingTheMainProfileWithIt_StillUnlocksSettings()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services, samePin: true);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });

        Assert.Equal(HttpStatusCode.OK, (await ChooseMainAsync(tv, AdminPin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode);
    }

    [Fact]
    public async Task AForgottenPin_ComesOffWithTheAdminPin_FromThePicker()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var claire = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Claire", pin = "4444" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });

        var withOwnPin = await tv.PostAsJsonAsync(Api($"/profiles/{claire}/forgot-pin"), new { adminPin = OwnPin });
        var withAdminPin = await tv.PostAsJsonAsync(Api($"/profiles/{claire}/forgot-pin"), new { adminPin = AdminPin });
        var chosen = await tv.PostAsJsonAsync(Api($"/profiles/{claire}/select"), new { pin = (string?)null });

        Assert.Equal(HttpStatusCode.Forbidden, withOwnPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, withAdminPin.StatusCode);
        Assert.False((await withAdminPin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
    }

    [Fact]
    public async Task ChangingTheAdminPinTheOldWay_IsRefused_RatherThanChangingTheMainProfilesOwn()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services, ownPin: false);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var oldWay = await admin.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "9999", currentPin = AdminPin });
        var profiles = await admin.GetFromJsonAsync<JsonElement>(Api("/profiles"));

        Assert.Equal(HttpStatusCode.Conflict, oldWay.StatusCode);
        Assert.False(profiles.EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean()).GetProperty("hasPin").GetBoolean());
    }

    [Fact]
    public async Task TheMainProfilesOwnPin_DoesntWipeWrongGuessesAtTheAdminPin()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Family TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });

        // Three wrong guesses, the own PIN in between, then one more: the fourth wrong one locks it out all the same.
        for (var i = 0; i < 3; i++)
        {
            await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = $"000{i}" });
        }

        var ownPin = await ChooseMainAsync(tv, OwnPin);
        await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "0003" });
        var rightAfter = await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = AdminPin });

        Assert.Equal(HttpStatusCode.OK, ownPin.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, rightAfter.StatusCode);
    }

    [Fact]
    public async Task WithNoAdminPinYet_AGuestIsToldTheMainProfileTakesIt_NotAskedForItsOwn()
    {
        using var server = new RemoteAccessFactory();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!.SetPin(OwnPin);
            db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = "Teen", TailscaleLogin = "teen3@example.com" });
            await db.SaveChangesAsync();
        }

        var guest = Guest(server, "100.70.2.12", "teen3@example.com");
        var main = (await guest.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean());

        Assert.True(main.GetProperty("asksAdminPin").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await ChooseMainAsync(guest, OwnPin)).StatusCode);
    }
}
