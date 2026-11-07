using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.LiveTv;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>The guide's optional narrowing, ETag and the programme endpoint (audit perf-20/27/56).</summary>
public sealed class LiveTvGuideOptionsTests : IDisposable
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeDispatcharr _dispatcharr = new();
    private readonly WebApplicationFactory<Program> _server;
    private readonly string _from = DateTime.UtcNow.AddMinutes(-30).ToString("O");

    public LiveTvGuideOptionsTests()
    {
        _server = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<XtreamClient>().ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
            services.AddHttpClient(XtreamClient.LogoClientName).ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
            services.AddHttpClient(CommunityLogoIndex.ClientName).ConfigurePrimaryHttpMessageHandler(() => _dispatcharr);
        }));

        var now = DateTime.UtcNow;
        _dispatcharr.Guide = $"""
            <?xml version="1.0" encoding="UTF-8"?><tv>
            <programme start="{Xmltv(now.AddMinutes(-20))}" stop="{Xmltv(now.AddMinutes(10))}" channel="101"><title>The News</title><desc>Headlines.</desc></programme>
            <programme start="{Xmltv(now.AddMinutes(10))}" stop="{Xmltv(now.AddMinutes(70))}" channel="101"><title>Doctor Who</title><desc>The Doctor returns.</desc></programme>
            <programme start="{Xmltv(now.AddMinutes(-5))}" stop="{Xmltv(now.AddMinutes(55))}" channel="102"><title>Gardeners' World</title><desc>Roses.</desc></programme>
            <programme start="{Xmltv(now.AddMinutes(-5))}" stop="{Xmltv(now.AddMinutes(55))}" channel="201"><title>Sky News Live</title><desc>More headlines.</desc></programme>
            </tv>
            """;
    }

    public void Dispose()
    {
        _server.Dispose();
        _factory.Dispose();
        _dispatcharr.Dispose();
    }

    private static string Xmltv(DateTime t) => t.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture) + " +0000";

    private static Uri Api(string path) => new($"/api/v1/{path}", UriKind.Relative);

    private async Task<HttpClient> ConnectedAsync()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        (await client.PutAsJsonAsync(Api("settings"), new { liveTv = new { baseUrl = FakeDispatcharr.Root, username = FakeDispatcharr.User, password = FakeDispatcharr.Password } }))
            .EnsureSuccessStatusCode();
        for (var i = 0; i < 50 && !(await client.GetFromJsonAsync<JsonElement>(Api("livetv/status"))).GetProperty("configured").GetBoolean(); i++)
        {
            await Task.Delay(100);
        }

        await using var scope = _server.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<LiveTvGuide>().RefreshAsync(CancellationToken.None, false);
        return client;
    }

    private string Guide(string extra = "") => $"livetv/guide?from={Uri.EscapeDataString(_from)}&hours=3{extra}";

    private static string[] Names(JsonElement guide) => [.. guide.GetProperty("channels").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    private static JsonElement[] Programmes(JsonElement guide) => [.. guide.GetProperty("channels").EnumerateArray().SelectMany(c => c.GetProperty("programmes").EnumerateArray())];

    [Fact]
    public async Task Without_the_new_parameters_the_guide_is_every_channel_with_descriptions()
    {
        var client = await ConnectedAsync();

        var guide = await client.GetFromJsonAsync<JsonElement>(Api(Guide()));

        Assert.Equal(["BBC One", "BBC Two", "Sky News"], Names(guide));
        Assert.Equal(4, Programmes(guide).Length);
        Assert.All(Programmes(guide), p => Assert.False(string.IsNullOrEmpty(p.GetProperty("description").GetString())));
        Assert.Equal(["id", "number", "name", "group", "logoUrl", "isFavourite", "programmes"],
            guide.GetProperty("channels")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["from", "until", "channels"], guide.EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Channels_group_and_favourites_narrow_the_guide()
    {
        var client = await ConnectedAsync();
        var all = await client.GetFromJsonAsync<JsonElement>(Api(Guide()));
        var ids = all.GetProperty("channels").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!, c => c.GetProperty("id").GetString()!);

        Assert.Equal(["BBC Two", "Sky News"], Names(await client.GetFromJsonAsync<JsonElement>(Api(Guide($"&channels={ids["Sky News"]},{ids["BBC Two"]}")))));
        Assert.Equal(["BBC One", "Sky News"], Names(await client.GetFromJsonAsync<JsonElement>(Api(Guide("&group=UK%20News")))));
        Assert.Empty(Names(await client.GetFromJsonAsync<JsonElement>(Api(Guide("&favourites=true")))));

        (await client.PutAsync(Api($"livetv/channels/{ids["BBC Two"]}/favourite"), null)).EnsureSuccessStatusCode();
        var favourites = await client.GetFromJsonAsync<JsonElement>(Api(Guide("&favourites=true")));
        Assert.Equal(["BBC Two"], Names(favourites));
        Assert.Equal(["Gardeners' World"], Programmes(favourites).Select(p => p.GetProperty("title").GetString()));
        Assert.Equal(["BBC Two"], Names(await client.GetFromJsonAsync<JsonElement>(Api(Guide("&favourites=false&group=UK%20General")))));
        Assert.Empty(Names(await client.GetFromJsonAsync<JsonElement>(Api(Guide("&favourites=true&group=UK%20News")))));

        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(Api(Guide("&channels=not-an-id")))).StatusCode);
    }

    [Fact]
    public async Task Descriptions_can_be_left_out_and_come_from_the_programme_itself()
    {
        var client = await ConnectedAsync();

        var guide = await client.GetFromJsonAsync<JsonElement>(Api(Guide("&descriptions=false")));
        Assert.Equal(4, Programmes(guide).Length);
        Assert.All(Programmes(guide), p => Assert.Equal(JsonValueKind.Null, p.GetProperty("description").ValueKind));

        var doctor = Programmes(guide).Single(p => p.GetProperty("title").GetString() == "Doctor Who");
        var one = await client.GetFromJsonAsync<JsonElement>(Api($"livetv/programmes/{doctor.GetProperty("id").GetInt64()}"));
        Assert.Equal("Doctor Who", one.GetProperty("title").GetString());
        Assert.Equal("The Doctor returns.", one.GetProperty("description").GetString());
        Assert.Equal(doctor.GetProperty("start").GetString(), one.GetProperty("start").GetString());

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api("livetv/programmes/987654321"))).StatusCode);
    }

    [Fact]
    public async Task An_unchanged_guide_answers_304_to_its_etag()
    {
        var client = await ConnectedAsync();

        var first = await client.GetAsync(Api(Guide()));
        first.EnsureSuccessStatusCode();
        Assert.Equal("application/json", first.Content.Headers.ContentType?.MediaType);
        var etag = first.Headers.ETag;
        Assert.NotNull(etag);

        using var again = new HttpRequestMessage(HttpMethod.Get, Api(Guide()));
        again.Headers.IfNoneMatch.Add(etag);
        var unchanged = await client.SendAsync(again);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());

        // Something in it changed (a favourite): the whole guide again, with a new tag.
        var bbc = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("channels")[0].GetProperty("id").GetString();
        (await client.PutAsync(Api($"livetv/channels/{bbc}/favourite"), null)).EnsureSuccessStatusCode();
        using var afterChange = new HttpRequestMessage(HttpMethod.Get, Api(Guide()));
        afterChange.Headers.IfNoneMatch.Add(etag);
        var changed = await client.SendAsync(afterChange);
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(etag, changed.Headers.ETag);
    }
}
