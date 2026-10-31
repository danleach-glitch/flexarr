using Flexarr.Api.Authentication;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Flexarr.Api.Tests;

/// <summary>A server with family invites on, a fake Tailscale API and a fake tailnet.</summary>
public sealed class RemoteGuestFactory : WebApplicationFactory<Program>
{
    public string DataDirectory { get; } = Directory.CreateTempSubdirectory("flexarr-guests-").FullName;
    public FakeWhois Whois { get; } = new();
    public FakeTailscale Tailscale { get; } = new();
    public FakeTailnet Tailnet { get; } = new();
    public FakePairRelay PairRelay { get; } = new();
    public TestClock Clock { get; } = new(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    public const string Guest = "100.64.0.9";
    public const string Owner = "100.64.0.2";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.UseSetting("Flexarr:ApiKey", FlexarrApiFactory.ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:Logs:ErrorReportsDsn", "");
        builder.UseSetting("Flexarr:RemoteGuests:Enabled", "true");
        builder.UseSetting("Flexarr:RemoteGuests:ClientId", "client");
        builder.UseSetting("Flexarr:RemoteGuests:ClientSecret", "secret");
        builder.UseSetting("Flexarr:RemoteGuests:ApiUrl", "https://tailscale.test");
        builder.ConfigureServices(services => services.RemoveAll<IHostedService>());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, PeerFromHeader>();
            services.AddSingleton<ITailnetWhois>(Whois);
            services.AddSingleton<ITailnet>(Tailnet);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddHttpClient(TailscaleApi.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => Tailscale);
            services.AddHttpClient(GuestTvCodes.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => PairRelay);
        });
        Whois.Tags[Guest] = ["tag:flexarr-guest"];
        Whois.Owners[Owner] = "dan@example.com";
    }

    public async Task<string> AddProfileAsync(string name)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = name }).Entity;
        await db.SaveChangesAsync();
        return profile.Id;
    }

    public HttpClient Admin()
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A client whose requests come straight from <paramref name="address"/> on the tailnet.</summary>
    public HttpClient From(string address, string? apiKey = null)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-Peer", address);
        if (apiKey is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        }

        return client;
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

