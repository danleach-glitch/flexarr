using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// A main profile with a PIN of its own: a device or browser that never gave it, from a client that says it can ask
/// who's watching (X-Flexarr-Profile-Choice), is answered as no profile chosen. Clients that don't say are unchanged.
/// </summary>
public class ProfileChoiceTests
{
    private const string AdminPin = "2580";
    private const string OwnPin = "1357";
    private const string ChoiceHeader = "X-Flexarr-Profile-Choice";

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task<string> SetUpAsync(IServiceProvider services, bool ownPin = true)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = (await db.Profiles.FindAsync(PlaybackState.DefaultUser))!;
        main.SetAdminPin(AdminPin);
        main.SetPin(ownPin ? OwnPin : null);
        var other = new Profile { Id = Profile.NewId(), Name = "Sam" };
        db.Profiles.Add(other);
        await db.SaveChangesAsync();
        return other.Id;
    }

    /// <summary>A TV paired with a code the main profile approved: its own token, nothing chosen on it yet.</summary>
    private static async Task<string> PairAsync(FlexarrApiFactory server)
    {
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.70");
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Living room" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsJsonAsync(Api($"/pairing/code/{started.GetProperty("code").GetString()}/approve"), new { })).StatusCode);
        var handedOver = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{started.GetProperty("id").GetString()}"));
        return handedOver.GetProperty("apiKey").GetString()!;
    }

    private static HttpClient Device(FlexarrApiFactory server, string token, bool asks)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", token);
        if (asks)
        {
            client.DefaultRequestHeaders.Add(ChoiceHeader, "1");
        }

        return client;
    }

    private static async Task<JsonElement> MeAsync(HttpClient client) => await client.GetFromJsonAsync<JsonElement>(Api("/me"));

    [Fact]
    public async Task ANewlyPairedTv_ThatCanAsk_IsAnsweredAsNoProfileChosen_UntilTheMainProfilesOwnPinIsGiven()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var tv = Device(server, await PairAsync(server), asks: true);

        var me = await MeAsync(tv);
        var libraries = await tv.GetAsync(Api("/libraries"));
        var profiles = await tv.GetAsync(Api("/profiles"));
        var wrongPin = await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "0000" });

        Assert.True(me.GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, libraries.StatusCode);
        Assert.True((await libraries.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, profiles.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongPin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await tv.GetAsync(Api("/libraries"))).StatusCode);

        // Its own PIN chooses it, and that's kept on the TV's sign-in.
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = OwnPin })).StatusCode);
        var after = await MeAsync(tv);
        Assert.False(after.GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(PlaybackState.DefaultUser, after.GetProperty("profileId").GetString());
        Assert.False(after.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task ANewlyPairedTv_ThatCanAsk_ChoosingAnotherProfile_IsThatProfile()
    {
        using var server = new FlexarrApiFactory();
        var sam = await SetUpAsync(server.Services);
        var tv = Device(server, await PairAsync(server), asks: true);

        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { })).StatusCode);
        var me = await MeAsync(tv);

        Assert.Equal(sam, me.GetProperty("profileId").GetString());
        Assert.False(me.GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task AnAppThatDoesntSayItCanAsk_CarriesOnAsTheMainProfile_AsBefore()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var tv = Device(server, await PairAsync(server), asks: false);

        var me = await MeAsync(tv);

        Assert.Equal(PlaybackState.DefaultUser, me.GetProperty("profileId").GetString());
        Assert.False(me.GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task WithoutAPinOfItsOwn_TheMainProfileIsChosenAsBefore()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services, ownPin: false);
        var tv = Device(server, await PairAsync(server), asks: true);

        Assert.False((await MeAsync(tv)).GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task ADeviceSignedInWithTheApiKey_IsTheOwners_AndIsntAsked()
    {
        using var server = new FlexarrApiFactory();
        await SetUpAsync(server.Services);
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        var tv = Device(server, token, asks: true);

        Assert.False((await MeAsync(tv)).GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await tv.GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task AtHome_ABrowserLetInByTheHomeNetwork_IsAskedWhosWatching_AndItsOwnPinSignsItIn()
    {
        using var server = new RemoteAccessFactory();
        await SetUpAsync(server.Services);
        HttpClient Browser(bool asks = true)
        {
            var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.60");
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
            if (asks)
            {
                client.DefaultRequestHeaders.Add(ChoiceHeader, "1");
            }

            return client;
        }

        var browser = Browser();
        var me = await MeAsync(browser);
        var libraries = await browser.GetAsync(Api("/libraries"));
        var chosen = await browser.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = OwnPin });

        Assert.True(me.GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, libraries.StatusCode);
        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        // Not asked again on this browser: the PIN gave it a sign-in of its own, which unlocks nothing.
        var cookie = chosen.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("flexarr.session=", StringComparison.Ordinal)).Split(';')[0];
        var signedIn = Browser();
        signedIn.DefaultRequestHeaders.Add("Cookie", cookie);
        var after = await MeAsync(signedIn);
        Assert.Equal(PlaybackState.DefaultUser, after.GetProperty("profileId").GetString());
        Assert.False(after.GetProperty("chooseProfile").GetBoolean());
        Assert.False(after.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await signedIn.GetAsync(Api("/libraries"))).StatusCode);
        // Another browser at home is asked in its turn; one that doesn't say it can ask is the main profile, as before.
        Assert.True((await MeAsync(Browser())).GetProperty("chooseProfile").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await Browser(asks: false).GetAsync(Api("/libraries"))).StatusCode);
    }

    [Fact]
    public async Task AtHome_SwitchingToTheMainProfileFromAnother_WithItsOwnPin_SignsTheBrowserIn_SoItIsntAskedAgain()
    {
        using var server = new RemoteAccessFactory();
        var sam = await SetUpAsync(server.Services);
        HttpClient Browser(string? profileToken = null, string? cookie = null)
        {
            var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.61");
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
            client.DefaultRequestHeaders.Add(ChoiceHeader, "1");
            if (profileToken is not null)
            {
                client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
            }

            if (cookie is not null)
            {
                client.DefaultRequestHeaders.Add("Cookie", cookie);
            }

            return client;
        }

        var asSam = (await (await Browser().PostAsJsonAsync(Api($"/profiles/{sam}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString()!;
        Assert.False((await MeAsync(Browser(asSam))).GetProperty("chooseProfile").GetBoolean());

        var chosen = await Browser(asSam).PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = OwnPin });

        Assert.Equal(HttpStatusCode.OK, chosen.StatusCode);
        var cookie = chosen.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("flexarr.session=", StringComparison.Ordinal)).Split(';')[0];
        var after = await MeAsync(Browser(cookie: cookie));
        Assert.Equal(PlaybackState.DefaultUser, after.GetProperty("profileId").GetString());
        Assert.False(after.GetProperty("chooseProfile").GetBoolean());
        Assert.False(after.GetProperty("isAdmin").GetBoolean());
    }
}
