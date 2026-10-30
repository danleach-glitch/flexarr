using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Flexarr.Api.Tests;

/// <summary>An assistant connected over MCP can find titles and play them on a device.</summary>
public class McpTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private async Task<McpClient> ConnectAsync(string? apiKey = FlexarrApiFactory.ApiKey)
    {
        var http = factory.CreateClient();
        var options = new HttpClientTransportOptions { Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"), TransportMode = HttpTransportMode.StreamableHttp };
        if (apiKey is not null)
        {
            options.AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = apiKey };
        }

        return await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true));
    }

    [Fact]
    public async Task TheTools_AreListed_AndSearchFindsATitle()
    {
        await SeedAsync("The Traitors Uncloaked", ("S01E01", 1, 1));
        await using var client = await ConnectAsync();

        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "search_library");
        Assert.Contains(tools, t => t.Name == "play_on_device");
        Assert.Contains(tools, t => t.Name == "list_devices");
        Assert.Contains(tools, t => t.Name == "live_channels");
        Assert.Contains(tools, t => t.Name == "watch_channel_on_device");
        Assert.Contains(tools, t => t.Name == "record_programme");
        Assert.Contains(tools, t => t.Name == "recordings");

        var found = await client.CallToolAsync("search_library", new Dictionary<string, object?> { ["query"] = "traitors" });
        Assert.NotEqual(true, found.IsError);
        var result = Result(found);
        var title = result.GetProperty("titles").EnumerateArray().Single(t => t.GetProperty("title").GetString() == "The Traitors Uncloaked");
        Assert.Equal("Series", title.GetProperty("kind").GetString());
        Assert.Equal(1, title.GetProperty("unwatchedEpisodes").GetInt32());
    }

    [Fact]
    public async Task PlayOnDevice_ResolvesAShowToItsNextEpisode_AndSaysWhetherTheDeviceTookIt()
    {
        var show = await SeedAsync("Curb Your Enthusiasm", ("S01E01", 1, 1), ("S01E02", 1, 2));
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var device = await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Lounge TV" })).Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = device.GetProperty("id").GetString()!;

        await using var client = await ConnectAsync();
        var devices = Result(await client.CallToolAsync("list_devices", new Dictionary<string, object?>()));
        var lounge = devices.EnumerateArray().Single(d => d.GetProperty("id").GetString() == deviceId);
        Assert.Equal("Lounge TV", lounge.GetProperty("name").GetString());
        Assert.False(lounge.GetProperty("online").GetBoolean());

        var played = await client.CallToolAsync("play_on_device", new Dictionary<string, object?> { ["deviceId"] = deviceId, ["itemId"] = show.Series, ["fromBeginning"] = false });
        Assert.NotEqual(true, played.IsError);
        var outcome = Result(played);
        Assert.False(outcome.GetProperty("delivered").GetBoolean());
        Assert.Equal("S01E01", outcome.GetProperty("willPlay").GetProperty("title").GetString());
        Assert.Contains("isn't open on Lounge TV", outcome.GetProperty("message").GetString(), StringComparison.Ordinal);

        var missing = await client.CallToolAsync("play_on_device", new Dictionary<string, object?> { ["deviceId"] = "nope", ["itemId"] = show.Series, ["fromBeginning"] = false });
        Assert.True(missing.IsError);
    }

    [Fact]
    public async Task SetDeviceSound_NeedsOnlyWhatChanges_AndTakesNamesOnly()
    {
        await using var client = await ConnectAsync();
        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "set_device_sound");
        var required = tool.JsonSchema.TryGetProperty("required", out var r) ? r.EnumerateArray().Select(e => e.GetString()).ToList() : [];
        Assert.Equal(["deviceId"], required);

        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var device = await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Den TV" })).Content.ReadFromJsonAsync<JsonElement>();
        var deviceId = device.GetProperty("id").GetString()!;

        var closed = Result(await client.CallToolAsync("set_device_sound", new Dictionary<string, object?> { ["deviceId"] = deviceId, ["nightMode"] = "on" }));
        Assert.False(closed.GetProperty("delivered").GetBoolean());
        Assert.True((await client.CallToolAsync("set_device_sound", new Dictionary<string, object?> { ["deviceId"] = deviceId, ["dialogueBoost"] = "1" })).IsError);
        Assert.True((await client.CallToolAsync("set_device_sound", new Dictionary<string, object?> { ["deviceId"] = deviceId, ["dialogueBoost"] = "low, high" })).IsError);
    }

    [Fact]
    public async Task WithoutASignIn_TheEndpointRefuses()
    {
        await Assert.ThrowsAnyAsync<Exception>(async () => await ConnectAsync(apiKey: null));
    }

    /// <summary>The tool's structured result, or its text parsed as JSON.</summary>
    private static JsonElement Result(CallToolResult result)
    {
        if (result.StructuredContent is { } structured)
        {
            // Tools returning a bare value wrap it as { "result": ... }.
            return structured.TryGetProperty("result", out var inner) ? inner : structured;
        }

        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        return JsonDocument.Parse(text).RootElement;
    }

    private async Task<(Guid Series, Dictionary<string, Guid> Episodes)> SeedAsync(string title, params (string Title, int Season, int Number)[] episodes)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = title, SortTitle = title.ToUpperInvariant() };
        db.AddRange(library, series);
        var seasons = new Dictionary<int, MediaItem>();
        var ids = new Dictionary<string, Guid>();
        foreach (var (name, number, episode) in episodes)
        {
            if (!seasons.TryGetValue(number, out var season))
            {
                season = seasons[number] = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = $"Season {number}", SortTitle = $"{number:D4}", SeasonNumber = number, Parent = series };
                db.Add(season);
            }

            var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = name, Parent = season, SeasonNumber = number, EpisodeNumber = episode, SortTitle = $"{episode:D4}" };
            db.Add(item);
            ids[name] = item.Id;
        }

        await db.SaveChangesAsync();
        return (series.Id, ids);
    }
}
