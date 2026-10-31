using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>The admin PIN and the main profile's own PIN, once one, kept apart.</summary>
public class PinSplitTests
{
    /// <summary>The main profile as the migration leaves it: its one PIN copied into the admin PIN.</summary>
    private static async Task<(IServiceScope Scope, FlexarrDbContext Db, Profile Main)> UpgradedAsync(FlexarrApiFactory server)
    {
        var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!;
        main.SetPin("2580");
        main.AdminPinHash = main.PinHash;
        await db.SaveChangesAsync();
        return (scope, db, main);
    }

    [Fact]
    public async Task Upgrading_TheMainProfileLosesTheCopy_UnlessItWasAskedFor()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;
        var tokenBound = main.TokenPinHash;

        await PinSplit.SettleAsync(db, askedForMainProfile: false, CancellationToken.None);

        Assert.Null(main.PinHash);
        Assert.True(main.CheckAdminPin("2580"));
        Assert.Equal(tokenBound, main.TokenPinHash); // nobody on the main profile is signed out
    }

    [Fact]
    public async Task Upgrading_KeepsTheMainProfilesPin_WhenItWasAskedFor()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;

        await PinSplit.SettleAsync(db, askedForMainProfile: true, CancellationToken.None);

        Assert.True(main.CheckPin("2580"));
        Assert.True(main.CheckAdminPin("2580"));
    }

    [Fact]
    public async Task APinChosenSince_IsNeverTakenOff()
    {
        using var server = new FlexarrApiFactory();
        var (scope, db, main) = await UpgradedAsync(server);
        using var _ = scope;
        main.SetPin("2580"); // the same digits, chosen again: a hash of its own

        await PinSplit.SettleAsync(db, askedForMainProfile: false, CancellationToken.None);

        Assert.True(main.CheckPin("2580"));
        Assert.NotEqual(main.AdminPinHash, main.PinHash);
    }

    [Fact]
    public async Task TheOldAskForThePinSetting_SaysWhetherTheMainProfileHasAPin_AndSavingItDoesNothing()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var settings = new Uri("/api/v1/settings", UriKind.Relative);

        var before = (await admin.GetFromJsonAsync<JsonElement>(settings)).GetProperty("askPinForMainProfile").GetBoolean();
        await admin.PutAsJsonAsync(settings, new { askPinForMainProfile = true });
        var afterSaving = (await admin.GetFromJsonAsync<JsonElement>(settings)).GetProperty("askPinForMainProfile").GetBoolean();
        await server.SetMainProfilePinAsync("1357");
        var withPin = (await admin.GetFromJsonAsync<JsonElement>(settings)).GetProperty("askPinForMainProfile").GetBoolean();

        Assert.False(before);
        Assert.False(afterSaving);
        Assert.True(withPin);
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    /// <summary>A TV paired with the key, on the main profile, its unlock lapsed.</summary>
    private static async Task<HttpClient> TvAsync(FlexarrApiFactory server, string name = "TV")
    {
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = name })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });
        return tv;
    }

    [Fact]
    public async Task GuessingTheAdminPin_HoldsOffThatDevice_NotTheOwnersOthers()
    {
        // Either PIN chooses the main profile, so they're counted together: a guesser can't switch to the other one.
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        await server.SetMainProfilePinAsync("1357");
        var guesser = await TvAsync(server, "Guesser");
        var owners = await TvAsync(server, "Owner's TV");
        for (var i = 0; i < 5; i++)
        {
            await guesser.PostAsJsonAsync(Api("/admin/unlock"), new { pin = $"000{i}" });
        }

        var guesserOwnPin = await guesser.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "1357" });
        await Task.Delay(PinGuard.ProfileThrottle); // everyone else is only slowed down
        var ownersOwnPin = await owners.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "1357" });

        Assert.Equal(HttpStatusCode.TooManyRequests, guesserOwnPin.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ownersOwnPin.StatusCode);
    }

    [Fact]
    public async Task OnlyTheAdminPin_LetsAChildOff_NotTheMainProfilesOwn()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        await server.SetMainProfilePinAsync("1357");
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var kid = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var tv = await TvAsync(server);
        await tv.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { });

        var lockedWithOwn = await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = "1357" });
        var locked = await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = "2580" });
        var releasedWithOwn = await tv.PostAsJsonAsync(Api("/profiles/kids-lock/release"), new { pin = "1357" });
        var released = await tv.PostAsJsonAsync(Api("/profiles/kids-lock/release"), new { pin = "2580" });

        Assert.Equal(HttpStatusCode.Forbidden, lockedWithOwn.StatusCode);
        Assert.True((await lockedWithOwn.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("parentPin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, locked.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, releasedWithOwn.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, released.StatusCode);
    }

    [Fact]
    public async Task TheProfileList_ShowsTheMainProfilesOwnPin_NotTheAdminPin()
    {
        // A guest over Tailscale choosing it gives the admin PIN instead: see RemoteAccessTests.
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync("2580");
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var before = (await admin.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean());
        await server.SetMainProfilePinAsync("1357");
        var after = (await admin.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean());

        Assert.Equal((false, false), (before.GetProperty("hasPin").GetBoolean(), before.GetProperty("asksPin").GetBoolean()));
        Assert.Equal((true, true), (after.GetProperty("hasPin").GetBoolean(), after.GetProperty("asksPin").GetBoolean()));
    }
}
