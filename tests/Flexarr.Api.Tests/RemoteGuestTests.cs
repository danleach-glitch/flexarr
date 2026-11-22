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
using Microsoft.EntityFrameworkCore;

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
    public const string HomeUrl = "http://192.168.1.2:8383/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("hostBuilder:reloadConfigOnChange", "false");
        builder.UseSetting("Flexarr:ApiKey", FlexarrApiFactory.ApiKey);
        builder.UseSetting("Flexarr:DataDirectory", DataDirectory);
        builder.UseSetting("Flexarr:Logs:ErrorReportsDsn", "");
        builder.UseSetting("Flexarr:RemoteGuests:Enabled", "true");
        // The test server listens nowhere, so it can't work out its own.
        builder.UseSetting("Flexarr:HomeUrl", HomeUrl);
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
        Assert.Null(payload.M); // Unnamed: the machine's name isn't for guests.
        Assert.Equal(factory.Clock.GetUtcNow().UtcDateTime.AddDays(3), invite.Invite.ExpiresAt, TimeSpan.FromDays(1));
        using var admin = factory.Admin();
        var waiting = await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web);
        Assert.Contains(waiting!, w => w.Id == invite.Invite.Id && w.ProfileName == "Gran");
        // Only a hash of the token is kept on the server.
        Assert.DoesNotContain(payload.T, await File.ReadAllTextAsync(Path.Combine(factory.DataDirectory, GuestInvites.FileName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_invite_carries_the_servers_name_once_an_admin_has_given_it_one()
    {
        var gran = await factory.AddProfileAsync("Gran named");
        using var admin = factory.Admin();
        Assert.True((await admin.PutAsJsonAsync("/api/v1/settings", new { serverName = "Smith House" })).IsSuccessStatusCode);
        try
        {
            var (_, payload) = await InviteAsync(gran, "Gran's TV");
            Assert.Equal("Smith House", payload.M);
        }
        finally
        {
            await admin.PutAsJsonAsync("/api/v1/settings", new { serverName = "" });
        }
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

        // The guest's sign-in works, as its profile.
        using (var guest = factory.From(RemoteGuestFactory.Guest, body.ApiKey))
        {
            var me = await guest.GetFromJsonAsync<JsonElement>("/api/v1/me");
            Assert.Equal(gran, me.GetProperty("profileId").GetString());
        }

        // Used: another guest device can't redeem it. Its own key was deleted once used.
        factory.Whois.Tags["100.64.0.43"] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync("100.64.0.43", payload.T)).StatusCode);
        await Task.Delay(200);
        Assert.Contains(payload.K["tskey-auth-".Length..^"-secret".Length], factory.Tailscale.DeletedKeys);

        // The same device asking again (its answer was lost) gets a fresh sign-in that replaces the first.
        var again = await RedeemAsync(RemoteGuestFactory.Guest, payload.T);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        var second = (await again.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!;
        using var first = factory.From(RemoteGuestFactory.Guest, body.ApiKey);
        Assert.NotEqual(HttpStatusCode.OK, (await first.GetAsync("/api/v1/me")).StatusCode);
        using var retried = factory.From(RemoteGuestFactory.Guest, second.ApiKey);
        Assert.Equal(HttpStatusCode.OK, (await retried.GetAsync("/api/v1/me")).StatusCode);

        // Once the owner removes the device, asking again doesn't bring it back.
        using var admin = factory.Admin();
        var devices = await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/remote/guests/devices");
        var id = devices!.Single(d => d.GetProperty("profileId").GetString() == gran).GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/remote/guests/devices/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync(RemoteGuestFactory.Guest, payload.T)).StatusCode);
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
    public async Task A_guest_doesnt_see_or_change_the_households_recordings()
    {
        var (_, key) = await JoinAsync("Gran DVR");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);

        var list = await guest.GetFromJsonAsync<JsonElement>("/api/v1/livetv/recordings");
        Assert.Empty(list.GetProperty("upcoming").EnumerateArray());
        Assert.Empty(list.GetProperty("recent").EnumerateArray());
        // Refused before the recording is even looked up.
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.DeleteAsync($"/api/v1/livetv/recordings/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.DeleteAsync($"/api/v1/livetv/recordings/series/{Guid.NewGuid()}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsJsonAsync($"/api/v1/livetv/recordings/series/{Guid.NewGuid()}", new { newOnly = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync("/api/v1/livetv/recordings", new { programmeId = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync("/api/v1/livetv/recordings/series", new { programmeId = 1 })).StatusCode);
    }

    [Fact]
    public async Task A_guest_cant_find_or_resync_subtitles_for_the_household()
    {
        var (_, key) = await JoinAsync("Gran subtitles");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);

        // To a guest, as to a kids profile, Bazarr isn't set up, so the apps don't offer it.
        Assert.False((await guest.GetFromJsonAsync<JsonElement>("/api/v1/subtitles/status")).GetProperty("configured").GetBoolean());
        // Refused before the title is even looked up.
        var item = Guid.NewGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync($"/api/v1/items/{item}/subtitles/search")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/v1/items/{item}/subtitles/download", new { language = "en" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/v1/items/{item}/subtitles/sync", new { key = "x" })).StatusCode);
    }

    [Fact]
    public async Task A_guest_assistant_doesnt_see_or_set_the_households_recordings()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Recordings.Add(new Flexarr.Core.LiveTv.Recording
            {
                ChannelId = Guid.NewGuid(), ChannelName = "BBC One", Title = "The household's quiz night",
                Start = DateTime.UtcNow.AddHours(1), End = DateTime.UtcNow.AddHours(2), State = Flexarr.Core.LiveTv.RecordingState.Scheduled,
            });
            await db.SaveChangesAsync();
        }

        async Task<ModelContextProtocol.Protocol.CallToolResult> CallAsync(HttpClient http, string apiKey, string tool, Dictionary<string, object?> arguments)
        {
            var options = new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"),
                TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
            };
            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                new ModelContextProtocol.Client.HttpClientTransport(options, http, ownsHttpClient: false));
            return await client.CallToolAsync(tool, arguments);
        }

        static string Text(ModelContextProtocol.Protocol.CallToolResult result) =>
            string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));

        using (var admin = factory.Admin())
        {
            Assert.Contains("quiz night", Text(await CallAsync(admin, FlexarrApiFactory.ApiKey, "recordings", [])), StringComparison.Ordinal);
        }

        var (_, key) = await JoinAsync("Gran assistant DVR");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);
        var listed = await CallAsync(guest, key, "recordings", []);
        Assert.NotEqual(true, listed.IsError);
        Assert.DoesNotContain("quiz night", Text(listed), StringComparison.Ordinal);
        // Refused before the programme is even looked up, as the API's route is.
        Assert.True((await CallAsync(guest, key, "record_programme", new() { ["programmeId"] = 1L, ["series"] = true, ["anyChannel"] = true })).IsError);
    }

    [Fact]
    public async Task A_guest_cant_move_the_sound_of_a_title_for_the_household()
    {
        var (_, key) = await JoinAsync("Gran sync");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);

        // Refused before the title is even looked up.
        var response = await guest.PutAsJsonAsync($"/api/v1/items/{Guid.NewGuid()}/audio-delay", new { seconds = 1.5 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task A_household_device_away_from_home_joins_its_own_profile_with_no_guest_limits()
    {
        var claire = await factory.AddProfileAsync("Claire away");
        var other = await factory.AddProfileAsync("Someone at home");
        using var admin = factory.Admin();

        // Never the main profile, or a guest's.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = PlaybackState.DefaultUser, household = true })).StatusCode);
        var (guestProfile, _) = await JoinAsync("Gran away");
        Assert.Equal(HttpStatusCode.BadRequest, (await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = guestProfile, household = true })).StatusCode);

        var made = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = claire, household = true });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var created = (await made.Content.ReadFromJsonAsync<CreatedInviteDto>(Web))!;
        Assert.True(created.Invite.Household);
        // Waiting, it doesn't make Claire a guest.
        Assert.DoesNotContain(claire, (await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people"))!);

        var redeemed = await RedeemAsync(RemoteGuestFactory.Guest, GuestInvites.Decode(created.Link[GuestInvites.LinkBase.Length..])!.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var answer = (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!;
        Assert.True(answer.Household);
        Assert.Equal(claire, answer.ProfileId);
        Assert.DoesNotContain(claire, (await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people"))!);

        using (var away = factory.From(RemoteGuestFactory.Guest, answer.ApiKey))
        {
            var me = await away.GetFromJsonAsync<JsonElement>("/api/v1/me");
            Assert.False(me.GetProperty("isGuest").GetBoolean());
            Assert.False(me.GetProperty("isAdmin").GetBoolean());
            Assert.False(me.GetProperty("canUnlock").GetBoolean());
            Assert.Equal(claire, me.GetProperty("profileId").GetString());
            // It sees the household and can switch to another of its profiles, but never the main one (no PIN tried).
            Assert.Contains(other, (await away.GetFromJsonAsync<List<JsonElement>>("/api/v1/profiles"))!.Select(p => p.GetProperty("id").GetString()));
            Assert.Equal(HttpStatusCode.Forbidden, (await away.PostAsJsonAsync($"/api/v1/profiles/{PlaybackState.DefaultUser}/select", new { pin = "1234" })).StatusCode);
            Assert.True((await away.PostAsJsonAsync($"/api/v1/profiles/{other}/select", new { })).IsSuccessStatusCode);
            Assert.True((await away.PostAsJsonAsync($"/api/v1/profiles/{claire}/select", new { })).IsSuccessStatusCode);
            // The household's devices, like at home.
            Assert.Equal(HttpStatusCode.OK, (await away.GetAsync("/api/v1/devices")).StatusCode);
        }

        // Its token is tied to the device that joined, like a guest's.
        using (var elsewhere = factory.From(RemoteGuestFactory.Owner, answer.ApiKey))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await elsewhere.GetAsync("/api/v1/me")).StatusCode);
        }

        // Listed, marked household; removing it signs it out and leaves Claire's profile alone.
        var devices = (await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!;
        var device = Assert.Single(devices, d => d.ProfileId == claire);
        Assert.True(device.Household);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/remote/guests/devices/{device.Id}")).StatusCode);
        Assert.Contains(claire, (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/profiles"))!.Select(p => p.GetProperty("id").GetString()));
        using (var removed = factory.From(RemoteGuestFactory.Guest, answer.ApiKey))
        {
            Assert.NotEqual(HttpStatusCode.OK, (await removed.GetAsync("/api/v1/me")).StatusCode);
        }
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
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PostAsJsonAsync($"/api/v1/profiles/{PlaybackState.DefaultUser}/forgot-pin", new { adminPin = "1234" })).StatusCode);
    }

    [Fact]
    public async Task Removing_a_guests_profile_in_profiles_signs_their_devices_out_and_cancels_their_invites()
    {
        var gran = await factory.AddProfileAsync("Gran41");
        var (_, joinedPayload) = await InviteAsync(gran, "Removed with profile");
        const string device = "100.64.0.41";
        factory.Whois.Tags[device] = ["tag:flexarr-guest"];
        var key = (await (await RedeemAsync(device, joinedPayload.T)).Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!.ApiKey;
        var (_, waitingPayload) = await InviteAsync(gran, "Still waiting");

        using var admin = factory.Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/profiles/{gran}")).StatusCode);

        // The app is told it was removed (not a plain 401), and the waiting invite can't be used.
        using var removed = factory.From(device, key);
        var refused = await removed.GetAsync("/api/v1/me");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("guestRemoved").GetBoolean());
        factory.Whois.Tags["100.64.0.42"] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync("100.64.0.42", waitingPayload.T)).StatusCode);
        // The fake's keys are "tskey-auth-<id>-secret"; deleted in the background.
        await Task.Delay(300);
        Assert.Contains(waitingPayload.K["tskey-auth-".Length..^"-secret".Length], factory.Tailscale.DeletedKeys);
    }

    [Fact]
    public async Task A_device_that_joined_before_the_servers_address_changed_says_so()
    {
        var gran = await factory.AddProfileAsync("Gran45");
        var (_, payload) = await InviteAsync(gran, "Moved test");
        const string device = "100.64.0.45";
        factory.Whois.Tags[device] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(device, payload.T)).StatusCode);
        using var admin = factory.Admin();

        async Task<bool> Moved() => (await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/remote/guests/devices"))!
            .Single(d => d.GetProperty("name").GetString() == "Moved test").GetProperty("serverMoved").GetBoolean();

        Assert.False(await Moved());
        var before = factory.Tailnet.Snapshot;
        factory.Tailnet.Snapshot = before! with { Address = "100.64.0.99" };
        try
        {
            Assert.True(await Moved());
        }
        finally
        {
            factory.Tailnet.Snapshot = before;
        }
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
    public async Task A_guest_assistant_sees_nothing_of_whats_playing_in_the_household()
    {
        Guid film;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Flexarr.Core.Libraries.Library { Name = $"Films {Guid.NewGuid():N}", Type = Flexarr.Core.Libraries.LibraryType.Movies };
            var movie = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Something private", SortTitle = "something private" };
            db.AddRange(library, movie);
            await db.SaveChangesAsync();
            film = movie.Id;
        }

        factory.Services.GetRequiredService<Flexarr.Api.Playback.PlaybackSessions>().Add(new Flexarr.Api.Playback.PlaybackSession
        {
            Id = Guid.NewGuid().ToString("N"),
            ItemId = film,
            ProfileId = PlaybackState.DefaultUser,
            FileId = Guid.NewGuid(),
            FilePath = "",
            Container = null,
            DurationSeconds = 600,
            Method = Flexarr.Streaming.PlaybackMethod.DirectPlay,
            Subtitles = [],
            CreditsStart = null,
        });

        async Task<string> NowPlayingAsync(HttpClient http, string apiKey)
        {
            var options = new ModelContextProtocol.Client.HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"),
                TransportMode = ModelContextProtocol.Client.HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey },
            };
            await using var client = await ModelContextProtocol.Client.McpClient.CreateAsync(
                new ModelContextProtocol.Client.HttpClientTransport(options, http, ownsHttpClient: false));
            var result = await client.CallToolAsync("now_playing", new Dictionary<string, object?>());
            Assert.NotEqual(true, result.IsError);
            return string.Concat(result.Content.OfType<ModelContextProtocol.Protocol.TextContentBlock>().Select(t => t.Text));
        }

        using (var admin = factory.Admin())
        {
            Assert.Contains("Something private", await NowPlayingAsync(admin, FlexarrApiFactory.ApiKey), StringComparison.Ordinal);
        }

        var (_, key) = await JoinAsync("Gran10");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);
        Assert.DoesNotContain("Something private", await NowPlayingAsync(guest, key), StringComparison.Ordinal);
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
    public async Task A_device_tailscale_cant_identify_just_now_waits_if_a_guest_ever_joined_from_it()
    {
        // A household device: carries on (pairing still waits, as it could be a guest that never joined).
        const string household = "100.64.0.31";
        factory.Whois.Unknown[household] = true;
        using (var device = factory.From(household))
        {
            Assert.Equal(HttpStatusCode.OK, (await device.GetAsync("/api/v1/setup")).StatusCode);
            Assert.NotEqual(HttpStatusCode.OK, (await device.PostAsJsonAsync("/api/v1/pairing", new { deviceName = "Living Room TV" })).StatusCode);
        }

        factory.Whois.Unknown.TryRemove(household, out _);

        // A device a guest joined from, now removed: while Tailscale can't say, it waits rather than being let in.
        var gran = await factory.AddProfileAsync("Gran46");
        var (_, payload) = await InviteAsync(gran, "Unknown test");
        const string guest = "100.64.0.46";
        factory.Whois.Tags[guest] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.OK, (await RedeemAsync(guest, payload.T)).StatusCode);
        using var admin = factory.Admin();
        var devices = await admin.GetFromJsonAsync<List<JsonElement>>("/api/v1/remote/guests/devices");
        var id = devices!.Single(d => d.GetProperty("name").GetString() == "Unknown test").GetProperty("id").GetString();
        await admin.DeleteAsync($"/api/v1/remote/guests/devices/{id}");
        factory.Whois.Unknown[guest] = true;
        try
        {
            using var removed = factory.From(guest);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await removed.GetAsync("/api/v1/setup")).StatusCode);
        }
        finally
        {
            factory.Whois.Unknown.TryRemove(guest, out _);
        }
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

    // The same vectors as relay/test/pair.test.js and docs/remote-guests.md, so the TV apps can match them byte for byte.
    [Theory]
    [InlineData("BAabxf2q9hMu/dxG+nGp5nYWsRDth260mBqx6U/8TU9miOPz86o/f4xuMSzrZ/YVxS1EHcdBY/JxFPKujcWATRA=", "P5GSPMQP")]
    [InlineData("BJeO6XNfhIDBbIdQxtPpYlj3Cjk7nlUzjg+xJFmk20ChvzfI1jdWs2T5CgsWu1MmBW4iXHxrMfK9/MTDb9Nb1mM=", "VFAZC453")]
    [InlineData("BBikfwYmLGipShyfrqxMWA/kv+NWBDrgrZotgMESONlNQfZ0um1pjfF8J+1iarUDMOzmd5+F9czYFE5xxV+pIvo=", "7EAR565J")]
    public void A_tv_key_makes_its_code(string publicKey, string code)
    {
        Assert.Equal(code, GuestTvCodes.CodeFor(publicKey));
        Assert.True(GuestTvCodes.IsKeyCode(code));
    }

    [Fact]
    public async Task A_code_made_from_a_key_takes_only_that_key_but_older_tvs_random_codes_still_work()
    {
        var gran = await factory.AddProfileAsync("Gran45");
        using var admin = factory.Admin();
        var before = (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Count;
        const string tvKey = "BAabxf2q9hMu/dxG+nGp5nYWsRDth260mBqx6U/8TU9miOPz86o/f4xuMSzrZ/YVxS1EHcdBY/JxFPKujcWATRA=";
        const string someoneElsesKey = "BJeO6XNfhIDBbIdQxtPpYlj3Cjk7nlUzjg+xJFmk20ChvzfI1jdWs2T5CgsWu1MmBW4iXHxrMfK9/MTDb9Nb1mM=";

        // A relay answering the TV's code with another key: nothing is sealed to it.
        factory.PairRelay.Keys["P5GSPMQP"] = someoneElsesKey;
        var swapped = await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "P5GS-PMQP", name = "x", profileId = gran });
        Assert.Equal(HttpStatusCode.BadGateway, swapped.StatusCode);
        Assert.False(factory.PairRelay.Invites.ContainsKey("P5GSPMQP"));
        Assert.Equal(before, (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Count);

        // The TV's own key.
        factory.PairRelay.Keys["P5GSPMQP"] = tvKey;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "P5GS-PMQP", name = "x", profileId = gran })).StatusCode);

        // An older TV's random code (no check character) takes whatever key the relay has, as before.
        Assert.False(GuestTvCodes.IsKeyCode("JKMNPQRS"));
        factory.PairRelay.Keys["JKMNPQRS"] = someoneElsesKey;
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "JKMN-PQRS", name = "y", profileId = gran })).StatusCode);
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
    public async Task A_key_off_the_curve_makes_no_invite_and_a_relay_timeout_keeps_the_one_it_may_have_delivered()
    {
        var gran = await factory.AddProfileAsync("Gran44");
        using var admin = factory.Admin();
        var before = (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Count;

        // A point that isn't on P-256 (x = y = 1): refused before any invite is made.
        var offCurve = new byte[65];
        offCurve[0] = 0x04;
        offCurve[32] = 1;
        offCurve[64] = 1;
        factory.PairRelay.Keys["BADKEY22"] = Convert.ToBase64String(offCurve);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "BADK-EY22", name = "x", profileId = gran })).StatusCode);

        using var tv = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        factory.PairRelay.Keys["SPEEDRAY"] = Convert.ToBase64String(GuestTvCodes.Uncompressed(tv.ExportParameters(false)));
        factory.PairRelay.DeliveryTimesOut = true;
        try
        {
            Assert.Equal(HttpStatusCode.BadGateway, (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "SPEE-DRAY", name = "x", profileId = gran })).StatusCode);
        }
        finally
        {
            factory.PairRelay.DeliveryTimesOut = false;
        }

        // A timeout may still have reached the TV, so that invite is kept (and goes by itself after an hour).
        var kept = (await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!;
        Assert.Equal(before + 1, kept.Count);
        await admin.DeleteAsync($"/api/v1/remote/guests/invites/{kept.Single(i => i.ProfileId == gran).Id}");
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
            Assert.Contains("didn’t accept", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            factory.Tailscale.AcceptClient = true;
            // A client that can do more than make keys and read the policy is refused.
            // Tailscale adds the devices' read scopes to every client that reads the policy, so those pass.
            factory.Tailscale.Scope = "auth_keys policy_file:read devices:core:read devices:posture_attributes:read";
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client", clientSecret = "secret" })).StatusCode);
            factory.Tailscale.Scope = "auth_keys policy_file devices:core";
            var tooMuch = await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client", clientSecret = "secret" });
            Assert.Equal(HttpStatusCode.BadRequest, tooMuch.StatusCode);
            Assert.Contains("policy_file, devices:core", await tooMuch.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            // A token that doesn't say: a client that can list devices is still refused, and isn't kept.
            factory.Tailscale.Scope = "";
            factory.Tailscale.WideWithoutScope = true;
            Assert.Equal(HttpStatusCode.BadRequest, (await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "wide", clientSecret = "secret" })).StatusCode);
            Assert.Equal("client", (await admin.GetFromJsonAsync<JsonElement>("/api/v1/remote/guests")).GetProperty("clientId").GetString());
            factory.Tailscale.WideWithoutScope = false;
            Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync("/api/v1/remote/guests/client", new { clientId = "client", clientSecret = "secret" })).StatusCode);
        }
        finally
        {
            factory.Tailscale.Scope = "auth_keys policy_file:read";
            factory.Tailscale.WideWithoutScope = false;
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

    /// <summary>Adds a household device away from home for <paramref name="profileId"/> from tailnet address <paramref name="address"/>.</summary>
    private async Task<RedeemedInviteDto> JoinHouseholdAsync(string profileId, string address)
    {
        factory.Whois.Tags[address] = ["tag:flexarr-guest"];
        using var admin = factory.Admin();
        var made = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId, household = true });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var created = (await made.Content.ReadFromJsonAsync<CreatedInviteDto>(Web))!;
        var redeemed = await RedeemAsync(address, GuestInvites.Decode(created.Link[GuestInvites.LinkBase.Length..])!.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        return (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!;
    }

    [Fact]
    public async Task A_household_device_also_signs_in_from_the_home_network_but_nowhere_else()
    {
        var claire = await factory.AddProfileAsync("Claire70");
        const string address = "100.64.0.70";
        var answer = await JoinHouseholdAsync(claire, address);

        // Home: the same key works, so the device can switch between its tunnel and the home address.
        using var home = factory.From("192.168.1.70", answer.ApiKey);
        Assert.Equal(claire, (await home.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("profileId").GetString());

        // Another tailnet address, or the internet: still refused.
        using var otherTailnet = factory.From("100.64.0.71", answer.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await otherTailnet.GetAsync("/api/v1/me")).StatusCode);
        using var internet = factory.From("203.0.113.70", answer.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await internet.GetAsync("/api/v1/me")).StatusCode);

        // Relayed from the internet to this machine: refused, however local it looks.
        using var relayed = factory.From("127.0.0.1", answer.ApiKey);
        relayed.DefaultRequestHeaders.Add("CF-Connecting-IP", "203.0.113.71");
        Assert.Equal(HttpStatusCode.Unauthorized, (await relayed.GetAsync("/api/v1/me")).StatusCode);

        // This machine itself isn't the home network.
        using var loopback = factory.From("127.0.0.1", answer.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await loopback.GetAsync("/api/v1/me")).StatusCode);

        // The owner sees which way it came last.
        using var admin = factory.Admin();
        async Task<string?> Route() => (await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!.Single(d => d.Address == address).LastRoute;
        Assert.Equal("home", await Route());
        using var tunnel = factory.From(address, answer.ApiKey);
        Assert.True((await tunnel.GetAsync("/api/v1/me")).IsSuccessStatusCode);
        Assert.Equal("away", await Route());
    }

    [Fact]
    public async Task Only_a_household_device_is_told_the_home_address()
    {
        var claire = await factory.AddProfileAsync("Claire73");
        var answer = await JoinHouseholdAsync(claire, "100.64.0.73");
        using var away = factory.From("100.64.0.73", answer.ApiKey);
        var me = await away.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(RemoteGuestFactory.HomeUrl, me.GetProperty("homeUrl").GetString());
        Assert.Equal(RemoteGuestFactory.HomeUrl, answer.HomeUrl?.ToString());

        var (_, key) = await JoinAsync("Gran73");
        using var guest = factory.From(RemoteGuestFactory.Guest, key);
        var guestMe = await guest.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.True(!guestMe.TryGetProperty("homeUrl", out var g) || g.ValueKind == JsonValueKind.Null);
    }

    [Theory]
    [InlineData("192.168.1.10", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.20.1.1", true)]
    [InlineData("172.32.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("127.0.0.1", false)]
    [InlineData("169.254.1.1", false)]
    [InlineData("8.8.8.8", false)]
    public void The_home_address_is_a_private_one(string address, bool home) =>
        Assert.Equal(home, HomeAddress.IsHome(System.Net.IPAddress.Parse(address)));

    [Theory]
    [InlineData("en0", false)]
    [InlineData("eth0", false)]
    [InlineData("wlan0", false)]
    [InlineData("enp3s0", false)]
    [InlineData("utun4", true)]
    [InlineData("wg0", true)]
    [InlineData("tailscale0", true)]
    [InlineData("docker0", true)]
    [InlineData("br-1a2b3c", true)]
    [InlineData("bridge100", true)]
    [InlineData("vmnet8", true)]
    [InlineData("Hyper-V Virtual Ethernet Adapter", true)]
    [InlineData("NordVPN", true)]
    public void A_vpn_or_virtual_machines_adapter_comes_last_for_the_home_address(string name, bool skipped) =>
        Assert.Equal(skipped, HomeAddress.Virtual(name));

    [Fact]
    public async Task The_home_address_proves_itself_to_a_household_device_before_it_sends_its_key()
    {
        var claire = await factory.AddProfileAsync("Claire74");
        var answer = await JoinHouseholdAsync(claire, "100.64.0.74");
        const string challenge = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";
        var hash = ServerProof.HashOf(answer.ApiKey);

        // Asked anonymously from home, as the apps do before trusting the address.
        using var home = factory.From("192.168.1.74");
        var setup = await home.GetFromJsonAsync<JsonElement>($"/api/v1/setup?challenge={challenge}&keyId={ServerProof.KeyId(hash)}");

        Assert.Equal(answer.ServerId, setup.GetProperty("serverId").GetString());
        Assert.Equal(ServerProof.Answer(hash, answer.ServerId, challenge), setup.GetProperty("proof").GetString());
    }

    [Fact]
    public async Task A_household_device_isnt_let_in_from_home_where_the_server_gives_no_home_address()
    {
        var claire = await factory.AddProfileAsync("Claire75");
        var answer = await JoinHouseholdAsync(claire, "100.64.0.75");
        Assert.Equal(RemoteGuestFactory.HomeUrl, answer.HomeUrl?.ToString());

        // As in Docker without Flexarr:HomeUrl, where the host's port forwarding could make anyone look local (here an
        // address that isn't one, which also gives none).
        using var unknown = factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:HomeUrl", "not an address"));
        using var home = unknown.CreateClient();
        home.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.75");
        home.DefaultRequestHeaders.Add("X-Api-Key", answer.ApiKey);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.GetAsync("/api/v1/me")).StatusCode);
    }

    [Fact]
    public async Task A_guests_key_never_works_from_the_home_network()
    {
        var (_, key) = await JoinAsync("Gran70");
        using var home = factory.From("192.168.1.72", key);
        Assert.Equal(HttpStatusCode.Unauthorized, (await home.GetAsync("/api/v1/me")).StatusCode);
    }

    private async Task<string> AddKidsProfileAsync(string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var profile = db.Profiles.Add(new Profile { Id = Profile.NewId(), Name = name, IsKids = true, MaxRatingAge = 12 }).Entity;
        await db.SaveChangesAsync();
        return profile.Id;
    }

    [Fact]
    public async Task A_household_device_goes_back_to_its_own_profile_when_the_one_it_chose_goes_and_with_its_own()
    {
        var claire = await factory.AddProfileAsync("Claire51");
        var other = await factory.AddProfileAsync("Other51");
        const string address = "100.64.0.51";
        var answer = await JoinHouseholdAsync(claire, address);
        using var admin = factory.Admin();
        using var away = factory.From(address, answer.ApiKey);
        Assert.True((await away.PostAsJsonAsync($"/api/v1/profiles/{other}/select", new { })).IsSuccessStatusCode);
        Assert.Equal(other, (await away.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("profileId").GetString());

        // The profile it switched to goes: it's back on Claire's, still signed in.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/profiles/{other}")).StatusCode);
        Assert.Equal(claire, (await away.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("profileId").GetString());
        var device = Assert.Single((await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!, d => d.Address == address);
        Assert.Equal(claire, device.HomeProfileId);

        // Claire's goes: so does her device.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync($"/api/v1/profiles/{claire}")).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await away.GetAsync("/api/v1/me")).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<SignIns>().List().FirstOrDefault(s => s.GuestAddress == address));
    }

    [Fact]
    public async Task A_household_device_whose_profiles_both_went_while_it_was_away_is_signed_out_never_the_main_profile()
    {
        var claire = await factory.AddProfileAsync("Claire52");
        const string address = "100.64.0.52";
        var answer = await JoinHouseholdAsync(claire, address);

        // Gone behind the server's back (no ForgetProfileAsync): the sign-in is ended on its next request.
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.Profiles.Where(p => p.Id == claire).ExecuteDeleteAsync();
        }

        using var away = factory.From(address, answer.ApiKey);
        Assert.NotEqual(HttpStatusCode.OK, (await away.GetAsync("/api/v1/me")).StatusCode);
        Assert.Null(factory.Services.GetRequiredService<SignIns>().List().FirstOrDefault(s => s.GuestAddress == address));
    }

    [Fact]
    public async Task A_household_device_never_unlocks_the_server_or_takes_the_main_profile_with_the_admin_pin()
    {
        var claire = await factory.AddProfileAsync("Claire53");
        const string address = "100.64.0.53";
        var answer = await JoinHouseholdAsync(claire, address);
        using var away = factory.From(address, answer.ApiKey);

        Assert.Equal(HttpStatusCode.Forbidden, (await away.PostAsJsonAsync("/api/v1/admin/unlock", new { pin = "1234" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await away.PostAsJsonAsync("/api/v1/admin/unlock", new { apiKey = FlexarrApiFactory.ApiKey })).StatusCode);

        // Knowing the API key, it may set the admin PIN; the device stays on Claire's profile, locked.
        Assert.Equal(HttpStatusCode.OK, (await away.PostAsJsonAsync("/api/v1/admin/pin", new { pin = "2468", apiKey = FlexarrApiFactory.ApiKey })).StatusCode);
        var me = await away.GetFromJsonAsync<JsonElement>("/api/v1/me");
        Assert.Equal(claire, me.GetProperty("profileId").GetString());
        Assert.False(me.GetProperty("isAdmin").GetBoolean());
        Assert.Equal(claire, factory.Services.GetRequiredService<SignIns>().List().First(s => s.GuestAddress == address).ProfileId);
        Assert.Equal(HttpStatusCode.Forbidden, (await away.GetAsync("/api/v1/remote/guests/invites")).StatusCode);
    }

    [Fact]
    public async Task A_household_device_joins_with_a_tv_code_too()
    {
        var claire = await factory.AddProfileAsync("Claire54");
        using var tv = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        factory.PairRelay.Keys["HSHKDTV5"] = Convert.ToBase64String(GuestTvCodes.Uncompressed(tv.ExportParameters(false)));
        using var admin = factory.Admin();

        var response = await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "HSHKDTV5", profileId = claire, household = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True((await response.Content.ReadFromJsonAsync<GuestInviteDto>(Web))!.Household);
        var left = factory.PairRelay.Invites["HSHKDTV5"];
        var opened = GuestTvCodes.Open(tv, Convert.FromBase64String(left["serverKey"]!.GetValue<string>()), Convert.FromBase64String(left["sealed"]!.GetValue<string>()));
        var payload = GuestInvites.Decode(System.Text.Encoding.UTF8.GetString(opened))!;
        const string address = "100.64.0.54";
        factory.Whois.Tags[address] = ["tag:flexarr-guest"];
        var redeemed = await RedeemAsync(address, payload.T);
        Assert.Equal(HttpStatusCode.OK, redeemed.StatusCode);
        var answer = (await redeemed.Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!;
        Assert.True(answer.Household);
        Assert.Equal(claire, answer.ProfileId);
        Assert.DoesNotContain(claire, (await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people"))!);

        // Used once: another device can't use it again.
        factory.Whois.Tags["100.64.0.55"] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync("100.64.0.55", payload.T)).StatusCode);
    }

    [Fact]
    public async Task A_kids_profiles_device_away_from_home_has_child_lock_on()
    {
        var kid = await AddKidsProfileAsync("Kid56");
        var grownUp = await factory.AddProfileAsync("Grown-up56");
        const string address = "100.64.0.56";
        var answer = await JoinHouseholdAsync(kid, address);

        var signIn = factory.Services.GetRequiredService<SignIns>().List().First(s => s.GuestAddress == address);
        Assert.True(signIn.KidsLock);
        using var away = factory.From(address, answer.ApiKey);
        var offered = (await away.GetFromJsonAsync<List<JsonElement>>("/api/v1/profiles"))!.Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains(kid, offered);
        Assert.DoesNotContain(grownUp, offered);
        var refused = await away.PostAsJsonAsync($"/api/v1/profiles/{grownUp}/select", new { });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.True((await refused.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("kidsLock").GetBoolean());
        Assert.Equal(kid, (await away.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task Releasing_child_lock_on_a_household_device_sends_its_notifications_to_its_own_profile()
    {
        var kid = await AddKidsProfileAsync("Kid57");
        const string address = "100.64.0.57";
        await JoinHouseholdAsync(kid, address);
        var signIn = factory.Services.GetRequiredService<SignIns>().List().First(s => s.GuestAddress == address);

        Assert.Equal(kid, Flexarr.Api.Profiles.ProfileEndpoints.ReleasedNotificationProfile(signIn));
        using var admin = factory.Admin();
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/sign-ins/{signIn.Id}/release-kids-lock", null)).StatusCode);
        Assert.False(signIn.KidsLock);
        Assert.Equal(kid, Flexarr.Api.Profiles.ProfileEndpoints.ReleasedNotificationProfile(signIn));
    }

    [Fact]
    public async Task A_household_device_never_offers_the_main_profile_or_a_guests()
    {
        var claire = await factory.AddProfileAsync("Claire58");
        var (gran, _) = await JoinAsync("Gran58");
        const string address = "100.64.0.58";
        var answer = await JoinHouseholdAsync(claire, address);
        using var away = factory.From(address, answer.ApiKey);

        var offered = (await away.GetFromJsonAsync<List<JsonElement>>("/api/v1/profiles"))!.Select(p => p.GetProperty("id").GetString()).ToList();
        Assert.Contains(claire, offered);
        Assert.DoesNotContain(PlaybackState.DefaultUser, offered);
        Assert.DoesNotContain(gran, offered);
        Assert.Equal(HttpStatusCode.Forbidden, (await away.PostAsJsonAsync($"/api/v1/profiles/{gran}/select", new { })).StatusCode);
        Assert.Equal(claire, (await away.GetFromJsonAsync<JsonElement>("/api/v1/me")).GetProperty("profileId").GetString());
    }

    [Fact]
    public async Task Someone_in_the_household_cant_be_invited_as_a_guest()
    {
        var claire = await factory.AddProfileAsync("Claire59");
        var waiting = await factory.AddProfileAsync("Waiting59");
        await JoinHouseholdAsync(claire, "100.64.0.59");
        using var admin = factory.Admin();
        Assert.True((await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = waiting, household = true })).IsSuccessStatusCode);

        foreach (var profileId in new[] { claire, waiting })
        {
            var refused = await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("is in your household", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadRequest,
                (await admin.PostAsJsonAsync("/api/v1/remote/guests/tv-code", new { code = "ABCDEFGH", profileId })).StatusCode);
        }
    }

    [Fact]
    public async Task An_invite_whose_profile_changed_sides_while_it_waited_cant_be_used()
    {
        var invites = factory.Services.GetRequiredService<GuestInvites>();
        using var admin = factory.Admin();

        // A household invite whose profile became a guest's.
        var dana = await factory.AddProfileAsync("Dana60");
        var household = (await (await admin.PostAsJsonAsync("/api/v1/remote/guests/invites", new { profileId = dana, household = true }))
            .Content.ReadFromJsonAsync<CreatedInviteDto>(Web))!;
        await invites.CreateAsync(null, dana, "Dana60", "Owner", InviteVia.Link, CancellationToken.None, household: false);
        factory.Whois.Tags["100.64.0.60"] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync("100.64.0.60", GuestInvites.Decode(household.Link[GuestInvites.LinkBase.Length..])!.T)).StatusCode);

        // A guest's invite whose profile now has a household device.
        var eli = await factory.AddProfileAsync("Eli61");
        await JoinHouseholdAsync(eli, "100.64.0.61");
        var (_, payload) = await invites.CreateAsync(null, eli, "Eli61", "Owner", InviteVia.Link, CancellationToken.None, household: false);
        factory.Whois.Tags["100.64.0.62"] = ["tag:flexarr-guest"];
        Assert.Equal(HttpStatusCode.Gone, (await RedeemAsync("100.64.0.62", payload.T)).StatusCode);
        Assert.DoesNotContain(factory.Services.GetRequiredService<SignIns>().List(), s => s.GuestAddress == "100.64.0.62");
    }

    [Fact]
    public async Task A_guest_who_lives_in_the_house_moves_to_the_household_keeping_their_profile_and_devices()
    {
        var finn = await factory.AddProfileAsync("Finn63");
        var (_, payload) = await InviteAsync(finn, "Finn's iPad");
        const string address = "100.64.0.63";
        factory.Whois.Tags[address] = ["tag:flexarr-guest"];
        var key = (await (await RedeemAsync(address, payload.T)).Content.ReadFromJsonAsync<RedeemedInviteDto>(Web))!.ApiKey;
        await InviteAsync(finn, "Finn's phone"); // still waiting
        using var admin = factory.Admin();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync($"/api/v1/remote/guests/people/{finn}/move-to-household", null)).StatusCode);

        Assert.DoesNotContain(finn, (await admin.GetFromJsonAsync<List<string>>("/api/v1/remote/guests/people"))!);
        var device = Assert.Single((await admin.GetFromJsonAsync<List<GuestDeviceDto>>("/api/v1/remote/guests/devices", Web))!, d => d.Address == address);
        Assert.True(device.Household);
        Assert.Equal(finn, device.HomeProfileId);
        Assert.All((await admin.GetFromJsonAsync<List<GuestInviteDto>>("/api/v1/remote/guests/invites", Web))!.Where(i => i.ProfileId == finn), i => Assert.True(i.Household));
        using (var moved = factory.From(address, key))
        {
            var me = await moved.GetFromJsonAsync<JsonElement>("/api/v1/me");
            Assert.False(me.GetProperty("isGuest").GetBoolean());
            Assert.Equal(finn, me.GetProperty("profileId").GetString());
        }

        // Only a guest moves; never the main profile.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/remote/guests/people/{finn}/move-to-household", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync($"/api/v1/remote/guests/people/{PlaybackState.DefaultUser}/move-to-household", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsync("/api/v1/remote/guests/people/nobody/move-to-household", null)).StatusCode);
        using var notAdmin = factory.From(address, key);
        Assert.Equal(HttpStatusCode.Forbidden, (await notAdmin.PostAsync($"/api/v1/remote/guests/people/{finn}/move-to-household", null)).StatusCode);
    }
}
