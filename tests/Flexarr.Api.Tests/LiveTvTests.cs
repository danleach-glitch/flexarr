using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flexarr.Api.LiveTv;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for Dispatcharr's Xtream Codes API: a channel list, a guide and logos, for one user.</summary>
public sealed class FakeDispatcharr : HttpMessageHandler
{
    public const string Root = "http://dispatcharr:9191";
    public const string User = "flexarr";
    public const string Password = "xc-s3cret";

    public List<(int Id, int Number, string Name, string Group, string? Logo)> Channels { get; } =
    [
        (1, 101, "BBC One", "UK News", $"{Root}/api/channels/logos/1/cache/"),
        (2, 102, "BBC Two", "UK General", "https://elsewhere.example/logo.png"),
        (3, 201, "Sky News", "UK News", null),
    ];

    public string Guide { get; set; } = "";

    public ConcurrentQueue<Uri> Requests { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        Requests.Enqueue(url);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        if (url.AbsolutePath is "/player_api.php" or "/xmltv.php" && (query["username"] != User || query["password"] != Password))
        {
            return Task.FromResult(Json("""{"user_info":{"auth":0}}"""));
        }

        return Task.FromResult(url.AbsolutePath switch
        {
            "/player_api.php" => query["action"] switch
            {
                null => Json("""{"user_info":{"auth":1,"status":"Active","max_connections":"1","exp_date":"1800000000"}}"""),
                "get_live_categories" => Json("""[{"category_id":"6","category_name":"UK News"},{"category_id":"7","category_name":"UK General"}]"""),
                "get_live_streams" => Json(JsonSerializer.Serialize(Channels.Select(c => new Dictionary<string, object?>
                {
                    ["num"] = c.Number,
                    ["name"] = c.Name,
                    ["stream_id"] = c.Id,
                    ["stream_icon"] = c.Logo,
                    ["epg_channel_id"] = c.Number.ToString(CultureInfo.InvariantCulture),
                    ["category_id"] = c.Group == "UK News" ? "6" : "7",
                }))),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            },
            "/xmltv.php" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Guide, Encoding.UTF8, "application/xml") },
            "/api/channels/logos/1/cache/" => Image(),
            _ => url.Host == "elsewhere.example" ? Image() : new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Image()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 1, 2, 3]) };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        return response;
    }
}

