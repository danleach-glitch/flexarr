using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

public sealed class PathMappingFinderTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
    private static readonly string[] Shows = ["Andor (2022)", "Severance (2022)", "Slow Horses (2022)"];

    [Fact]
    public async Task Folders_SaysWhereEachAppsFoldersAre()
    {
        using var server = new FlexarrApiFactory();
        var arr = Arr(server, out var tv);
        using var app = arr.Serve(server);
        await AddShowsAsync(app, tv);

        var folders = await Client(app).GetFromJsonAsync<List<ArrFolderDto>>("/api/v1/arr/folders", Json);

        var sonarr = Assert.Single(folders!, f => f.App == "Sonarr");
        Assert.Equal(ArrFolderState.Suggested, sonarr.State);
        Assert.Equal(("/tv", tv), (sonarr.Suggested!.From, sonarr.Suggested.To));
        Assert.True(sonarr.Certain);
        Assert.Equal((3, 3), (sonarr.Agreeing, sonarr.Compared));

        var radarr = Assert.Single(folders!, f => f.App == "Radarr");
        Assert.Equal(ArrFolderState.Found, radarr.State);
        Assert.Equal(server.MediaDirectory, radarr.Local);
    }

    [Fact]
    public async Task ApplyCertain_SavesTheMappingOnce_AndRefreshesUnlinkedTitles()
    {
        using var server = new FlexarrApiFactory();
        var arr = Arr(server, out var tv);
        using var app = arr.Serve(server);
        await AddShowsAsync(app, tv);

        using (var scope = app.Services.CreateScope())
        {
            var added = await scope.ServiceProvider.GetRequiredService<PathMappingFinder>().ApplyCertainAsync(CancellationToken.None);
            Assert.Equal(("/tv", tv), (Assert.Single(added).From, added[0].To));
        }

        var options = app.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;
        Assert.Contains(options.PathMappings, m => m.From == "/tv" && m.To == tv);
        Assert.Contains(options.PathMappings, m => m.From == "/data"); // Kept.
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.All(db.MediaItems.Where(i => i.Kind == MediaKind.Series), s => Assert.Equal(DateTime.UnixEpoch, s.MetadataUpdatedAt));
        }

        // Taken out by hand, it isn't saved again.
        await Client(app).PutAsJsonAsync("/api/v1/settings", new { pathMappings = options.PathMappings.Where(m => m.From != "/tv") });
        using (var scope = app.Services.CreateScope())
        {
            Assert.Empty(await scope.ServiceProvider.GetRequiredService<PathMappingFinder>().ApplyCertainAsync(CancellationToken.None));
        }

        var folders = await Client(app).GetFromJsonAsync<List<ArrFolderDto>>("/api/v1/arr/folders", Json);
        Assert.Equal(ArrFolderState.Suggested, Assert.Single(folders!, f => f.App == "Sonarr").State);
    }

    [Fact]
    public async Task Folders_SaysAMappedFolderIsMapped()
    {
        using var server = new FlexarrApiFactory();
        var arr = Arr(server, out var tv);
        using var app = arr.Serve(server);
        await Client(app).PutAsJsonAsync("/api/v1/settings", new { pathMappings = new[] { new { from = "/tv", to = tv } } });

        var folders = await Client(app).GetFromJsonAsync<List<ArrFolderDto>>("/api/v1/arr/folders", Json);

        var sonarr = Assert.Single(folders!, f => f.App == "Sonarr");
        Assert.Equal((ArrFolderState.Mapped, tv), (sonarr.State, sonarr.Local));
    }

    /// <summary>Sonarr keeps its shows in /tv, which is the media folder's TV here; Radarr's folder is here as it is.</summary>
    private static FakeArr Arr(FlexarrApiFactory server, out string tv)
    {
        tv = Directory.CreateDirectory(Path.Combine(server.MediaDirectory, "TV")).FullName;
        var arr = new FakeArr();
        arr.Responses["GET //sonarr.test/api/v3/rootfolder"] = new JsonArray(new JsonObject { ["id"] = 1, ["path"] = "/tv" }).ToJsonString();
        arr.Responses["GET //sonarr.test/api/v3/series"] = new JsonArray([.. Shows.Select((s, i) => (JsonNode)new JsonObject
        {
            ["id"] = i + 1, ["title"] = s, ["path"] = $"/tv/{s}",
        })]).ToJsonString();
        arr.Responses["GET //radarr.test/api/v3/rootfolder"] = new JsonArray(new JsonObject { ["id"] = 1, ["path"] = server.MediaDirectory }).ToJsonString();
        return arr;
    }

    private static async Task AddShowsAsync(WebApplicationFactory<Program> app, string tv)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = "TV", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = tv }] };
        db.Add(library);
        db.AddRange(Shows.Select(s => new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Series, Title = s, SortTitle = s, Path = Path.Combine(tv, s),
            MetadataSource = "Sonarr lookup", MetadataUpdatedAt = DateTime.UtcNow,
        }));
        await db.SaveChangesAsync();
    }

    private static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }
}
