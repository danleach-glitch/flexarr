using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Kids profiles: an age limit on what they see, and no way out without the main profile's PIN.</summary>
public class KidsProfileTests
{
    private const string MainPin = "2580";

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static HttpClient Admin(FlexarrApiFactory server)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A TV paired with the key: a device sign-in of its own, which remembers the profile chosen on it.</summary>
    private static async Task<HttpClient> TvAsync(FlexarrApiFactory server)
    {
        var token = (await (await Admin(server).PostAsJsonAsync(Api("/session/device"), new { deviceName = "Playroom TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        return tv;
    }

    private static async Task<string> CreateAsync(FlexarrApiFactory server, string name, int? maxRatingAge = null, bool isKids = false)
    {
        var response = await Admin(server).PostAsJsonAsync(Api("/profiles"), new { name, maxRatingAge, isKids });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private static async Task<HttpResponseMessage> SelectAsync(HttpClient client, string id, object? body = null) =>
        await client.PostAsJsonAsync(Api($"/profiles/{id}/select"), body ?? new { });

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private static async Task<Dictionary<string, Guid>> SeedAsync(FlexarrApiFactory server)
    {
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var films = new Library { Name = "Films", Type = LibraryType.Movies };
        var shows = new Library { Name = "TV", Type = LibraryType.TvShows };
        MediaItem Item(Library library, MediaKind kind, string title, string? rating, Guid? parent = null) =>
            new() { LibraryId = library.Id, Kind = kind, Title = title, SortTitle = title.ToLowerInvariant(), ContentRating = rating, ParentId = parent };
        var paddington = Item(films, MediaKind.Movie, "Kids Paddington", "PG");
        var alien = Item(films, MediaKind.Movie, "Kids Alien", "R");
        var unrated = Item(films, MediaKind.Movie, "Kids Unrated", null);
        var bluey = Item(shows, MediaKind.Series, "Kids Bluey", "TV-Y");
        db.AddRange(films, shows, paddington, alien, unrated, bluey);
        await db.SaveChangesAsync();
        var season = Item(shows, MediaKind.Season, "Season 1", null, bluey.Id);
        db.Add(season);
        await db.SaveChangesAsync();
        var episode = Item(shows, MediaKind.Episode, "Magic Xylophone", null, season.Id);
        db.Add(episode);
        await db.SaveChangesAsync();
        return new() { ["paddington"] = paddington.Id, ["alien"] = alien.Id, ["unrated"] = unrated.Id, ["bluey"] = bluey.Id, ["episode"] = episode.Id };
    }

    [Fact]
    public async Task AKidsProfile_NeedsAnAgeLimit_AndTheMainProfilesPin_AndTheMainProfileCantHaveOne()
    {
        using var server = new FlexarrApiFactory();
        var admin = Admin(server);

        var noPin = await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true });
        await server.SetMainPinAsync(MainPin);
        var noLimit = await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Ada", isKids = true });
        var oddAge = await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Ada", maxRatingAge = 10 });
        var main = await admin.PutAsJsonAsync(Api("/profiles/owner/age-limit"), new { maxRatingAge = 12 });
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var teen = await CreateAsync(server, "Sam", 15);

        Assert.Equal(HttpStatusCode.Conflict, noPin.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noLimit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, oddAge.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, main.StatusCode);
        var profiles = await admin.GetFromJsonAsync<JsonElement>(Api("/profiles"));
        var byId = profiles.EnumerateArray().ToDictionary(p => p.GetProperty("id").GetString()!);
        Assert.Equal((8, true), (byId[ada].GetProperty("maxRatingAge").GetInt32(), byId[ada].GetProperty("isKids").GetBoolean()));
        Assert.Equal((15, false), (byId[teen].GetProperty("maxRatingAge").GetInt32(), byId[teen].GetProperty("isKids").GetBoolean()));

        // Taking the limit off again.
        var cleared = await JsonAsync(await admin.PutAsJsonAsync(Api($"/profiles/{teen}/age-limit"), new { maxRatingAge = (int?)null }));
        Assert.Equal(JsonValueKind.Null, cleared.GetProperty("maxRatingAge").ValueKind);
    }

    [Fact]
    public async Task AnAgeLimit_ShowsOnlyWhatsRatedWithinIt_AndKeepsItFromRequests()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var items = await SeedAsync(server);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var tv = await TvAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await SelectAsync(tv, ada)).StatusCode);

        var search = await tv.GetFromJsonAsync<JsonElement>(Api("/search?q=Kids"));
        var titles = search.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        var me = await tv.GetFromJsonAsync<JsonElement>(Api("/me"));
        var requests = await tv.GetFromJsonAsync<JsonElement>(Api("/requests/status"));

        Assert.Contains(items["paddington"], titles);
        Assert.Contains(items["bluey"], titles);
        Assert.DoesNotContain(items["alien"], titles);
        Assert.DoesNotContain(items["unrated"], titles);
        Assert.Equal(HttpStatusCode.NotFound, (await tv.GetAsync(Api($"/items/{items["alien"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api($"/items/{items["episode"]}"))).StatusCode); // Bluey's own
        Assert.Equal((true, 8), (me.GetProperty("isKids").GetBoolean(), me.GetProperty("maxRatingAge").GetInt32()));
        Assert.False(requests.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/requests"), new { mediaType = "movie", tmdbId = 603 })).StatusCode);
        // What Flexarr can't check the age of is empty rather than refused, so screens a child opens don't show errors.
        Assert.Equal(0, (await tv.GetFromJsonAsync<JsonElement>(Api("/podcasts/search?q=news"))).GetArrayLength());
        Assert.Equal(0, (await tv.GetFromJsonAsync<JsonElement>(Api("/livetv/recordings"))).GetProperty("recent").GetArrayLength());
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/livetv/recordings"), new { programmeId = 1 })).StatusCode);
    }

    [Fact]
    public async Task AParent_CanAllowOrBlockATitle_ForEveryLimitedProfile()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var items = await SeedAsync(server);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var tv = await TvAsync(server);
        await SelectAsync(tv, ada);
        var admin = Admin(server);

        var allow = await admin.PutAsJsonAsync(Api($"/items/{items["unrated"]}/kids"), new { @override = "Allow" });
        var block = await admin.PutAsJsonAsync(Api($"/items/{items["bluey"]}/kids"), new { @override = "Block" });
        var episode = await admin.PutAsJsonAsync(Api($"/items/{items["episode"]}/kids"), new { @override = "Allow" });
        var detail = await admin.GetFromJsonAsync<JsonElement>(Api($"/items/{items["bluey"]}"));

        Assert.Equal(HttpStatusCode.NoContent, allow.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, block.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, episode.StatusCode); // the show decides for its episodes
        Assert.Equal(("Block", 0), (detail.GetProperty("kidsOverride").GetString(), detail.GetProperty("ratingAge").GetInt32()));
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api($"/items/{items["unrated"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tv.GetAsync(Api($"/items/{items["bluey"]}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await tv.GetAsync(Api($"/items/{items["episode"]}"))).StatusCode);
    }

    [Fact]
    public async Task WithoutChildLock_AKidsProfile_CanBeLeftForAnyProfile_WithNoPinButItsOwn()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var ben = await CreateAsync(server, "Ben", 0, isKids: true);
        var mum = await CreateAsync(server, "Mum");
        var tv = await TvAsync(server);
        await SelectAsync(tv, ada);

        var toBen = await SelectAsync(tv, ben);
        var toMum = await SelectAsync(tv, mum);
        await SelectAsync(tv, ada);
        var toMain = await SelectAsync(tv, "owner");
        var main = (await tv.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().Single(p => p.GetProperty("isMain").GetBoolean());

        Assert.Equal(HttpStatusCode.OK, toBen.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toMum.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toMain.StatusCode); // its PIN guards the server's settings, not watching as it
        Assert.False(main.GetProperty("asksPin").GetBoolean());
    }

    [Fact]
    public async Task ADeviceLockedToKids_OffersOnlyThem_UntilTheMainProfilesPinReleasesIt()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var ben = await CreateAsync(server, "Ben", 0, isKids: true);
        var mum = await CreateAsync(server, "Mum");
        var tv = await TvAsync(server);

        var noPin = await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = "1111" });
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { }); // as it is once the owner's unlock has lapsed
        var wrongPin = await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = "1111" });
        var moveByLocking = await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = MainPin, profileId = ben });
        var toBen = await SelectAsync(tv, ben); // PG to U is stricter: no PIN
        var offered = (await tv.GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().Select(p => p.GetProperty("id").GetString()).ToList();
        var toMum = await SelectAsync(tv, mum, new { parentPin = MainPin });
        var toAdaAlone = await SelectAsync(tv, ada); // between kids profiles: no PIN, child lock or not
        var toAda = await SelectAsync(tv, ada, new { parentPin = MainPin });
        var me = await tv.GetFromJsonAsync<JsonElement>(Api("/me"));
        var devices = await Admin(server).GetFromJsonAsync<JsonElement>(Api("/sign-ins"));

        Assert.Equal(HttpStatusCode.OK, noPin.StatusCode); // the owner's own unlocked TV needs no PIN
        Assert.Equal(HttpStatusCode.OK, wrongPin.StatusCode); // already locked on that profile: nothing to check
        Assert.Equal(HttpStatusCode.Conflict, moveByLocking.StatusCode); // moving goes through Who's watching
        Assert.Equal(HttpStatusCode.OK, toBen.StatusCode);
        Assert.Equal([ada, ben], offered);
        Assert.Equal(HttpStatusCode.Forbidden, toMum.StatusCode);
        Assert.True((await JsonAsync(toMum)).GetProperty("kidsLock").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, toAdaAlone.StatusCode);
        Assert.Equal(HttpStatusCode.OK, toAda.StatusCode);
        Assert.Equal((ada, true), (me.GetProperty("profileId").GetString(), me.GetProperty("kidsLock").GetBoolean()));
        Assert.Contains(devices.EnumerateArray(), d => d.GetProperty("kidsLock").GetBoolean());

        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api("/profiles/kids-lock/release"), new { pin = "1111" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await tv.PostAsJsonAsync(Api("/profiles/kids-lock/release"), new { pin = MainPin })).StatusCode);
        Assert.Equal(4, (await tv.GetFromJsonAsync<JsonElement>(Api("/profiles"))).GetArrayLength());
        Assert.Equal(HttpStatusCode.OK, (await SelectAsync(tv, mum, new { parentPin = MainPin })).StatusCode);
    }

    [Fact]
    public async Task TheMainProfile_TurnsChildLockOn_WithoutItsPin_ButAnotherProfileNeedsIt()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        await CreateAsync(server, "Ada", 8, isKids: true);
        var mum = await CreateAsync(server, "Mum");
        var tv = await TvAsync(server);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { }); // settings locked here, as they usually are
        var phone = await TvAsync(server);
        await SelectAsync(phone, mum);

        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await phone.PostAsJsonAsync(Api("/profiles/kids-lock"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await phone.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = MainPin })).StatusCode);
    }

    [Fact]
    public async Task TurningChildLockOff_GivesTheDevicesNotificationsBackToTheMainProfile()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var tv = await TvAsync(server);
        var signIn = (await Admin(server).GetFromJsonAsync<JsonElement>(Api("/sign-ins"))).EnumerateArray()
            .First(d => d.GetProperty("name").GetString() == "Playroom TV").GetProperty("id").GetString()!;
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.NotificationDevices.Add(new Flexarr.Core.Notifications.NotificationDevice
            {
                Id = "phone", SignInId = signIn, ProfileId = PlaybackState.DefaultUser, Token = "t", Environment = "sandbox", Key = new byte[32],
            });
            await db.SaveChangesAsync();
        }

        string ProfileOfNotifications()
        {
            using var scope = server.Services.CreateScope();
            return scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().NotificationDevices.Single(d => d.Id == "phone").ProfileId;
        }

        await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { });
        var whileLocked = ProfileOfNotifications();
        await tv.PostAsJsonAsync(Api("/profiles/kids-lock/release"), new { pin = MainPin });
        var afterRelease = ProfileOfNotifications();
        await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = MainPin });
        await Admin(server).PostAsJsonAsync(Api($"/sign-ins/{signIn}/release-kids-lock"), new { });

        Assert.Equal(ada, whileLocked);
        Assert.Equal(PlaybackState.DefaultUser, afterRelease);
        Assert.Equal(PlaybackState.DefaultUser, ProfileOfNotifications());
    }

    [Fact]
    public async Task APhoneOnTheMainProfile_HearsDevicesAskingToConnect_WhateverProfileItsNotificationsFollow()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var phone = await TvAsync(server);
        var other = await TvAsync(server);
        await SelectAsync(other, ada);
        var signIns = server.Services.GetRequiredService<Flexarr.Api.Authentication.SignIns>();
        var list = (await Admin(server).GetFromJsonAsync<JsonElement>(Api("/sign-ins"))).EnumerateArray().Select(d => d.GetProperty("id").GetString()!).ToList();
        var onMain = list.First(id => signIns.ById(id)?.ProfileId is null && signIns.ById(id)?.Kind == Flexarr.Api.Authentication.SignInKind.Device);
        var onAda = list.First(id => signIns.ById(id)?.ProfileId == ada);
        using var scope = server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        foreach (var (id, signIn) in new[] { ("main-now", onMain), ("ada-now", onAda) })
        {
            db.NotificationDevices.Add(new Flexarr.Core.Notifications.NotificationDevice { Id = id, SignInId = signIn, ProfileId = ada, Token = id, Environment = "sandbox", Key = new byte[32] });
        }

        await db.SaveChangesAsync();
        var phones = await Flexarr.Api.Notifications.PairingAlerts.MainProfilePhonesAsync(db, signIns, CancellationToken.None);

        Assert.Equal(["main-now"], phones.Select(p => p.Id));
        Assert.NotNull(phone);
    }

    [Fact]
    public async Task TheMainProfile_CanReleaseALockedDevice_FromSettings()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        await CreateAsync(server, "Ada", 8, isKids: true);
        var tv = await TvAsync(server);
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = MainPin })).StatusCode);
        var id = (await Admin(server).GetFromJsonAsync<JsonElement>(Api("/sign-ins"))).EnumerateArray()
            .First(d => d.GetProperty("kidsLock").GetBoolean()).GetProperty("id").GetString();

        Assert.Equal(HttpStatusCode.Forbidden, (await tv.PostAsJsonAsync(Api($"/sign-ins/{id}/release-kids-lock"), new { })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin(server).PostAsJsonAsync(Api($"/sign-ins/{id}/release-kids-lock"), new { })).StatusCode);
        Assert.False((await tv.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("kidsLock").GetBoolean());
    }

    [Fact]
    public async Task AKidsHome_HasWatchAgain_RightAfterContinueWatching()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var items = await SeedAsync(server);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var sam = await CreateAsync(server, "Sam", 15);
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            foreach (var who in new[] { ada, sam })
            {
                db.PlaybackStates.Add(new PlaybackState { UserId = who, MediaItemId = items["episode"], Watched = true, LastPlayedAt = DateTime.UtcNow });
                db.PlaybackStates.Add(new PlaybackState { UserId = who, MediaItemId = items["paddington"], Watched = true, LastPlayedAt = DateTime.UtcNow.AddDays(-1) });
            }

            await db.SaveChangesAsync();
        }

        var tv = await TvAsync(server);
        await SelectAsync(tv, ada);
        var rows = (await tv.GetFromJsonAsync<JsonElement>(Api("/home"))).GetProperty("rows").EnumerateArray().ToList();
        await tv.PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { parentPin = MainPin });
        var teen = (await tv.GetFromJsonAsync<JsonElement>(Api("/home"))).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();

        var again = rows.FindIndex(r => r.GetProperty("id").GetString() == "again");
        Assert.Equal(rows.FindIndex(r => r.GetProperty("id").GetString() == "continue") + 1, again);
        Assert.Equal([items["bluey"], items["paddington"]], rows[again].GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain("again", teen); // only kids profiles
    }

    [Fact]
    public async Task LeavingAnAgeLimit_TakesNoPin_ButAProfilesOwnPinStillCounts()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var sam = await CreateAsync(server, "Sam", 12);
        var mum = await CreateAsync(server, "Mum");
        var tv = await TvAsync(server);
        await SelectAsync(tv, sam);

        var toMum = await SelectAsync(tv, mum);
        await SelectAsync(tv, sam);
        var dad = (await (await Admin(server).PostAsJsonAsync(Api("/profiles"), new { name = "Dad", pin = "1234" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        var wrongOwn = await SelectAsync(tv, dad, new { pin = "9999", parentPin = MainPin }); // an older app still sends parentPin
        var rightOwn = await SelectAsync(tv, dad, new { pin = "1234" });

        Assert.Equal(HttpStatusCode.OK, toMum.StatusCode);
        Assert.True((await JsonAsync(wrongOwn)).GetProperty("ownPin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, rightOwn.StatusCode);
    }

    [Fact]
    public async Task ALockedDevicesProfileGoing_MovesItToAnotherKidsProfile_OrIsRefused()
    {
        using var server = new FlexarrApiFactory();
        await server.SetMainPinAsync(MainPin);
        var ada = await CreateAsync(server, "Ada", 8, isKids: true);
        var ben = await CreateAsync(server, "Ben", 0, isKids: true);
        var tv = await TvAsync(server);
        await tv.PostAsJsonAsync(Api("/profiles/kids-lock"), new { pin = MainPin, profileId = ada });

        Assert.Equal(HttpStatusCode.NoContent, (await Admin(server).DeleteAsync(Api($"/profiles/{ada}"))).StatusCode);
        Assert.Equal(ben, (await tv.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());
        Assert.Equal(HttpStatusCode.Conflict, (await Admin(server).DeleteAsync(Api($"/profiles/{ben}"))).StatusCode); // the last one
        Assert.Equal(HttpStatusCode.Conflict, (await tv.DeleteAsync(Api("/session"))).StatusCode); // nor can it sign itself out
    }

    [Fact]
    public async Task AtHome_WithOnlyAwayFromHome_ABrowserWithNoProfileChosen_StartsOnAKidsProfile()
    {
        using var server = new RemoteAccessFactory();
        using (var scope = server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!.SetPin(MainPin);
            await db.SaveChangesAsync();
        }

        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var before = server.CreateClient();
        before.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.50");
        var mainBefore = (await before.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString();
        var ada = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Ada", maxRatingAge = 8, isKids = true })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();

        // A private window: no cookie, no profile token.
        var fresh = server.CreateClient();
        fresh.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.50");
        fresh.DefaultRequestHeaders.Add("X-Flexarr-Profile", "made-up.token");

        Assert.Equal("owner", mainBefore);
        Assert.Equal(ada, (await fresh.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("profileId").GetString());
    }
}
