using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

public class LibraryApiTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Fact]
    public async Task CreateLibrary_PersistsAndQueuesScan()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "movies-" + Guid.NewGuid())).FullName;

        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Movies", type = "Movies", folders = new[] { folder + "/" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LibraryDto>(Json);
        Assert.Equal([folder], created!.Folders);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == folder);

        var all = await Client().GetFromJsonAsync<List<LibraryDto>>(new Uri("/api/v1/libraries", UriKind.Relative), Json);
        Assert.Contains(all!, l => l.Id == created.Id);
    }

    [Fact]
    public async Task UpdateLibrary_RenamesWithoutRescanning_AndScansOnlyAddedFolders()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "films-" + Guid.NewGuid())).FullName;
        var extra = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "films-" + Guid.NewGuid())).FullName;
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Films", type = "Movies", folders = new[] { folder } })).Content.ReadFromJsonAsync<LibraryDto>(Json);
        var before = factory.ScanQueue.Requests.Count;

        var renamed = await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{created!.Id}", UriKind.Relative),
            new { name = "  Kids' films ", type = "Movies", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Kids' films", (await renamed.Content.ReadFromJsonAsync<LibraryDto>(Json))!.Name);
        Assert.Equal(before, factory.ScanQueue.Requests.Count);

        await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{created.Id}", UriKind.Relative),
            new { name = "Kids' films", type = "Movies", folders = new[] { folder, extra } });

        Assert.Equal([extra], factory.ScanQueue.Requests.Skip(before).Select(r => r.Path));
    }

    [Fact]
    public async Task CreateLibrary_RejectsMissingAndOverlappingFolders()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "tv-" + Guid.NewGuid())).FullName;
        await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative), new { name = "TV", type = "TvShows", folders = new[] { folder } });

        var missing = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Nope", type = "Movies", folders = new[] { "/definitely/not/here" } });
        var overlapping = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Dupe", type = "TvShows", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);
        Assert.Contains("overlaps", await overlapping.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateLibrary_RejectsTheRecordingsName()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "recordings-" + Guid.NewGuid())).FullName;

        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Recordings", type = "TvShows", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownLibrary_Items_ReturnsEmptyPage()
    {
        var page = await Client().GetFromJsonAsync<PagedDto<ItemSummaryDto>>(new Uri($"/api/v1/libraries/{Guid.NewGuid()}/items", UriKind.Relative), Json);

        Assert.Equal(0, page!.Total);
    }

    [Fact]
    public async Task Filesystem_ListsDirectories()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "browse-" + Guid.NewGuid(), "Child")).Parent!.FullName;

        var listing = await Client().GetFromJsonAsync<DirectoryListingDto>(new Uri($"/api/v1/filesystem?path={Uri.EscapeDataString(folder)}", UriKind.Relative), Json);

        Assert.Equal(["Child"], listing!.Directories.Select(d => d.Name));
    }
}