public sealed class LiveTvTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeDispatcharr _dispatcharr = new();
    private readonly WebApplicationFactory<Program> _server;

    public LiveTvTests()
    {
        _server = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddHttpClient<XtreamClient>().ConfigurePrimaryHttpMessageHandler(() => _dispatcharr)));

        // On BBC One: a programme now and the next; on Sky News, one that ended long ago (dropped).
        var now = DateTime.UtcNow;
        _dispatcharr.Guide = $"""
            <?xml version="1.0" encoding="UTF-8"?><!DOCTYPE tv SYSTEM "xmltv.dtd"><tv>
            <programme start="{Xmltv(now.AddMinutes(-20))}" stop="{Xmltv(now.AddMinutes(10))}" channel="101"><title>The News</title><category>News</category><category>Current affairs</category></programme>
            <programme start="{Xmltv(now.AddMinutes(10))}" stop="{Xmltv(now.AddMinutes(70))}" channel="101"><title>Doctor Who</title><sub-title>The Star Beast</sub-title><new/></programme>
            <programme start="{Xmltv(now.AddDays(-2))}" stop="{Xmltv(now.AddDays(-2).AddHours(1))}" channel="201"><title>Old news</title></programme>
            </tv>
            """;
    }

    public void Dispose()
    {
        _server.Dispose();
        _factory.Dispose();
        _dispatcharr.Dispose();
    }

    private static string Xmltv(DateTime t) => t.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";

    private HttpClient Client(string? profile = null)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profile is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profile);
        }

        return client;
    }

    private static Uri Api(string path) => new($"/api/v1/{path}", UriKind.Relative);

    private async Task ConnectAsync(HttpClient client)
    {
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = FakeDispatcharr.Password } }))
            .EnsureSuccessStatusCode();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        // Background workers don't run in tests; refresh as the guide worker would.
        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().RefreshAsync(CancellationToken.None);
    }

    private static async Task<JsonElement[]> ChannelsAsync(HttpClient client) => [.. (await client.GetFromJsonAsync<JsonElement>(Api("livetv/channels"))).EnumerateArray()];

    [Fact]
    public async Task The_password_is_kept_but_never_shown_and_only_for_the_same_server()
    {
        var client = Client();
        await ConnectAsync(client);

        var settings = await client.GetFromJsonAsync<JsonElement>(Api("settings"));
        var live = settings.GetProperty("liveTv");
        Assert.True(live.GetProperty("hasPassword").GetBoolean());
        Assert.DoesNotContain(FakeDispatcharr.Password, settings.GetRawText(), StringComparison.Ordinal);

        // Saved again without a password: the saved one stays.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, rewindMinutes = 30 } }))
            .EnsureSuccessStatusCode();
        live = (await client.GetFromJsonAsync<JsonElement>(Api("settings"))).GetProperty("liveTv");
        Assert.True(live.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(30, live.GetProperty("rewindMinutes").GetInt32());

        // Pointed somewhere else without one: it isn't sent there.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = "http://another:9191", username = FakeDispatcharr.User } })).EnsureSuccessStatusCode();
        live = (await client.GetFromJsonAsync<JsonElement>(Api("settings"))).GetProperty("liveTv");
        Assert.False(live.GetProperty("hasPassword").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Testing_says_whether_the_sign_in_works()
    {
        var client = Client();
        var good = await (await client.PostAsJsonAsync(Api("settings/livetv/test"),
            new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = FakeDispatcharr.Password })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(good.GetProperty("ok").GetBoolean());
        Assert.Equal(3, good.GetProperty("channels").GetInt32());
        Assert.Equal(1, good.GetProperty("maxConnections").GetInt32());

        var bad = await (await client.PostAsJsonAsync(Api("settings/livetv/test"),
            new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = "wrong" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(bad.GetProperty("ok").GetBoolean());
        Assert.Contains("refused", bad.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Channels_carry_what_is_on_now_and_next_and_follow_the_source()
    {
        var client = Client();
        await ConnectAsync(client);

        var channels = await ChannelsAsync(client);
        Assert.Equal(["BBC One", "BBC Two", "Sky News"], channels.Select(c => c.GetProperty("name").GetString()));
        var bbc = channels[0];
        Assert.Equal("UK News", bbc.GetProperty("group").GetString());
        Assert.Equal("The News", bbc.GetProperty("now").GetProperty("title").GetString());
        Assert.Equal("News", bbc.GetProperty("now").GetProperty("category").GetString());
        Assert.EndsWith("Z", bbc.GetProperty("now").GetProperty("start").GetString(), StringComparison.Ordinal); // UTC, so browsers place it right
        Assert.Equal("Doctor Who", bbc.GetProperty("next").GetProperty("title").GetString());
        Assert.True(bbc.GetProperty("next").GetProperty("isNew").GetBoolean());
        Assert.Equal(JsonValueKind.Null, channels[2].GetProperty("now").ValueKind); // its only programme ended days ago

        // Dropped from Dispatcharr: gone here too, its guide with it.
        _dispatcharr.Channels.RemoveAt(0);
        await RefreshAsync();
        Assert.Equal(["BBC Two", "Sky News"], (await ChannelsAsync(client)).Select(c => c.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Favourites_belong_to_each_profile()
    {
        var client = Client();
        await ConnectAsync(client);
        var sky = (await ChannelsAsync(client)).Single(c => c.GetProperty("name").GetString() == "Sky News").GetProperty("id").GetString();

        (await client.PutAsync(Api($"livetv/channels/{sky}/favourite"), null)).EnsureSuccessStatusCode();
        (await client.PutAsync(Api($"livetv/channels/{sky}/favourite"), null)).EnsureSuccessStatusCode(); // twice is fine
        Assert.Equal(["Sky News"], (await ChannelsAsync(client)).Where(c => c.GetProperty("isFavourite").GetBoolean()).Select(c => c.GetProperty("name").GetString()));
        var guide = await client.GetFromJsonAsync<JsonElement>(Api("livetv/guide"));
        Assert.True(guide.GetProperty("channels").EnumerateArray().Single(c => c.GetProperty("id").GetString() == sky).GetProperty("isFavourite").GetBoolean());

        var other = await (await client.PostAsJsonAsync(Api("profiles"), new { name = "Claire" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await client.PostAsJsonAsync(Api($"profiles/{other.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.All(await ChannelsAsync(Client(token)), c => Assert.False(c.GetProperty("isFavourite").GetBoolean()));

        (await client.DeleteAsync(Api($"livetv/channels/{sky}/favourite"))).EnsureSuccessStatusCode();
        Assert.All(await ChannelsAsync(client), c => Assert.False(c.GetProperty("isFavourite").GetBoolean()));
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync(Api($"livetv/channels/{Guid.NewGuid()}/favourite"), null)).StatusCode);
    }

    [Fact]
    public async Task Home_has_a_live_tv_row_of_the_profiles_favourites()
    {
        var client = Client();
        await ConnectAsync(client);
        static JsonElement? LiveRow(JsonElement home) =>
            home.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("id").GetString() == "live-tv").Cast<JsonElement?>().FirstOrDefault();

        // Nothing starred: no row.
        Assert.Null(LiveRow(await client.GetFromJsonAsync<JsonElement>(Api("home"))));

        var bbc = (await ChannelsAsync(client)).Single(c => c.GetProperty("name").GetString() == "BBC One").GetProperty("id").GetString();
        (await client.PutAsync(Api($"livetv/channels/{bbc}/favourite"), null)).EnsureSuccessStatusCode();
        var row = LiveRow(await client.GetFromJsonAsync<JsonElement>(Api("home")));
        Assert.NotNull(row);
        Assert.Equal("live", row.Value.GetProperty("style").GetString());
        Assert.Equal(0, row.Value.GetProperty("items").GetArrayLength());
        var channel = Assert.Single(row.Value.GetProperty("channels").EnumerateArray());
        Assert.Equal("The News", channel.GetProperty("now").GetProperty("title").GetString());

        // Switched off in Customise display: gone.
        (await client.PutAsJsonAsync(Api("settings/layout"), new { home = new[] { new { id = "live-tv", enabled = false } }, movies = Array.Empty<object>(), shows = Array.Empty<object>() }))
            .EnsureSuccessStatusCode();
        // The saved layout is picked up when the config file reloads, a moment later.
        JsonElement? after = row;
        for (var i = 0; i < 20 && after is not null; i++)
        {
            await Task.Delay(100);
            after = LiveRow(await client.GetFromJsonAsync<JsonElement>(Api("home")));
        }

        Assert.Null(after);
    }

    [Fact]
    public async Task Logos_are_only_fetched_from_the_source_itself()
    {
        var client = Client();
        await ConnectAsync(client);
        var channels = await ChannelsAsync(client);

        var own = await client.GetAsync(channels[0].GetProperty("logoUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal("image/png", own.Content.Headers.ContentType?.MediaType);

        // A logo on another server isn't fetched, so a channel list can't make Flexarr call anywhere it likes.
        var elsewhere = await client.GetAsync(channels[1].GetProperty("logoUrl").GetString());
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.DoesNotContain(_dispatcharr.Requests, u => u.Host == "elsewhere.example");
        Assert.Equal(JsonValueKind.Null, channels[2].GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task Watching_needs_a_source_and_streams_need_a_viewer()
    {
        var client = Client();
        var watch = await client.PostAsJsonAsync(Api($"livetv/channels/{Guid.NewGuid()}/watch"), new { });
        Assert.Equal(HttpStatusCode.Conflict, watch.StatusCode);

        // The viewer id in the address is the capability: a made-up one gets nothing, signed in or not.
        var anonymous = _server.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(Api("livetv/watch/0123456789abcdef0123456789abcdef/live.m3u8"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.GetAsync(Api("livetv/watch/0123456789abcdef0123456789abcdef/seg1.ts"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Api("livetv/channels"))).StatusCode);
    }
}