public class RemoteGuestTests(RemoteGuestFactory factory) : IClassFixture<RemoteGuestFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private async Task<(CreatedInviteDto Invite, InvitePayload Payload)> InviteAsync(string profileId, string name = "Gran's iPad")
    {
        using var admin = factory.Admin();
        var response = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { name, profileId });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<CreatedInviteDto>(Web))!;
        Assert.StartsWith(GuestInvites.LinkBase, created.Link, StringComparison.Ordinal);
        return (created, GuestInvites.Decode(created.Link[GuestInvites.LinkBase.Length..])!);
    }

    private async Task<HttpResponseMessage> RedeemAsync(string address, string token)
    {
        using var client = factory.From(address);
        return await client.PostAsJsonAsync("/api/v1/invites/redeem", new { token });
    }

    [Fact]
    public async Task An_invite_carries_a_one_use_key_this_servers_tailnet_address_and_the_profile()
    {
        var gran = await factory.AddProfileAsync("Gran");

        var (invite, payload) = await InviteAsync(gran);

        Assert.Equal(1, payload.V);
        Assert.StartsWith("tskey-auth-", payload.K, StringComparison.Ordinal);
        Assert.Equal("100.64.0.1:8383", payload.A);
        Assert.Equal("Gran", payload.P);
        Assert.Equal("Gran's iPad", payload.G);
        Assert.False(string.IsNullOrEmpty(payload.S));
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddDays(3), invite.Invite.ExpiresAt, TimeSpan.FromDays(1));
        using var admin = factory.Admin();
        var waiting = await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web);
        Assert.Contains(waiting!, w => w.Id == invite.Invite.Id && w.ProfileName == "Gran");
        // Only a hash of the token is kept on the server.
        Assert.DoesNotContain(payload.T, await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, GuestInvites.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guests_never_get_the_main_profile()
    {
        using var admin = factory.Admin();
        var response = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { name = "x", profileId = PlaybackState.DefaultUser });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Only_an_admin_makes_invites()
    {
        var gran = await factory.AddProfileAsync("Gran2");
        using var guest = factory.From(RemoteGuestFactory.Owner);
        var response = await guest.PostAsJsonAsync("/api/v1/remote/guests/invites", new { name = "x", profileId = gran });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task An_invite_works_once_and_only_from_a_guest_device()
    {
        var gran = await factory.AddProfileAsync("Gran3");
        var (_, payload) = await InviteAsync(gran);

        Assert.Equal(HttpStatusCode.Forbidden, (await RedeemAsync("192.168.1.50", payload.T)).StatusCode); // the home network
        Assert.Equal(HttpStatusCode.Forbidden, (await RedeemAsync(RemoteGuestFactory.Owner, payload.T)).StatusCode); // a person's device

        var redeemed = await RedeemAsync(RemoteGuestFactory.Guest, payload.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var body = (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!;
        Assert.Equal(gran, body.ProfileId);

        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync(RemoteGuestFactory.Guest, payload.T)).StatusCode);

        // The guest's sign-in works, as its profile.
        using var guest = factory.From(RemoteGuestFactory.Guest, body.ApiKey);
        var me = await guest.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(gran, me.GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task Cancelling_deletes_the_tailscale_key()
    {
        var gran = await factory.AddProfileAsync("Gran4");
        var (invite, payload) = await InviteAsync(gran);
        using var admin = factory.Admin();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/remote/guests/invites/{invite.Invite.Id}")).StatusCode);

        Assert.Contains(payload.K.Split('-')[2], factory.Tailscale.DeletedKeys);
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync(RemoteGuestFactory.Guest, payload.T)).StatusCode);
    }

    [Fact]
    public async Task An_unsafe_policy_stops_new_invites()
    {
        var gran = await factory.AddProfileAsync("Gran5");
        factory.Tailscale.Validate = _ => """{"message":"test(s) failed","data":[{"user":"tag:flexarr-guest","errors":["address \"100.64.0.2:22\": want: Drop, got: Accept"]}]}""";
        factory.Services.GetRequiredService<GuestPolicy>().Forget();
        try
        {
            using var admin = factory.Admin();
            var response = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { name = "x", profileId = gran });
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Contains("autogroup:member", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            factory.Tailscale.Validate = _ => "{}";
            factory.Services.GetRequiredService<GuestPolicy>().Forget();
        }
    }

    [Fact]
    public async Task Invites_expire_after_three_days()
    {
        var gran = await factory.AddProfileAsync("Gran6");
        var (_, payload) = await InviteAsync(gran);

        factory.Clock.Advance(GuestInvites.Lifetime + TimeSpan.FromMinutes(1));
        try
        {
            factory.Services.GetRequiredService<GuestPolicy>().Forget();
            Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync(RemoteGuestFactory.Guest, payload.T)).StatusCode);
        }
        finally
        {
            factory.Services.GetRequiredService<GuestPolicy>().Forget();
        }
    }

    /// <summary>Invites a guest to a new profile and redeems it from the guest device; returns the profile and the device's key.</summary>
    private async Task<(string Profile, string ApiKey)> JoinAsync(string name)
    {
        var profile = await factory.AddProfileAsync(name);
        var (_, payload) = await InviteAsync(profile, $"{name}'s iPad");
        var redeemed = await RedeemAsync(RemoteGuestFactory.Guest, payload.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return (profile, (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!.ApiKey);
    }

    [Fact]
    public async Task A_guest_stays_on_its_profile_and_never_manages_the_server()
    {
        var (gran, key) = await JoinAsync("Gran7");
        var other = await factory.AddProfileAsync("Someone else");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);

        var me = await guest.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.False(me.GetProperty("canUnlock").GetBoolean());

        var profiles = await guest.GetFromJsonAsync<List<JsonElement>>("/api/v1/profiles");
        Assert.Equal([gran], profiles!.Select(p => p.GetProperty("id").GetString()));

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/v1/profiles/{other}/select", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/v1/profiles/{PlaybackState.DefaultUser}/select", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync("/api/v1/profiles/kids-lock", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/remote/guests/invites")).StatusCode);
    }

    [Fact]
    public async Task A_guest_sign_in_is_kept_where_an_older_server_never_finds_it()
    {
        var gran = await factory.AddProfileAsync("Gran40");
        var (_, payload) = await InviteAsync(gran, "Rollback test");
        const string device = "100.64.0.40";
        factory.Whois.Tags[device] = ["tag:flexarr-guest"];
        var key = (await (await RedeemAsync(device, payload.T)).Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!.ApiKey;

        // Older servers look a token up by its plain hash; a guest's is stored under a prefix they never ask for.
        var plain = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key)));
        var file = await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, SignIns.FileName));
        Assert.Contains($"\"{SignIns.GuestHashPrefix}{plain}\"", file, StringComparison.Ordinal);
        Assert.DoesNotContain($"\"{plain}\"", file, StringComparison.Ordinal);
        using var joined = factory.From(device, key);
        Assert.Equal(HttpStatusCode.OK, (await joined.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_guest_token_only_works_from_its_own_device()
    {
        var (_, key) = await JoinAsync("Gran8");

        using var elsewhere = factory.From("192.168.1.50", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/me")).StatusCode);
        using var otherTailnet = factory.From(RemoteGuestFactory.Owner, key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherTailnet.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task Guests_and_the_household_never_see_each_others_devices()
    {
        var (_, key) = await JoinAsync("Gran9");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);
        Assert.Empty((await guest.GetFromJsonAsync<List<JsonElement>>("/api/v1/devices"))!);

        using var admin = factory.Admin();
        var devices = await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/devices");
        Assert.DoesNotContain(devices!, d => d.GetProperty("name").GetString() == "Gran9's iPad");
    }

    [Fact]
    public async Task A_guest_device_cant_ask_for_a_pairing_code()
    {
        using var guest = factory.From(RemoteGuestFactory.Guest);
        var response = await guest.PostAsJsonAsync("/api/v1/pairing", new { deviceName = "Sneaky TV" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Tagged_devices_cant_be_linked_to_a_profile()
    {
        var gran = await factory.AddProfileAsync("Gran10");
        using var admin = factory.Admin();
        var response = await admin.PostAsJsonAsync("/api/v1/remote/tailscale/links", new { login = "tagged-devices", profileId = gran });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_guest_device_without_a_sign_in_reaches_nothing_but_redeem_and_removing_it_shuts_it_out()
    {
        const string stranger = "100.64.0.20";
        factory.Whois.Tags[stranger] = ["tag:flexarr-guest"];
        using (var unjoined = factory.From(stranger))
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await unjoined.GetAsync("/api/v1/setup")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await unjoined.GetAsync("/")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await unjoined.GetAsync("/health")).StatusCode);
        }

        var gran = await factory.AddProfileAsync("Gran11");
        var (_, payload) = await InviteAsync(gran, "Gate test");
        var redeemed = await RedeemAsync(stranger, payload.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var key = (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!.ApiKey;
        using (var joined = factory.From(stranger, key))
        {
            Assert.Equal(HttpStatusCode.OK, (await joined.GetAsync("/api/v1/me")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await joined.GetAsync("/api/v1/setup")).StatusCode);
        }

        using var admin = factory.Admin();
        var signIns = await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/sign-ins");
        var id = signIns!.Single(s => s.GetProperty("name").GetString() == "Gate test").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/sign-ins/{id}")).StatusCode);

        using var removed = factory.From(stranger, key);
        var refused = await removed.GetAsync("/api/v1/setup");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        // The apps tell this refusal from any other, and sign out.
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("guestRemoved").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, (await removed.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_device_tailscale_cant_identify_just_now_waits_and_cant_ask_for_a_pairing_code()
    {
        const string unidentified = "100.64.0.31";
        factory.Whois.Unknown[unidentified] = true;
        using var device = factory.From(unidentified);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await device.GetAsync("/api/v1/setup")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/pairing", new { deviceName = "Living Room TV" })).StatusCode);
        factory.Whois.Unknown.TryRemove(unidentified, out _);
        Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/setup")).StatusCode);
    }

    [Fact]
    public async Task The_gate_leaves_the_households_own_tailnet_devices_alone()
    {
        using var owner = factory.From(RemoteGuestFactory.Owner);
        Assert.Equal(HttpStatusCode.OK, (await owner.GetAsync("/api/v1/setup")).StatusCode);
    }

    [Fact]
    public async Task A_tv_code_gets_an_invite_only_that_tv_can_open()
    {
        var gran = await factory.AddProfileAsync("Gran12");
        using var tv = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        factory.PairRelay.Keys["ABCDEFGH"] = Convert.ToBase64String(GuestTvCodes.Uncompressed(tv.ExportParameters(false)));
        using var admin = factory.Admin();

        var response = await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "abcd-efgh", name = "Gran's TV", profileId = gran });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var left = factory.PairRelay.Invites["ABCDEFGH"];
        var opened = GuestTvCodes.Open(tv, Convert.FromBase64String(left["serverKey"]!.GetValue<string>()), Convert.FromBase64String(left["sealed"]!.GetValue<string>()));
        var payload = GuestInvites.Decode(System.Text.Encoding.UTF8.GetString(opened))!;
        Assert.Equal("Gran12", payload.P);
        Assert.Equal("Gran's TV", payload.G);
        Assert.StartsWith("tskey-auth-", payload.K, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(RemoteGuestFactory.Guest, payload.T)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_or_malformed_tv_code_says_so_and_makes_no_invite()
    {
        var gran = await factory.AddProfileAsync("Gran13");
        using var admin = factory.Admin();
        var before = (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Count;

        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "ZZZZ-ZZZZ", name = "x", profileId = gran })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "O0O0", name = "x", profileId = gran })).StatusCode);
        Assert.Equal(before, (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Count);
    }

    [Fact]
    public async Task Each_guest_device_shows_how_it_reaches_the_server()
    {
        var (_, _) = await JoinAsync("Gran14"); // redeemed from RemoteGuestFactory.Guest, which the fake tailnet has on Tailscale's relay
        using var admin = factory.Admin();

        var devices = await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web);

        var gran = devices!.Single(d => d.Name == "Gran14's iPad");
        Assert.True(gran.Online);
        Assert.Equal(TailnetPath.Derp, gran.Path);
        Assert.Equal("Gran14", gran.ProfileName);
        Assert.Equal(RemoteGuestFactory.Guest, gran.Address);
    }

    [Fact]
    public async Task The_faster_relay_turns_on_and_off_on_the_servers_tailscale()
    {
        using var admin = factory.Admin();

        var on = await admin.PutAsJsonAsync("/api/v1/remote/guests/relay", new { enabled = true });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        var relay = (await on.Content.ReadFromJsonAsync<RelayDto>(Web))!;
        Assert.Equal(RemoteGuestEndpoints.DefaultRelayPort, relay.Port);
        Assert.True(relay.Working);
        Assert.Equal(RemoteGuestEndpoints.DefaultRelayPort, factory.Tailnet.RelayPorts[^1]);

        var off = await admin.PutAsJsonAsync("/api/v1/remote/guests/relay", new { enabled = false });
        Assert.Null((await off.Content.ReadFromJsonAsync<RelayDto>(Web))!.Port);
        Assert.Null(factory.Tailnet.RelayPorts[^1]);

        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/remote/guests/relay", new { enabled = true, port = 80 })).StatusCode);
    }

    [Fact]
    public async Task Setup_shows_the_snippet_with_this_servers_address_and_never_the_secret()
    {
        using var admin = factory.Admin();
        await admin.PostAsync("/api/v1/remote/guests/check", null);

        var response = await admin.GetAsync("/api/v1/remote/guests");
        var text = await response.Content.ReadAsStringAsync();
        var setup = JsonSerializer.Deserialize<RemoteGuestsDto>(text, Web)!;

        Assert.True(setup.Enabled);
        Assert.True(setup.HasClient);
        Assert.Equal("client", setup.ClientId);
        Assert.Equal("100.64.0.1", setup.Address);
        Assert.Contains("\"flexarr\": \"100.64.0.1\"", setup.Snippet, StringComparison.Ordinal);
        Assert.True(setup.Check!.Safe);
        Assert.DoesNotContain("secret", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_the_client_checks_it_and_keeps_the_secret_when_left_out()
    {
        using var admin = factory.Admin();
        try
        {
            // Same id, no secret: the saved secret stays, and Tailscale accepts it.
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client" })).StatusCode);
            // A new id needs its secret.
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "other" })).StatusCode);
            // A client Tailscale refuses says so.
            factory.Tailscale.AcceptClient = false;
            var refused = await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client", clientSecret = "wrong" });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("didn't accept", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        finally
        {
            factory.Tailscale.AcceptClient = true;
            await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client", clientSecret = "secret" });
        }
    }

    [Fact]
    public async Task Removing_a_guests_device_shuts_it_out_and_only_guests_can_be_removed_here()
    {
        var (_, key) = await JoinAsync("Gran15");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);
        Assert.True((await guest.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("isGuest").GetBoolean());

        using var admin = factory.Admin();
        var device = (await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!.Single(d => d.Name == "Gran15's iPad");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/remote/guests/devices/{device.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync("/api/v1/me")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/v1/remote/guests/devices/not-a-guest")).StatusCode);
    }

    [Fact]
    public async Task A_device_invited_without_a_name_is_named_from_its_profile_and_what_it_is()
    {
        var aunt = await factory.AddProfileAsync("Aunt Sue");
        using var admin = factory.Admin();
        var created = await (await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = aunt })).Content.ReadFromJsonAsync<CreatedInviteDto>(Web);
        var payload = GuestInvites.Decode(created!.Link[GuestInvites.LinkBase.Length..])!;
        Assert.Equal("Aunt Sue", payload.G); // the tailnet name comes from the profile

        using var guest = factory.From(RemoteGuestFactory.Guest);
        guest.DefaultRequestHeaders.Add("X-Flexarr-Device", "IPad");
        var redeemed = await (await guest.PostAsJsonAsync("/api/v1/invites/redeem", new { token = payload.T })).Content.ReadFromJsonAsync<RedeemedInviteDto>(Web);

        Assert.Equal("Aunt Sue’s iPad", redeemed!.Name);
    }

    [Fact]
    public async Task A_guest_stays_a_guest_after_their_last_device_is_removed()
    {
        var (dad, _) = await JoinAsync("Dad16");
        using var admin = factory.Admin();
        foreach (var d in (await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!.Where(d => d.ProfileId == dad))
        {
            await admin.DeleteAsync($"/api/v1/remote/guests/devices/{d.Id}");
        }

        var people = await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people");
        Assert.Contains(dad, people!);

        // Removed as a profile: no longer a guest.
        await admin.DeleteAsync($"/api/v1/profiles/{dad}");
        Assert.DoesNotContain(dad, (await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people"))!);
    }
}
