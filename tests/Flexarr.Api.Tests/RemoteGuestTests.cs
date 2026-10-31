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
}
