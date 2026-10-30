using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Managing the server takes the main profile's PIN, given lately on that browser or device; watching as it doesn't.</summary>
public class AdminLockTests
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static HttpClient Admin(FlexarrApiFactory server)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A TV on the main profile, paired with the key and then locked, as it is once the unlock has lapsed.</summary>
    private static async Task<HttpClient> LockedTvAsync(FlexarrApiFactory server, string name = "Living Room TV")
    {
        var token = (await (await Admin(server).PostAsJsonAsync(Api("/session/device"), new { deviceName = name })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.Equal(HttpStatusCode.NoContent, (await tv.PostAsJsonAsync(Api("/admin/lock"), new { })).StatusCode);
        return tv;
    }

    private static async Task<JsonElement> MeAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>(Api("/me"));

    [Fact]
    public async Task ALockedMainProfile_StillSeesWhatsLeftOfTheSetupGuide()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);

        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/setup-guide"))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tv.PutAsJsonAsync(Api("/setup-guide"), new { dismissed = true })).StatusCode); // Hide
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api("/arr/discover"))).StatusCode); // the guide's own steps take the PIN
    }

    [Fact]
    public async Task ALockedMainProfile_Watches_ButManagingTheServerTakesItsPin()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);

        var me = await MeAsync(tv);
        Assert.Equal(("owner", false, true), (me.GetProperty("profileId").GetString(), me.GetProperty("isAdmin").GetBoolean(), me.GetProperty("canUnlock").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api("/settings"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/pairing/waiting"))).StatusCode); // it hears who's asking to connect
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/pairing/code/ABCD-EFGH/approve"), new { deviceName = "TV" })).StatusCode);

        var unlocked = await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" });

        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.NotNull((await unlocked.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("unlockedUntil").GetString());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode);
        Assert.True((await MeAsync(tv)).GetProperty("isAdmin").GetBoolean());
    }

    [Fact]
    public async Task ChoosingAnotherProfile_OrLock_LocksItAgain()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);
        var kid = (await (await Admin(server).PostAsJsonAsync(Api("/profiles"), new { name = "Kid" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();

        await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" });
        await tv.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null });
        await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = (string?)null });
        var afterSwitching = await tv.GetAsync(Api("/settings"));
        await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" });
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });
        var afterLock = await tv.GetAsync(Api("/settings"));

        Assert.Equal(HttpStatusCode.Forbidden, afterSwitching.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, afterLock.StatusCode);
    }

    [Fact]
    public async Task ChoosingTheMainProfile_WithItsPin_AlsoUnlocks()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);

        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "1111" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "2580" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode);
    }

    [Fact]
    public async Task AskingForThePin_WhenChoosingTheMainProfile_IsTheOwnersChoice()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);
        await Admin(server).PutAsJsonAsync(Api("/settings"), new { askPinForMainProfile = true });

        var withoutPin = await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = (string?)null });
        await Admin(server).PutAsJsonAsync(Api("/settings"), new { askPinForMainProfile = false });

        Assert.Equal(HttpStatusCode.Forbidden, withoutPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = (string?)null })).StatusCode);
    }

    [Fact]
    public void AnUnlock_Lapses_UnlessChangesKeepItGoing()
    {
        var clock = new MovableClock();
        var unlocks = new AdminUnlocks(clock);

        unlocks.Unlock("tv");
        clock.Now += AdminUnlocks.Lasts - TimeSpan.FromMinutes(1);
        unlocks.Extend("tv");
        clock.Now += AdminUnlocks.Lasts - TimeSpan.FromMinutes(1);
        var stillUnlocked = unlocks.IsUnlocked("tv");
        clock.Now += TimeSpan.FromMinutes(2);

        Assert.True(stillUnlocked);
        Assert.False(unlocks.IsUnlocked("tv"));
        unlocks.Extend("tv"); // a lapsed unlock isn't brought back
        Assert.False(unlocks.IsUnlocked("tv"));
        unlocks.Unlock("phone");
        unlocks.Lock("phone");
        unlocks.Extend("phone"); // nor a locked one
        Assert.False(unlocks.IsUnlocked("phone"));
    }

    [Fact]
    public async Task TheHouseholdsCollections_ShowAsTheMainProfiles_AndChangingThemTakesThePin()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var created = await (await Admin(server).PostAsJsonAsync(Api("/collections"), new { name = "Family films", personal = false })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var tv = await LockedTvAsync(server);

        var listed = (await tv.GetFromJsonAsync<JsonElement>(Api("/collections"))).EnumerateArray().Single(c => c.GetProperty("id").GetString() == id);
        var renamedLocked = await tv.PutAsJsonAsync(Api($"/collections/{id}"), new { name = "Films" });
        var madeLocked = await tv.PostAsJsonAsync(Api("/collections"), new { name = "Ours" });
        await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" });
        var renamed = await tv.PutAsJsonAsync(Api($"/collections/{id}"), new { name = "Films" });

        Assert.True(listed.GetProperty("canEdit").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, renamedLocked.StatusCode); // a 403 the apps answer with the PIN
        Assert.Equal(HttpStatusCode.Forbidden, madeLocked.StatusCode); // the household's by default, as before
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
    }

    [Fact]
    public async Task GuessingTheUnlock_LocksTheGuesserOut_WithTheStricterCount()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);
        var lockedOut = new List<string>();
        server.Services.GetRequiredService<PinGuard>().CallerLockedOut += (profile, _) => lockedOut.Add(profile);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = $"000{i}" })).StatusCode);
        }

        // A locked main profile isn't already managing the server, so it gets four tries, not the eight admins share.
        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(["owner"], lockedOut);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" })).StatusCode);
        // The key still gets in, and is how a forgotten PIN is replaced.
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/admin/unlock"), new { apiKey = FlexarrApiFactory.ApiKey })).StatusCode);
    }

    [Fact]
    public async Task WithoutAPin_NobodyCanUnlock_UntilTheOwnerGivesOne()
    {
        using var server = new FlexarrApiFactory();
        var tv = await LockedTvAsync(server);
        var kid = (await (await Admin(server).PostAsJsonAsync(Api("/profiles"), new { name = "Kid" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var kidsTv = await LockedTvAsync(server, "Kid's TV");
        await kidsTv.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null });

        var me = await MeAsync(tv);
        var unlock = await tv.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "1234" });
        var fromKid = await kidsTv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "1234" });
        var tooShort = await tv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "12" });
        var first = await tv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "1234" });
        var again = await tv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "5678" });

        Assert.True(me.GetProperty("adminPinMissing").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, unlock.StatusCode);
        Assert.True((await unlock.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("pinMissing").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, fromKid.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooShort.StatusCode);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode); // setting it unlocks
        Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode); // once there's one, a new one takes the key
        Assert.False((await MeAsync(tv)).GetProperty("adminPinMissing").GetBoolean());
    }

    [Fact]
    public async Task AForgottenPin_IsReplacedWithTheApiKey_AndEverywhereElseLocks()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var tv = await LockedTvAsync(server);
        var phone = await LockedTvAsync(server, "Phone");
        await phone.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "2580" });

        var wrongKey = await tv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "9999", apiKey = "not-the-key" });
        var reset = await tv.PostAsJsonAsync(Api("/admin/pin"), new { pin = "9999", apiKey = FlexarrApiFactory.ApiKey });

        Assert.Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.StartsWith("owner.", (await reset.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/settings"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.GetAsync(Api("/settings"))).StatusCode); // it knew the old one
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "9999" })).StatusCode);
    }

    [Fact]
    public async Task TheMainProfilesPin_CantBeTakenOff()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");

        var removed = await Admin(server).PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true });

        Assert.Equal(HttpStatusCode.Conflict, removed.StatusCode);
    }

    private sealed class MovableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
