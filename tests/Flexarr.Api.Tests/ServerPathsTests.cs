using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Where things are on the server (folders, files, Sonarr and Radarr's addresses) is told to the main profile only.</summary>
public class ServerPathsTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static HttpClient Admin(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A phone the owner connected, as it is at first (the main profile) or with another profile chosen on it.</summary>
    private static async Task<HttpClient> PhoneAsync(WebApplicationFactory<Program> app, bool anotherProfile)
    {
        var admin = Admin(app);
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Phone" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var phone = app.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Api-Key", token);
        if (anotherProfile)
        {
            var profile = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}"[..20] })).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("id").GetString();
            Assert.True((await phone.PostAsJsonAsync(Api($"/profiles/{profile}/select"), new { })).IsSuccessStatusCode);
        }

        return phone;
    }

    [Fact]
    public async Task FoldersAndFilePaths_AreSentEmpty_ToEveryProfileButTheMainOne()
    {
        var folder = $"/Volumes/Media/films-{Guid.NewGuid():N}";
        var path = $"{folder}/Heat (1995)/Heat.1995.1080p.BluRay-GRP.mkv";
        Guid library, film;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = folder }] };
            var heat = new MediaItem
            {
                LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Heat", SortTitle = "heat", Path = $"{folder}/Heat (1995)",
                Files = [new MediaFile { Path = path, Size = 5, Height = 1080 }],
            };
            db.AddRange(films, heat);
            await db.SaveChangesAsync();
            (library, film) = (films.Id, heat.Id);
        }

        var main = await PhoneAsync(factory, anotherProfile: false);
        var sam = await PhoneAsync(factory, anotherProfile: true);

        // The main profile (and the API key) are told, as before.
        foreach (var owner in new[] { main, Admin(factory) })
        {
            var detail = await owner.GetFromJsonAsync<JsonElement>(Api($"/items/{film}"));
            Assert.Equal($"{folder}/Heat (1995)", detail.GetProperty("path").GetString());
            Assert.Equal(path, detail.GetProperty("files")[0].GetProperty("path").GetString());
            Assert.Equal([folder], (await owner.GetFromJsonAsync<JsonElement>(Api($"/libraries/{library}"))).GetProperty("folders").EnumerateArray().Select(f => f.GetString()));
            Assert.Contains((await owner.GetFromJsonAsync<JsonElement>(Api("/libraries"))).EnumerateArray(),
                l => l.GetProperty("id").GetGuid() == library && l.GetProperty("folders").GetArrayLength() == 1);
            Assert.NotEqual("", (await owner.GetFromJsonAsync<JsonElement>(Api("/livetv/recordings"))).GetProperty("folder").GetString());
        }

        // Anyone else gets the same fields (apps read them), with nothing in them, and everything else about the title.
        var theirs = await sam.GetFromJsonAsync<JsonElement>(Api($"/items/{film}"));
        Assert.Equal(JsonValueKind.Null, theirs.GetProperty("path").ValueKind);
        Assert.Equal("", theirs.GetProperty("files")[0].GetProperty("path").GetString());
        Assert.Equal((5, 1080), (theirs.GetProperty("files")[0].GetProperty("size").GetInt32(), theirs.GetProperty("files")[0].GetProperty("height").GetInt32()));
        Assert.Equal(0, (await sam.GetFromJsonAsync<JsonElement>(Api($"/libraries/{library}"))).GetProperty("folders").GetArrayLength());
        var listed = (await sam.GetFromJsonAsync<JsonElement>(Api("/libraries"))).EnumerateArray().Single(l => l.GetProperty("id").GetGuid() == library);
        Assert.Equal((0, 1), (listed.GetProperty("folders").GetArrayLength(), listed.GetProperty("itemCount").GetInt32()));
        Assert.Equal("", (await sam.GetFromJsonAsync<JsonElement>(Api("/livetv/recordings"))).GetProperty("folder").GetString());
        Assert.DoesNotContain("/Volumes/Media", await sam.GetStringAsync(Api($"/items/{film}")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ComingSoonAndDownloads_LinkIntoSonarr_OnlyForTheMainProfile()
    {
        var arr = new FakeArr();
        var today = DateTime.UtcNow.Date;
        var airs = today.AddDays(2).ToString("yyyy-MM-dd'T'20:00:00'Z'", CultureInfo.InvariantCulture);
        foreach (var days in new[] { 10, 14 })
        {
            arr.Responses[$"GET //sonarr.test/api/v3/calendar?start={today.AddDays(-2):yyyy-MM-dd}&end={today.AddDays(days):yyyy-MM-dd}&includeSeries=true&includeEpisodeImages=true&unmonitored=false"] =
                $$$"""
                [{"id": 1, "seriesId": 1, "title": "Ep 1", "seasonNumber": 1, "episodeNumber": 1, "airDateUtc": "{{{airs}}}", "hasFile": false,
                  "series": {"id": 1, "title": "Brand New", "tvdbId": 920001, "titleSlug": "brand-new", "path": "/tv/Brand New"}}]
                """;
        }

        arr.Responses["GET //sonarr.test/api/v3/queue?page=1&pageSize=200&includeSeries=true&includeEpisode=true"] = """
            {"totalRecords": 1, "records": [{"id": 5, "episodeId": 1, "seriesId": 1, "title": "BN", "size": 100, "sizeleft": 50, "status": "downloading",
              "series": {"id": 1, "title": "Brand New", "tvdbId": 920001}, "episode": {"id": 1, "title": "Ep 1", "seasonNumber": 1, "episodeNumber": 1}}]}
            """;
        using var app = arr.Serve(factory);
        var main = await PhoneAsync(app, anotherProfile: false);
        var sam = await PhoneAsync(app, anotherProfile: true);
        UpcomingEndpoints.ForgetArrCalls();

        var mine = (await main.GetFromJsonAsync<JsonElement>(Api("/upcoming?days=14"))).EnumerateArray().Single();
        var theirs = (await sam.GetFromJsonAsync<JsonElement>(Api("/upcoming?days=14"))).EnumerateArray().Single();
        var myDownload = (await main.GetFromJsonAsync<JsonElement>(Api("/downloads"))).EnumerateArray().Single();
        var theirDownload = (await sam.GetFromJsonAsync<JsonElement>(Api("/downloads"))).EnumerateArray().Single();

        Assert.Equal("http://sonarr.test/series/brand-new", mine.GetProperty("manageUrl").GetString());
        Assert.Equal("http://sonarr.test/activity/queue", myDownload.GetProperty("manageUrl").GetString());
        // The same title and download, without the address of the app they're in.
        Assert.Equal(("Brand New", JsonValueKind.Null), (theirs.GetProperty("seriesTitle").GetString(), theirs.GetProperty("manageUrl").ValueKind));
        Assert.Equal(("Brand New", JsonValueKind.Null), (theirDownload.GetProperty("seriesTitle").GetString(), theirDownload.GetProperty("manageUrl").ValueKind));
        foreach (var page in new[] { "/upcoming?days=14", "/downloads", "/home" })
        {
            Assert.DoesNotContain("sonarr.test", await sam.GetStringAsync(Api(page)), StringComparison.Ordinal);
        }

        Assert.Contains("sonarr.test", await main.GetStringAsync(Api("/home")), StringComparison.Ordinal);
    }
}
