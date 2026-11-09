using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Api.LiveTv;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
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

    public const string AdminKey = "admin-k3y";

    /// <summary>How many times its guides have been asked to download.</summary>
    public int GuideDownloads => _guideDownloads;

    public bool GuideSourceFails { get; set; }

    /// <summary>When set, how every request fails, as the HTTP client fails: thrown, or an answer.</summary>
    public Func<HttpResponseMessage>? Fails { get; set; }

    /// <summary>Answers who a key belongs to, as Dispatcharr 0.20 does; otherwise a 404, as an older one.</summary>
    public bool SaysWhoKeysAreFor { get; set; }

    private int _guideDownloads;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        Requests.Enqueue(url);
        if (Fails is { } fails)
        {
            return Task.FromResult(fails());
        }

        if (SaysWhoKeysAreFor && url.AbsolutePath == "/api/accounts/users/me/")
        {
            return Task.FromResult(request.Headers.TryGetValues("X-API-Key", out var key) && key.Single() == AdminKey
                ? Json("""{"user_level":10}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized));
        }

        if (url.AbsolutePath.StartsWith("/api/epg/", StringComparison.Ordinal))
        {
            if (!request.Headers.TryGetValues("X-API-Key", out var keys) || keys.Single() != AdminKey)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            if (request.Method == HttpMethod.Post)
            {
                Interlocked.Increment(ref _guideDownloads);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
            }

            // Each download finishes at once, stamped with how many there have been; a failed one leaves the stamp alone, as Dispatcharr's do.
            return Task.FromResult(Json(GuideSourceFails
                ? $$"""[{"id":1,"source_type":"xmltv","is_active":true,"status":"error","updated_at":"0","last_message":"{{_guideDownloads}}"}]"""
                : $$"""[{"id":1,"source_type":"xmltv","is_active":true,"status":"success","updated_at":"{{_guideDownloads}}"}]"""));
        }

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
            // Logos that redirect: to another of the source's own, and off it to a device on the home network.
            "/api/channels/logos/8/cache/" => Redirect("/api/channels/logos/1/cache/"),
            "/api/channels/logos/9/cache/" => Redirect("http://192.168.1.1/admin/logo.png"),
            // The community collection's file list (GitHub) and its logos.
            "/repos/tv-logo/tv-logos/git/trees/main" => Json("""{"tree":[{"path":"countries/united-kingdom/bbc-one-uk.png","type":"blob"},{"path":"countries/united-kingdom/sky-news-uk.png","type":"blob"}]}"""),
            _ => url.Host is "elsewhere.example" or "raw.githubusercontent.com" ? Image() : new HttpResponseMessage(HttpStatusCode.NotFound),
        });
    }

    private static HttpResponseMessage Redirect(string to) => new(HttpStatusCode.Found) { Headers = { Location = new Uri(to, UriKind.RelativeOrAbsolute) } };

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
        {
            services.AddHttpClient<XtreamClient>().ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
            services.AddHttpClient(XtreamClient.LogoClientName).ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
            services.AddHttpClient<DispatcharrGuides>().ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
            // GitHub too, so tests never reach the internet.
            services.AddHttpClient(CommunityLogoIndex.ClientName).ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
        }));

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
        // The config file reloads a moment after a save; refresh once the source is in.
        for (var i = 0; i < 50 && !(await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("configured").GetBoolean(); i++)
        {
            await Task.Delay(100);
        }

        await RefreshAsync();
    }

    private async Task RefreshAsync(bool onlyIfChanged = false)
    {
        // Background workers don't run in tests; refresh as the guide worker would.
        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().RefreshAsync(CancellationToken.None, onlyIfChanged);
    }

    /// <summary>The Live TV settings once <paramref name="saved"/> holds: the config file reloads a moment after a save.</summary>
    private static async Task<JsonElement> LiveTvSettingsAsync(HttpClient client, Func<JsonElement, bool> saved)
    {
        var live = default(JsonElement);
        for (var i = 0; i < 50; i++)
        {
            live = (await client.GetFromJsonAsync<JsonElement>(Api("settings"))).GetProperty("liveTv");
            if (live.ValueKind == JsonValueKind.Object && saved(live))
            {
                break;
            }

            await Task.Delay(100);
        }

        return live;
    }

    private static async Task<JsonElement[]> ChannelsAsync(HttpClient client) => [.. (await client.GetFromJsonAsync<JsonElement>(Api("livetv/channels"))).EnumerateArray()];

    [Fact]
    public async Task RecordingsLibrary_IsLeftOutOfTheListsAndHome_OnceThereIsLiveTv()
    {
        var recordings = new Library { Name = RecordingsLibrary.Name, Type = LibraryType.TvShows };
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Libraries.Add(recordings);
            await db.SaveChangesAsync();
        }

        var client = Client();
        async Task<bool> ListedAsync(string path) =>
            (await client.GetFromJsonAsync<JsonElement>(Api(path))).EnumerateArray().Any(l => l.GetProperty("id").GetGuid() == recordings.Id);
        async Task<bool> RowAsync(string path) => (await client.GetStringAsync(Api(path))).Contains($"recent-{recordings.Id}", StringComparison.Ordinal);

        // Without Live TV, there's nowhere else to find them.
        Assert.True(await ListedAsync("libraries"));

        await ConnectAsync(client);

        Assert.False(await ListedAsync("libraries"));
        Assert.False(await ListedAsync("libraries?all=true"));
        Assert.False(await RowAsync("home"));
        Assert.False(await RowAsync("settings/layout"));
        // Still there to open, for a recording's own page.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api($"libraries/{recordings.Id}"))).StatusCode);
    }

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
        live = await LiveTvSettingsAsync(client, l => l.GetProperty("rewindMinutes").GetInt32() == 30);
        Assert.True(live.GetProperty("hasPassword").GetBoolean());
        Assert.Equal(30, live.GetProperty("rewindMinutes").GetInt32());

        // Pointed somewhere else without one: it isn't sent there.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = "http://another:9191", username = FakeDispatcharr.User } })).EnsureSuccessStatusCode();
        live = await LiveTvSettingsAsync(client, l => l.GetProperty("baseUrl").GetString() == "http://another:9191");
        Assert.False(live.GetProperty("hasPassword").GetBoolean());
        Assert.False((await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("configured").GetBoolean());
    }

    [Fact]
    public async Task Settings_name_the_server_for_links_to_apps_at_localhost()
    {
        var settings = await Client().GetFromJsonAsync<JsonElement>(Api("settings"));

        var name = settings.GetProperty("hostName").GetString();
        Assert.False(string.IsNullOrEmpty(name));
        Assert.DoesNotContain(".local", name, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null, Flexarr.Api.Security.NetworkZone.Local, false)]
    [InlineData(null, Flexarr.Api.Security.NetworkZone.Tailnet, true)]
    [InlineData(null, Flexarr.Api.Security.NetworkZone.Internet, true)]
    [InlineData("original", Flexarr.Api.Security.NetworkZone.Tailnet, false)]
    [InlineData("reduced", Flexarr.Api.Security.NetworkZone.Local, true)]
    public void Away_from_home_gets_the_reduced_version_unless_asked_otherwise(string? quality, Flexarr.Api.Security.NetworkZone zone, bool reduced)
    {
        Assert.Equal(reduced, LiveTvEndpoints.Reduced(quality, zone));
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

    private static async Task<JsonElement> GuideProgrammeAsync(HttpClient client, string title)
    {
        var guide = await client.GetFromJsonAsync<JsonElement>(Api("livetv/guide?hours=3"));
        return guide.GetProperty("channels").EnumerateArray().SelectMany(c => c.GetProperty("programmes").EnumerateArray())
            .First(p => p.GetProperty("title").GetString() == title);
    }

    [Fact]
    public async Task A_profile_kept_out_of_the_Recordings_library_doesnt_see_the_recordings_list()
    {
        var client = Client();
        await ConnectAsync(client);
        var doctorWho = (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("id").GetInt64();
        (await client.PostAsJsonAsync(Api("livetv/recordings"), new { programmeId = doctorWho })).EnsureSuccessStatusCode();

        string hiddenFrom, sees;
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var recordings = db.Libraries.Add(new Library { Name = RecordingsLibrary.Name, Type = LibraryType.TvShows }).Entity;
            var other = db.Libraries.Add(new Library { Name = "Films", Type = LibraryType.Movies }).Entity;
            await db.SaveChangesAsync();
            hiddenFrom = db.Profiles.Add(new Flexarr.Core.Media.Profile { Id = Flexarr.Core.Media.Profile.NewId(), Name = "Lodger", HiddenLibraryIds = [recordings.Id] }).Entity.Id;
            sees = db.Profiles.Add(new Flexarr.Core.Media.Profile { Id = Flexarr.Core.Media.Profile.NewId(), Name = "Claire", HiddenLibraryIds = [other.Id] }).Entity.Id;
            await db.SaveChangesAsync();
        }

        async Task<JsonElement> ListAsync(string profileId)
        {
            var token = (await (await client.PostAsJsonAsync(Api($"profiles/{profileId}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
            return await Client(token).GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        }

        Assert.Empty((await ListAsync(hiddenFrom)).GetProperty("upcoming").EnumerateArray());
        Assert.Single((await ListAsync(sees)).GetProperty("upcoming").EnumerateArray());
    }

    [Fact]
    public async Task A_programme_can_be_set_to_record_and_taken_off_again()
    {
        var client = Client();
        await ConnectAsync(client);
        var doctorWho = (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("id").GetInt64();

        var scheduled = await (await client.PostAsJsonAsync(Api("livetv/recordings"), new { programmeId = doctorWho })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("scheduled", scheduled.GetProperty("state").GetString());
        Assert.Equal("BBC One", scheduled.GetProperty("channelName").GetString());
        Assert.False(scheduled.GetProperty("clashes").GetBoolean());
        // The guide shows it, and asking twice doesn't add it twice.
        Assert.Equal("scheduled", (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("recording").GetString());
        // The channel list (On now, the iPhone's list, Home) says so on what's next.
        var bbc = (await ChannelsAsync(client)).Single(c => c.GetProperty("name").GetString() == "BBC One");
        Assert.Equal("scheduled", bbc.GetProperty("next").GetProperty("recording").GetString());
        Assert.Equal(JsonValueKind.Null, bbc.GetProperty("now").GetProperty("recording").ValueKind);
        var again = await (await client.PostAsJsonAsync(Api("livetv/recordings"), new { programmeId = doctorWho })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(scheduled.GetProperty("id").GetString(), again.GetProperty("id").GetString());

        var list = await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        Assert.Single(list.GetProperty("upcoming").EnumerateArray());
        Assert.Equal(1, list.GetProperty("streams").GetInt32());

        (await client.DeleteAsync(Api($"livetv/recordings/{scheduled.GetProperty("id").GetString()}"))).EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"))).GetProperty("upcoming").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("recording").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(Api("livetv/recordings"), new { programmeId = 999999 })).StatusCode);
    }

    [Fact]
    public async Task Stopping_a_recording_the_scheduler_hasnt_carried_on_since_a_restart_stops_it()
    {
        var client = Client();
        await ConnectAsync(client);
        var doctorWho = (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("id").GetInt64();
        var scheduled = await (await client.PostAsJsonAsync(Api("livetv/recordings"), new { programmeId = doctorWho })).Content.ReadFromJsonAsync<JsonElement>();
        var id = Guid.Parse(scheduled.GetProperty("id").GetString()!);
        // As a restart leaves it: still marked Recording, but nothing running it.
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
            var recording = await db.Recordings.SingleAsync(r => r.Id == id);
            recording.State = Flexarr.Core.LiveTv.RecordingState.Recording;
            await db.SaveChangesAsync();
        }

        Assert.Equal(HttpStatusCode.Accepted, (await client.DeleteAsync(Api($"livetv/recordings/{id}"))).StatusCode);

        var dvr = _server.Services.GetRequiredService<DvrService>();
        for (var i = 0; i < 100 && dvr.Active.Count > 0; i++)
        {
            await Task.Delay(50);
        }

        Assert.Empty(dvr.Active);
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
            Assert.NotEqual(Flexarr.Core.LiveTv.RecordingState.Recording, (await db.Recordings.AsNoTracking().SingleAsync(r => r.Id == id)).State);
        }
    }

    [Fact]
    public async Task A_series_schedules_its_showings()
    {
        var client = Client();
        await ConnectAsync(client);
        var doctorWho = (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("id").GetInt64();

        var rule = await (await client.PostAsJsonAsync(Api("livetv/recordings/series"), new { programmeId = doctorWho, newOnly = true })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Doctor Who", rule.GetProperty("title").GetString());
        Assert.Equal("BBC One", rule.GetProperty("channelName").GetString());
        Assert.True(rule.GetProperty("newOnly").GetBoolean());

        // The scheduler (a background service, not run in tests) plans it.
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var scheduler = new DvrScheduler(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<DvrService>(),
                services.GetRequiredService<DvrSignal>(), services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Flexarr.Api.Configuration.FlexarrOptions>>(),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DvrScheduler>.Instance);
            await scheduler.RunOnceAsync(CancellationToken.None);
        }

        var list = await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        var upcoming = Assert.Single(list.GetProperty("upcoming").EnumerateArray());
        Assert.Equal("Doctor Who", upcoming.GetProperty("title").GetString());
        Assert.Equal("The Star Beast", upcoming.GetProperty("subtitle").GetString());
        Assert.Equal(rule.GetProperty("id").GetString(), upcoming.GetProperty("ruleId").GetString());
        var series = Assert.Single(list.GetProperty("series").EnumerateArray());
        Assert.Equal(1, series.GetProperty("upcoming").GetInt32());
        Assert.True((await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("series").GetBoolean());

        // Stopping the series takes its scheduled showings off.
        (await client.DeleteAsync(Api($"livetv/recordings/series/{rule.GetProperty("id").GetString()}"))).EnsureSuccessStatusCode();
        list = await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        Assert.Empty(list.GetProperty("upcoming").EnumerateArray());
        Assert.Empty(list.GetProperty("series").EnumerateArray());
    }

    [Fact]
    public async Task A_series_finds_its_showings_whatever_their_case_and_spacing_and_nothing_else()
    {
        var now = DateTime.UtcNow;
        _dispatcharr.Guide = $"""
            <?xml version="1.0" encoding="UTF-8"?><tv>
            <programme start="{Xmltv(now.AddMinutes(10))}" stop="{Xmltv(now.AddMinutes(70))}" channel="101"><title>Doctor Who</title><sub-title>The Star Beast</sub-title><desc>One</desc><new/></programme>
            <programme start="{Xmltv(now.AddDays(1))}" stop="{Xmltv(now.AddDays(1).AddHours(1))}" channel="101"><title>  DOCTOR WHO </title><sub-title>Wild Blue Yonder</sub-title><desc>Two</desc><new/></programme>
            <programme start="{Xmltv(now.AddDays(2))}" stop="{Xmltv(now.AddDays(2).AddHours(1))}" channel="101"><title>Doctor Who Confidential</title><new/></programme>
            <programme start="{Xmltv(now.AddDays(9))}" stop="{Xmltv(now.AddDays(9).AddHours(1))}" channel="101"><title>Doctor Who</title><sub-title>Too far ahead</sub-title><new/></programme>
            </tv>
            """;
        var client = Client();
        await ConnectAsync(client);
        var doctorWho = (await GuideProgrammeAsync(client, "Doctor Who")).GetProperty("id").GetInt64();
        (await client.PostAsJsonAsync(Api("livetv/recordings/series"), new { programmeId = doctorWho, newOnly = true })).EnsureSuccessStatusCode();

        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var scheduler = new DvrScheduler(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<DvrService>(),
                services.GetRequiredService<DvrSignal>(), services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Flexarr.Api.Configuration.FlexarrOptions>>(),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DvrScheduler>.Instance);
            await scheduler.RunOnceAsync(CancellationToken.None);
        }

        var list = await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        Assert.Equal(["The Star Beast", "Wild Blue Yonder"],
            list.GetProperty("upcoming").EnumerateArray().OrderBy(u => u.GetProperty("start").GetDateTime()).Select(u => u.GetProperty("subtitle").GetString()).ToList());
    }

    [Fact]
    public async Task A_series_with_more_showings_than_SQLite_takes_parameters_is_still_planned()
    {
        var client = Client();
        await ConnectAsync(client);
        var now = DateTime.UtcNow;
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var channel = new Flexarr.Core.LiveTv.LiveChannel { Name = "Rolling News" };
            db.LiveChannels.Add(channel);
            // Repeats only, so a new-only rule plans none of them: the look-up is what's under test.
            db.LiveProgrammes.AddRange(Enumerable.Range(0, 33_000).Select(i => new Flexarr.Core.LiveTv.LiveProgramme
            {
                ChannelId = channel.Id, Title = "Rolling News", Start = now.AddSeconds(10 * i + 60), End = now.AddSeconds(10 * i + 70),
            }));
            db.RecordingRules.Add(new Flexarr.Core.LiveTv.RecordingRule { Title = "Rolling News", NewOnly = true });
            await db.SaveChangesAsync();
        }

        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var services = scope.ServiceProvider;
            var scheduler = new DvrScheduler(services.GetRequiredService<IServiceScopeFactory>(), services.GetRequiredService<DvrService>(),
                services.GetRequiredService<DvrSignal>(), services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Flexarr.Api.Configuration.FlexarrOptions>>(),
                TimeProvider.System, Microsoft.Extensions.Logging.Abstractions.NullLogger<DvrScheduler>.Instance);
            await scheduler.RunOnceAsync(CancellationToken.None);
        }

        var list = await client.GetFromJsonAsync<JsonElement>(Api("livetv/recordings"));
        Assert.Empty(list.GetProperty("upcoming").EnumerateArray());
    }

    [Fact]
    public async Task The_dvr_settings_are_kept_with_the_connection()
    {
        var client = Client();
        await ConnectAsync(client);
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, dvrFolder = "/tmp/flexarr-dvr-test", paddingAfterMinutes = 10, minFreeGigabytes = 20 } }))
            .EnsureSuccessStatusCode();
        var live = await LiveTvSettingsAsync(client, l => l.GetProperty("paddingAfterMinutes").GetInt32() == 10);

        Assert.Equal("/tmp/flexarr-dvr-test", live.GetProperty("dvrFolder").GetString());
        Assert.Equal(Path.GetFullPath("/tmp/flexarr-dvr-test"), live.GetProperty("dvrFolderInUse").GetString());
        Assert.Equal(20, live.GetProperty("minFreeGigabytes").GetInt32());
        // Saving only the rewind keeps the rest; an empty folder goes back to the default.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, rewindMinutes = 30, dvrFolder = "" } }))
            .EnsureSuccessStatusCode();
        live = await LiveTvSettingsAsync(client, l => l.GetProperty("rewindMinutes").GetInt32() == 30);
        Assert.Equal(10, live.GetProperty("paddingAfterMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Null, live.GetProperty("dvrFolder").ValueKind);
    }

    [Fact]
    public async Task A_recordings_folder_flexarr_cant_write_to_isnt_saved()
    {
        var client = Client();
        await ConnectAsync(client);
        // A file where the folder would go: it can't be made, as a folder the sandbox never let it reach can't.
        var blocker = Path.Combine(Path.GetTempPath(), $"flexarr-dvr-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "");
        try
        {
            var response = await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, dvrFolder = Path.Combine(blocker, "DVR") } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("can’t write", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            response = await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, dvrFolder = "DVR" } });
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task Homes_live_tv_row_starts_with_the_channel_watched_last()
    {
        var client = Client();
        await ConnectAsync(client);
        var channels = await ChannelsAsync(client);
        string IdOf(string name) => channels.Single(c => c.GetProperty("name").GetString() == name).GetProperty("id").GetString()!;
        (await client.PutAsync(Api($"livetv/channels/{IdOf("BBC One")}/favourite"), null)).EnsureSuccessStatusCode();
        (await client.PutAsync(Api($"livetv/channels/{IdOf("BBC Two")}/favourite"), null)).EnsureSuccessStatusCode();

        // Watching records the channel (a real stream isn't to be had here, so as the watch endpoint would).
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
            var profile = db.Profiles.Select(p => p.Id).First();
            db.LiveLastChannels.Add(new Flexarr.Core.LiveTv.LiveLastChannel { ProfileId = profile, ChannelId = Guid.Parse(IdOf("Sky News")), WatchedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var home = await client.GetFromJsonAsync<JsonElement>(Api("home"));
        var row = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "live-tv").GetProperty("channels").EnumerateArray().ToList();
        Assert.Equal(["Sky News", "BBC One", "BBC Two"], row.Select(c => c.GetProperty("name").GetString()));
        Assert.False(row[0].GetProperty("isFavourite").GetBoolean());
        Assert.True(row[1].GetProperty("isFavourite").GetBoolean());
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
    public async Task Channels_take_the_community_collections_logo_when_it_has_one()
    {
        var client = Client();
        await ConnectAsync(client);
        var channels = await ChannelsAsync(client);
        var bbc = channels.Single(c => c.GetProperty("name").GetString() == "BBC One");

        // UK group, "BBC One" → countries/united-kingdom/bbc-one-uk.png, served through Flexarr like any logo.
        var logo = await client.GetAsync(bbc.GetProperty("logoUrl").GetString());
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Contains(_dispatcharr.Requests, u => u.AbsoluteUri == "https://raw.githubusercontent.com/tv-logo/tv-logos/main/countries/united-kingdom/bbc-one-uk.png");
        Assert.DoesNotContain(_dispatcharr.Requests, u => u.AbsolutePath == "/api/channels/logos/1/cache/");
    }

    [Fact]
    public async Task An_unchanged_guide_isnt_written_again_and_a_changed_one_is()
    {
        var client = Client();
        await ConnectAsync(client);
        async Task<List<(long Id, string Title)>> ProgrammesAsync()
        {
            await using var scope = _server.Services.CreateAsyncScope();
            return [.. (await scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>().LiveProgrammes.OrderBy(p => p.Start)
                .Select(p => new { p.Id, p.Title }).ToListAsync()).Select(p => (p.Id, p.Title))];
        }

        var first = await ProgrammesAsync();
        Assert.Equal(["The News", "Doctor Who"], first.Select(p => p.Title));

        // The same guide again: the same rows, not deleted and written anew.
        await RefreshAsync();
        Assert.Equal(first, await ProgrammesAsync());

        // A changed guide replaces it.
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("Doctor Who", "Doctor Who: Specials", StringComparison.Ordinal);
        await RefreshAsync();
        var changed = await ProgrammesAsync();
        Assert.Equal(["The News", "Doctor Who: Specials"], changed.Select(p => p.Title));
        Assert.Empty(changed.Select(p => p.Id).Intersect(first.Select(p => p.Id)));
    }

    [Fact]
    public async Task Programmes_go_as_they_slip_out_of_the_past_kept_even_when_the_guide_is_unchanged()
    {
        var client = Client();
        await ConnectAsync(client);
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            // As if the last refresh was hours ago: The News ended longer ago than the guide keeps.
            var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
            await db.LiveProgrammes.Where(p => p.Title == "The News").ExecuteUpdateAsync(u => u
                .SetProperty(p => p.Start, DateTime.UtcNow.AddHours(-5)).SetProperty(p => p.End, DateTime.UtcNow.AddHours(-4)));
        }

        await RefreshAsync();
        await using (var scope = _server.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
            Assert.Equal(["Doctor Who"], await db.LiveProgrammes.Select(p => p.Title).ToListAsync());
        }
    }

    [Fact]
    public async Task A_quick_check_brings_in_channels_added_in_dispatcharr_and_otherwise_changes_nothing()
    {
        var client = Client();
        await ConnectAsync(client);
        var refreshed = (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("refreshedAt").GetDateTime();
        var guideFetches = _dispatcharr.Requests.Count(u => u.AbsolutePath == "/xmltv.php");

        // Nothing changed: the guide isn't fetched again, and the last refresh stands.
        await Task.Delay(20);
        await RefreshAsync(onlyIfChanged: true);
        Assert.Equal(guideFetches, _dispatcharr.Requests.Count(u => u.AbsolutePath == "/xmltv.php"));
        Assert.Equal(refreshed, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("refreshedAt").GetDateTime());

        // A channel added in Dispatcharr: in at the next check, with its guide.
        _dispatcharr.Channels.Add((4, 202, "Sky News 2", "UK News", null));
        await RefreshAsync(onlyIfChanged: true);
        Assert.Contains("Sky News 2", (await ChannelsAsync(client)).Select(c => c.GetProperty("name").GetString()));
        Assert.Equal(guideFetches + 1, _dispatcharr.Requests.Count(u => u.AbsolutePath == "/xmltv.php"));
    }

    [Fact]
    public async Task A_channel_with_nothing_ahead_brings_the_next_full_refresh_forward_and_is_counted()
    {
        var client = Client();
        await ConnectAsync(client);
        // BBC One has programmes ahead; BBC Two and Sky News have none.
        Assert.Equal(2, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("channelsWithoutListings").GetInt32());

        await using var scope = _server.Services.CreateAsyncScope();
        var guide = scope.ServiceProvider.GetRequiredService<LiveTvGuide>();
        var now = DateTime.UtcNow;
        Assert.False(await guide.IsDueAsync(now.AddMinutes(30), CancellationToken.None));
        Assert.True(await guide.IsDueAsync(now.AddMinutes(90), CancellationToken.None));

        // Every channel listed: back to the usual wait.
        var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
        var listed = await db.LiveChannels.Select(c => c.Id).ToListAsync();
        db.LiveProgrammes.AddRange(listed.Select(id => new Flexarr.Core.LiveTv.LiveProgramme { ChannelId = id, Title = "All day", Start = now, End = now.AddDays(1) }));
        await db.SaveChangesAsync();
        Assert.False(await guide.IsDueAsync(now.AddMinutes(90), CancellationToken.None));
        Assert.True(await guide.IsDueAsync(now.AddHours(7), CancellationToken.None));
    }

    private static async Task SaveAdminKeyAsync(HttpClient client, string key)
    {
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, adminApiKey = key } })).EnsureSuccessStatusCode();
        await LiveTvSettingsAsync(client, l => l.GetProperty("hasAdminApiKey").GetBoolean());
        // The options reload a moment after the settings say so.
        await Task.Delay(200);
    }

    private async Task DownloadAsync()
    {
        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().DownloadSourcesAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Refreshes_that_find_the_same_guide_while_a_channel_stays_empty_come_round_less_often()
    {
        var client = Client();
        await ConnectAsync(client);
        await using var scope = _server.Services.CreateAsyncScope();
        var guide = scope.ServiceProvider.GetRequiredService<LiveTvGuide>();
        await RefreshAsync();
        var start = DateTime.UtcNow;
        Assert.False(await guide.IsDueAsync(start.AddMinutes(90), CancellationToken.None));
        Assert.True(await guide.IsDueAsync(start.AddMinutes(150), CancellationToken.None));

        // A changed guide that fills nothing in doesn't: Dispatcharr's guide can change with a channel still empty.
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("Doctor Who", "Doctor Who: Specials", StringComparison.Ordinal);
        await RefreshAsync();
        Assert.False(await guide.IsDueAsync(DateTime.UtcNow.AddMinutes(90), CancellationToken.None));
        Assert.True(await guide.IsDueAsync(DateTime.UtcNow.AddMinutes(250), CancellationToken.None));

        // One that fills a channel in goes back to looking hourly.
        var soon = DateTime.UtcNow.AddHours(1).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var later = DateTime.UtcNow.AddHours(2).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("</tv>", $"""<programme start="{soon} +0000" stop="{later} +0000" channel="201"><title>Filled in</title></programme></tv>""", StringComparison.Ordinal);
        await RefreshAsync();
        Assert.Equal(0, _server.Services.GetRequiredService<LiveTvStatus>().QuietRefreshes);
    }

    [Fact]
    public async Task A_healthy_guide_keeps_its_hourly_look_for_when_a_channels_listings_run_out()
    {
        var client = Client();
        await ConnectAsync(client);
        var soon = DateTime.UtcNow.AddHours(1).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var later = DateTime.UtcNow.AddHours(2).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        _dispatcharr.Guide = "<tv>" + string.Concat(new[] { 101, 102, 201 }.Select(c => $"""<programme start="{soon} +0000" stop="{later} +0000" channel="{c}"><title>Filled in</title></programme>""")) + "</tv>";
        var status = _server.Services.GetRequiredService<LiveTvStatus>();
        for (var i = 0; i < 5; i++)
        {
            await RefreshAsync(); // every channel listed, refresh after refresh
        }

        await using var scope = _server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>();
        var ended = DateTime.UtcNow.AddMinutes(-1);
        var channel = await db.LiveChannels.Where(c => c.Name == "Sky News").Select(c => c.Id).SingleAsync();
        await db.LiveProgrammes.Where(p => p.ChannelId == channel).ExecuteUpdateAsync(u => u.SetProperty(p => p.Start, ended.AddHours(-1)).SetProperty(p => p.End, ended));

        await RefreshAsync(); // finds them run out

        Assert.Equal(0, status.QuietRefreshes);
        Assert.True(await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().IsDueAsync(DateTime.UtcNow.AddMinutes(90), CancellationToken.None));

        await RefreshAsync(); // and it stays empty: only now does the wait begin to grow
        Assert.Equal(1, status.QuietRefreshes);
    }

    [Fact]
    public async Task A_guide_whose_bytes_differ_with_the_same_programmes_isnt_written_again()
    {
        var client = Client();
        await ConnectAsync(client);
        await RefreshAsync();
        async Task<List<long>> IdsAsync()
        {
            await using var scope = _server.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>().LiveProgrammes.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        }

        var first = await IdsAsync();
        Assert.NotEmpty(first);
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("<tv>", """<tv generator-info-name="Dispatcharr" date="20261109120000">""", StringComparison.Ordinal);

        await RefreshAsync();

        Assert.Equal(first, await IdsAsync());
    }

    [Fact]
    public async Task A_guide_is_read_through_once_when_new_and_not_at_all_when_its_the_same_file()
    {
        var client = Client();
        var status = _server.Services.GetRequiredService<LiveTvStatus>();
        await ConnectAsync(client);
        Assert.Equal(1, status.GuideParses); // new: once, for both knowing it's new and writing it

        await RefreshAsync();
        Assert.Equal(1, status.GuideParses); // the same file

        var dated = _dispatcharr.Guide.Replace("<tv>", """<tv date="20261109120000">""", StringComparison.Ordinal);
        _dispatcharr.Guide = dated;
        await RefreshAsync();
        Assert.Equal(2, status.GuideParses); // a new file has to be looked in
        await RefreshAsync();
        Assert.Equal(2, status.GuideParses); // and is known from then on

        _dispatcharr.Guide = dated.Replace("Doctor Who", "Doctor Who: Specials", StringComparison.Ordinal);
        await RefreshAsync();
        Assert.Equal(3, status.GuideParses);
        var titles = (await GuideProgrammeTitlesAsync()).Order();
        Assert.Equal(["Doctor Who: Specials", "The News"], titles);
    }

    [Fact]
    public async Task The_same_file_is_read_again_for_a_new_channel_it_already_lists()
    {
        var client = Client();
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("</tv>", $"""<programme start="{Xmltv(DateTime.UtcNow.AddMinutes(-5))}" stop="{Xmltv(DateTime.UtcNow.AddMinutes(30))}" channel="301"><title>New arrival</title></programme></tv>""", StringComparison.Ordinal);
        await ConnectAsync(client);
        Assert.DoesNotContain("New arrival", await GuideProgrammeTitlesAsync());

        _dispatcharr.Channels.Add((4, 301, "Film4", "UK General", null));
        await RefreshAsync();

        Assert.Contains("New arrival", await GuideProgrammeTitlesAsync());
    }

    private async Task<List<string>> GuideProgrammeTitlesAsync()
    {
        await using var scope = _server.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>().LiveProgrammes.Select(p => p.Title).ToListAsync();
    }

    [Fact]
    public async Task A_refresh_that_fails_isnt_tried_again_every_few_minutes_for_an_empty_channel()
    {
        var client = Client();
        await ConnectAsync(client);
        await using var scope = _server.Services.CreateAsyncScope();
        var guide = scope.ServiceProvider.GetRequiredService<LiveTvGuide>();
        var status = _server.Services.GetRequiredService<LiveTvStatus>();
        var failedAt = DateTime.UtcNow.AddMinutes(90);
        status.Finished(failedAt, "Not there");
        Assert.False(await guide.IsDueAsync(failedAt.AddMinutes(5), CancellationToken.None));
        status.Finished(DateTime.UtcNow, null);
    }

    [Fact]
    public async Task The_status_says_when_a_refresh_didnt_ask_the_sources_again()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, FakeDispatcharr.AdminKey);
        async Task<JsonElement> StatusAsync() => await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"));

        // Asked, and a source failed: that isn't skipping.
        _dispatcharr.GuideSourceFails = true;
        await DownloadAsync();
        Assert.False((await StatusAsync()).GetProperty("downloadSkipped").GetBoolean());
        var asked = _dispatcharr.GuideDownloads;

        // Pressed again within the limit: not asked, and the failure stands beside saying so.
        _dispatcharr.GuideSourceFails = false;
        await DownloadAsync();
        Assert.Equal(asked, _dispatcharr.GuideDownloads);
        var skipped = await StatusAsync();
        Assert.True(skipped.GetProperty("downloadSkipped").GetBoolean());
        Assert.Contains("couldn’t download", skipped.GetProperty("downloadError").GetString(), StringComparison.Ordinal);

        // A refresh nobody pressed moves on from it.
        _server.Services.GetRequiredService<LiveTvStatus>().ForgetDownloadProblem();
        Assert.False((await StatusAsync()).GetProperty("downloadSkipped").GetBoolean());
    }

    public static TheoryData<string, string, string> SourceFailures => new()
    {
        { "unreachable", "Flexarr can’t reach the Live TV source: Connection refused (dispatcharr:9191)", "⟦reach Connection refused (dispatcharr:9191)⟧" },
        { "slow", "The Live TV source didn’t answer in time.", "⟦late⟧" },
        { "refused", "The Live TV source refused the user name or password.", "⟦refused⟧" },
        { "broken", "The Live TV source answered with an error (500).", "⟦broken 500⟧" },
    };

    [Theory]
    [MemberData(nameof(SourceFailures))]
    public async Task A_source_failing_is_said_in_the_language_of_whoever_asks(string how, string english, string pseudo)
    {
        var client = Client();
        await ConnectAsync(client);
        var folder = Directory.CreateDirectory(Path.Combine(_factory.DataDirectory, "languages")).FullName;
        // A code of its own: what's loaded is shared across a test run, and the newest version of a code wins. en-XA is
        // LanguageEndpointTests', en-XC NotificationTests'.
        File.WriteAllText(Path.Combine(folder, "en-XB.json"), """
            {"format":1,"code":"en-XB","name":"Pseudo (test)","direction":"ltr","version":7,"coverage":{"web":1},"messages":{
            "Flexarr can’t reach the Live TV source: {reason}":"⟦reach {reason}⟧",
            "The Live TV source didn’t answer in time.":"⟦late⟧",
            "The Live TV source refused the user name or password.":"⟦refused⟧",
            "The Live TV source answered with an error ({code}).":"⟦broken {code}⟧"},
            "scopes":{}}
            """);
        _server.Services.GetRequiredService<Flexarr.Api.Localization.LanguageIndex>().LoadKept();

        // Each as the HTTP client really fails: thrown, or an answer.
        _dispatcharr.Fails = how switch
        {
            "unreachable" => () => throw new HttpRequestException("Connection refused (dispatcharr:9191)"),
            "slow" => () => throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing."),
            "refused" => () => new HttpResponseMessage(HttpStatusCode.Unauthorized),
            _ => () => new HttpResponseMessage(HttpStatusCode.InternalServerError),
        };
        await RefreshAsync();

        Assert.Equal(english, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("error").GetString());

        // The one failure, read later by someone in another language.
        var other = Client();
        other.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-XB");
        Assert.Equal(pseudo, (await other.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("error").GetString());

        // Testing a connection in Settings says the same.
        var tested = await (await other.PostAsJsonAsync(Api("settings/livetv/test"),
            new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = FakeDispatcharr.Password })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(pseudo, tested.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Each_finished_refresh_is_counted_even_when_it_fails_the_same_way()
    {
        var client = Client();
        await ConnectAsync(client);
        async Task<int> FinishedAsync() => (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("refreshesFinished").GetInt32();
        var before = await FinishedAsync();
        var status = _server.Services.GetRequiredService<LiveTvStatus>();
        status.Finished(DateTime.UtcNow, "Not there");
        status.Finished(DateTime.UtcNow, "Not there");
        Assert.Equal(before + 2, await FinishedAsync());
    }

    [Fact]
    public async Task A_guide_that_cant_be_read_is_said_in_flexarrs_own_words()
    {
        var client = Client();
        await ConnectAsync(client);
        _dispatcharr.Guide = "<tv><programme";

        await RefreshAsync();

        Assert.Equal("The guide from the Live TV source couldn’t be read.", (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("error").GetString());
        Assert.Equal(LiveTvRefreshProblem.GuideUnreadable, _server.Services.GetRequiredService<LiveTvStatus>().Problem);
    }

    [Fact]
    public async Task The_limit_on_downloads_holds_when_one_goes_wrong_part_way()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, FakeDispatcharr.AdminKey);
        _dispatcharr.GuideSourceFails = true;

        await DownloadAsync();
        Assert.Equal(1, _dispatcharr.GuideDownloads);
        Assert.Contains("couldn’t download", (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").GetString(), StringComparison.Ordinal);

        // Inside the limit nothing's asked, so a fix in Dispatcharr hasn't been tried: the fault stands.
        _dispatcharr.GuideSourceFails = false;
        await DownloadAsync();
        await RefreshAsync();
        Assert.Equal(1, _dispatcharr.GuideDownloads);
        Assert.Contains("couldn’t download", (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Removing_the_key_takes_what_it_ran_into_with_it()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, "wrong");
        await DownloadAsync();
        Assert.NotEqual(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").ValueKind);

        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, adminApiKey = "" } })).EnsureSuccessStatusCode();
        await LiveTvSettingsAsync(client, l => !l.GetProperty("hasAdminApiKey").GetBoolean());
        for (var i = 0; i < 50 && (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").ValueKind != JsonValueKind.Null; i++)
        {
            await Task.Delay(100);
        }

        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").ValueKind);
    }

    [Fact]
    public async Task A_guide_with_some_too_far_ahead_isnt_written_again_when_its_the_same()
    {
        var client = Client();
        await ConnectAsync(client);
        var far = DateTime.UtcNow.AddDays(10).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        _dispatcharr.Guide = _dispatcharr.Guide.Replace("</tv>", $"""<programme start="{far} +0000" stop="{far} +0000" channel="101"><title>Too far</title></programme></tv>""", StringComparison.Ordinal);
        await RefreshAsync();
        async Task<List<long>> IdsAsync()
        {
            await using var scope = _server.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>().LiveProgrammes.OrderBy(p => p.Id).Select(p => p.Id).ToListAsync();
        }

        var first = await IdsAsync();
        Assert.NotEmpty(first);
        await RefreshAsync();
        Assert.Equal(first, await IdsAsync());
    }

    [Fact]
    public async Task Without_an_admin_key_dispatcharr_isnt_asked_to_download()
    {
        await ConnectAsync(Client());
        await DownloadAsync();
        Assert.Equal(0, _dispatcharr.GuideDownloads);
    }

    [Fact]
    public async Task However_often_refresh_is_pressed_dispatcharrs_sources_are_only_asked_every_so_often()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, FakeDispatcharr.AdminKey);

        await DownloadAsync();
        Assert.Equal(1, _dispatcharr.GuideDownloads);
        await DownloadAsync();
        Assert.Equal(1, _dispatcharr.GuideDownloads);
    }

    [Fact]
    public async Task Saving_a_wrong_admin_key_says_so_and_leaves_what_was_saved()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, FakeDispatcharr.AdminKey);
        _dispatcharr.SaysWhoKeysAreFor = true;

        var refused = await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, adminApiKey = "wrong" } });

        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("admin API key", await refused.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True((await LiveTvSettingsAsync(client, l => l.GetProperty("hasAdminApiKey").GetBoolean())).GetProperty("hasAdminApiKey").GetBoolean());

        // The right one is fine, and the same one again isn't asked about.
        var before = _dispatcharr.Requests.Count(r => r.AbsolutePath == "/api/accounts/users/me/");
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, adminApiKey = FakeDispatcharr.AdminKey } })).EnsureSuccessStatusCode();
        Assert.Equal(before, _dispatcharr.Requests.Count(r => r.AbsolutePath == "/api/accounts/users/me/"));
    }

    [Fact]
    public async Task A_refused_key_is_put_in_words_and_can_be_tried_again_straight_away()
    {
        var client = Client();
        await ConnectAsync(client);
        await SaveAdminKeyAsync(client, "wrong");

        await DownloadAsync();
        Assert.Equal(0, _dispatcharr.GuideDownloads);
        Assert.Contains("refused", (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").GetString(), StringComparison.Ordinal);

        // A better key: no waiting, and the words go.
        await SaveAdminKeyAsync(client, FakeDispatcharr.AdminKey);
        await DownloadAsync();
        Assert.Equal(1, _dispatcharr.GuideDownloads);
        Assert.Equal(JsonValueKind.Null, (await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("downloadError").ValueKind);
    }

    [Fact]
    public void Only_refresh_pressed_asks_for_a_download_and_it_asks_once()
    {
        var signal = new LiveTvRefreshSignal();
        signal.Request();
        Assert.False(signal.TakeDownload());
        signal.Request(download: true);
        Assert.True(signal.TakeDownload());
        Assert.False(signal.TakeDownload());
    }

    [Fact]
    public async Task The_admin_key_is_never_sent_back_and_stays_with_its_server()
    {
        var client = Client();
        await ConnectAsync(client);
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, adminApiKey = "admin-k3y" } })).EnsureSuccessStatusCode();
        var saved = await LiveTvSettingsAsync(client, l => l.GetProperty("hasAdminApiKey").GetBoolean());
        Assert.DoesNotContain("admin-k3y", saved.GetRawText(), StringComparison.Ordinal);

        // Saved again without it: kept, while the server's the same.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, rewindMinutes = 30 } })).EnsureSuccessStatusCode();
        Assert.True((await LiveTvSettingsAsync(client, l => l.GetProperty("rewindMinutes").GetInt32() == 30)).GetProperty("hasAdminApiKey").GetBoolean());

        // A slash at the end is the same server.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root + "/", username = FakeDispatcharr.User, rewindMinutes = 45 } })).EnsureSuccessStatusCode();
        Assert.True((await LiveTvSettingsAsync(client, l => l.GetProperty("rewindMinutes").GetInt32() == 45)).GetProperty("hasAdminApiKey").GetBoolean());

        // Another path at that address is another app, behind a reverse proxy: not carried over.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root + "/other", username = FakeDispatcharr.User, password = "other" } })).EnsureSuccessStatusCode();
        Assert.False((await LiveTvSettingsAsync(client, l => l.GetProperty("baseUrl").GetString()!.EndsWith("/other", StringComparison.Ordinal))).GetProperty("hasAdminApiKey").GetBoolean());
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = FakeDispatcharr.Password, adminApiKey = "admin-k3y" } })).EnsureSuccessStatusCode();
        await LiveTvSettingsAsync(client, l => l.GetProperty("hasAdminApiKey").GetBoolean());

        // Another server: not carried over.
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = "http://elsewhere:9191", username = FakeDispatcharr.User, password = "other" } })).EnsureSuccessStatusCode();
        Assert.False((await LiveTvSettingsAsync(client, l => l.GetProperty("baseUrl").GetString() == "http://elsewhere:9191")).GetProperty("hasAdminApiKey").GetBoolean());
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
        // Sky News has no logo in Dispatcharr but one in the community collection, which it takes.
        Assert.Equal(JsonValueKind.String, channels[2].GetProperty("logoUrl").ValueKind);
    }

    [Fact]
    public async Task A_logo_redirect_is_only_followed_while_it_stays_on_the_source()
    {
        // Channels the community collection has no logo for, so the source's own are used.
        _dispatcharr.Channels[1] = _dispatcharr.Channels[1] with { Logo = $"{FakeDispatcharr.Root}/api/channels/logos/8/cache/" };
        _dispatcharr.Channels.Add((4, 301, "Local TV", "UK General", $"{FakeDispatcharr.Root}/api/channels/logos/9/cache/"));
        var client = Client();
        await ConnectAsync(client);
        var channels = await ChannelsAsync(client);
        string? LogoOf(string name) => channels.Single(c => c.GetProperty("name").GetString() == name).GetProperty("logoUrl").GetString();

        var own = await client.GetAsync(LogoOf("BBC Two"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Contains(_dispatcharr.Requests, u => u.AbsolutePath == "/api/channels/logos/1/cache/");

        var away = await client.GetAsync(LogoOf("Local TV"));
        Assert.Equal(HttpStatusCode.NotFound, away.StatusCode);
        Assert.DoesNotContain(_dispatcharr.Requests, u => u.Host == "192.168.1.1");
    }

    [Fact]
    public void The_logo_client_leaves_redirects_to_the_code_that_checks_them()
    {
        // Without the tests' stand-in: the server's own handler for logos.
        using var factory = new FlexarrApiFactory();
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(XtreamClient.LogoClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
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
