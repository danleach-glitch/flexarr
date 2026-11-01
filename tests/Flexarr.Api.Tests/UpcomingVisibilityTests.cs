using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Coming soon and Downloads leave out titles in a library hidden from the profile.</summary>
public class UpcomingVisibilityTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task ComingSoonAndDownloads_LeaveOutShowsInAHiddenLibrary()
    {
        var hiddenFolder = $"/Volumes/Media/claire-{Guid.NewGuid():N}";
        Guid hidden;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var mine = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = $"/Volumes/Media/tv-{Guid.NewGuid():N}" }] };
            var theirs = new Library { Name = $"Claire's {Guid.NewGuid():N}", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = hiddenFolder }] };
            db.Libraries.AddRange(mine, theirs);
            db.MediaItems.Add(new MediaItem { LibraryId = theirs.Id, Kind = MediaKind.Series, Title = "Made in Chelsea", SortTitle = "made in chelsea", TvdbId = "910001", Path = $"{hiddenFolder}/Made in Chelsea" });
            await db.SaveChangesAsync();
            hidden = theirs.Id;
        }

        var arr = new FakeArr();
        var today = DateTime.UtcNow.Date;
        var airs = today.AddDays(2).ToString("yyyy-MM-dd'T'20:00:00'Z'", CultureInfo.InvariantCulture);
        static string Episode(int id, string show, int tvdb, string path, string airs) =>
            $$$"""
            {"id": {{{id}}}, "seriesId": {{{id}}}, "title": "Ep {{{id}}}", "seasonNumber": 1, "episodeNumber": 1, "airDateUtc": "{{{airs}}}", "hasFile": false,
             "series": {"id": {{{id}}}, "title": "{{{show}}}", "tvdbId": {{{tvdb}}}, "path": "{{{path}}}"}}
            """;
        arr.Responses[$"GET //sonarr.test/api/v3/calendar?start={today.AddDays(-2):yyyy-MM-dd}&end={today.AddDays(14):yyyy-MM-dd}&includeSeries=true&includeEpisodeImages=true&unmonitored=false"] =
            $"[{Episode(1, "Made in Chelsea", 910001, "/elsewhere/Made in Chelsea", airs)}, {Episode(2, "Not Scanned Yet", 910002, $"{hiddenFolder}/Not Scanned Yet", airs)}, {Episode(3, "Brand New", 910003, "/tv/Brand New", airs)}]";
        arr.Responses["GET //sonarr.test/api/v3/queue?page=1&pageSize=200&includeSeries=true&includeEpisode=true"] = """
            {"totalRecords": 1, "records": [{"id": 5, "episodeId": 1, "seriesId": 1, "title": "MIC", "size": 100, "sizeleft": 50, "status": "downloading",
              "series": {"id": 1, "title": "Made in Chelsea", "tvdbId": 910001}, "episode": {"id": 1, "title": "Ep 1", "seasonNumber": 1, "episodeNumber": 1}}]}
            """;
        using var app = arr.Serve(factory);
        var admin = Client(app.CreateClient());
        var created = await (await admin.PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = $"Dan {Guid.NewGuid():N}"[..20], hiddenLibraryIds = new[] { hidden } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await admin.PostAsJsonAsync(new Uri($"/api/v1/profiles/{created.GetProperty("id").GetString()}/select", UriKind.Relative), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var dan = Client(app.CreateClient());
        dan.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        UpcomingEndpoints.ForgetArrCalls();
        static List<string?> Shows(JsonElement list) => [.. list.EnumerateArray().Select(u => u.GetProperty("seriesTitle").GetString()).Order()];
        var forDan = await dan.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/upcoming?days=14", UriKind.Relative));
        var forMain = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/upcoming?days=14", UriKind.Relative));
        var downloadsForDan = await dan.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/downloads", UriKind.Relative));
        var downloadsForMain = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/downloads", UriKind.Relative));

        Assert.Equal(["Brand New"], Shows(forDan));
        Assert.Equal(["Brand New", "Made in Chelsea", "Not Scanned Yet"], Shows(forMain));
        Assert.Empty(downloadsForDan.EnumerateArray());
        Assert.Single(downloadsForMain.EnumerateArray());
    }

    private static HttpClient Client(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }
}
