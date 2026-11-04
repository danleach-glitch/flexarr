using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

/// <summary>The server's name: its default, renaming it, and where it's given out.</summary>
public sealed class ServerNameTests
{
    private const string Home = "192.168.1.20";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Theory]
    [InlineData("Smith House", "Dans-Mac-Mini", "Smith House")]
    [InlineData("  Smith House  ", "Dans-Mac-Mini", "Smith House")]
    [InlineData(null, "Dans-Mac-Mini", "Dans-Mac-Mini")]
    [InlineData("   ", "Dans-Mac-Mini", "Dans-Mac-Mini")]
    [InlineData(null, null, "Flexarr")]
    [InlineData(null, "", "Flexarr")]
    public void Resolve_PrefersTheGivenName_ThenTheMachines(string? set, string? machine, string expected) =>
        Assert.Equal(expected, ServerName.Resolve(set, machine));

    [Fact]
    public void Resolve_SkipsAMachineNameTooLongToAnnounce() =>
        Assert.Equal("Flexarr", ServerName.Resolve(null, new string('é', 40)));

    [Theory]
    [InlineData("Smith House", true)]
    [InlineData("", true)]
    [InlineData("Ünïcødé ✨ server", true)]
    [InlineData("Line\nbreak", false)]
    [InlineData("Tab\there", false)]
    public void Problem_RefusesControlCharacters(string name, bool ok) => Assert.Equal(ok, ServerName.Problem(name) is null);

    [Fact]
    public void Problem_RefusesNamesLongerThanFortyCharacters_OrSixtyThreeBytes()
    {
        Assert.Null(ServerName.Problem(new string('a', ServerName.MaxLength)));
        Assert.NotNull(ServerName.Problem(new string('a', ServerName.MaxLength + 1)));
        Assert.NotNull(ServerName.Problem(new string('✨', 30))); // 30 characters, 90 bytes.
    }

    [Theory]
    [InlineData("Smith House", new string[0], "Flexarr (Smith House)")]
    [InlineData("Flexarr", new string[0], "Flexarr")]
    [InlineData("Smith House", new[] { "flexarr (smith house)" }, "Flexarr (Smith House) 2")]
    [InlineData("Flexarr", new[] { "Flexarr", "Flexarr 2" }, "Flexarr 3")]
    public void ConnectionName_IsNamedAfterTheServer_AndNumberedWhenTaken(string server, string[] taken, string expected) =>
        Assert.Equal(expected, SetupGuideEndpoints.ConnectionName(server, taken));

    [Fact]
    public async Task Rename_ShowsEverywhere_AndEmptyGoesBackToTheDefault()
    {
        using var server = new FlexarrApiFactory();
        var client = Client(server);

        var before = await client.GetFromJsonAsync<SettingsDto>("/api/v1/settings", Json);
        Assert.Null(before!.ServerName);
        Assert.Equal(ServerName.Default, before.DefaultServerName);
        Assert.Equal(ServerName.Default, (await client.GetFromJsonAsync<SystemStatus>("/api/v1/system/status", Json))!.ServerName);

        var renamed = await client.PutAsJsonAsync("/api/v1/settings", new { serverName = "  Smith House " });
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Smith House", (await renamed.Content.ReadFromJsonAsync<SettingsDto>(Json))!.ServerName);
        Assert.Equal("Smith House", (await client.GetFromJsonAsync<SystemStatus>("/api/v1/system/status", Json))!.ServerName);
        Assert.Equal("Smith House", (await client.GetFromJsonAsync<JsonObject>("/api/v1/setup"))!["serverName"]!.GetValue<string>());
        Assert.Equal("Smith House", (await MediaBrowser(server).GetFromJsonAsync<JsonObject>("/System/Configuration"))!["ServerName"]!.GetValue<string>());

        // Other settings saved later leave the name alone.
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/settings", new { allowMusicRequests = true })).StatusCode);
        Assert.Equal("Smith House", (await client.GetFromJsonAsync<SettingsDto>("/api/v1/settings", Json))!.ServerName);

        var reset = await client.PutAsJsonAsync("/api/v1/settings", new { serverName = "" });
        Assert.Null((await reset.Content.ReadFromJsonAsync<SettingsDto>(Json))!.ServerName);
        Assert.Equal(ServerName.Default, (await client.GetFromJsonAsync<SystemStatus>("/api/v1/system/status", Json))!.ServerName);
    }

    [Fact]
    public async Task Rename_RefusesABadName_AndKeepsTheOldOne()
    {
        using var server = new FlexarrApiFactory();
        var client = Client(server);
        await client.PutAsJsonAsync("/api/v1/settings", new { serverName = "Smith House" });

        var refused = await client.PutAsJsonAsync("/api/v1/settings", new { serverName = new string('a', 41) });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("serverName", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal("Smith House", (await client.GetFromJsonAsync<SettingsDto>("/api/v1/settings", Json))!.ServerName);
    }

    [Fact]
    public async Task Setup_GivesTheNameAtHome_ButNotToTheInternet()
    {
        using var server = new FlexarrApiFactory();
        var anonymous = server.CreateClient();

        Assert.Equal(ServerName.Default, (await SetupFrom(Home))!["serverName"]!.GetValue<string>());
        Assert.Null((await SetupFrom("203.0.113.9"))!["serverName"]);

        async Task<JsonObject?> SetupFrom(string peer)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/setup");
            request.Headers.Add("X-Test-Peer", peer);
            return await (await anonymous.SendAsync(request)).Content.ReadFromJsonAsync<JsonObject>();
        }
    }

    private static HttpClient Client(FlexarrApiFactory server)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        client.DefaultRequestHeaders.Add("X-Test-Peer", Home);
        return client;
    }

    private static HttpClient MediaBrowser(FlexarrApiFactory server)
    {
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-MediaBrowser-Token", FlexarrApiFactory.ApiKey);
        return client;
    }
}
