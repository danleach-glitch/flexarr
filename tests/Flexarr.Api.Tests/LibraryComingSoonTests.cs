using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A film or TV library's Browse has a Coming soon row of what's on its way to that library alone.</summary>
public class LibraryComingSoonTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task Browse_ComingSoon_HasOnlyThisLibrarysFilms()
    {
        var (a, b, aFolder, bFolder) = await TwoMovieLibrariesAsync();
        var arr = new FakeArr();
        var digital = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
        arr.Responses[$"GET //radarr.test/api/v3/calendar?{Range()}"] = $"""
            [{Movie(1, "Due in A", 1001, $"{aFolder}/Due in A", digital)},
             {Movie(2, "Due in B", 1002, $"{bFolder}/Due in B", digital)},
             {Movie(3, "Scanned into A", 1003, "/somewhere/else", digital)},
             {Movie(4, "Nowhere Yet", 1004, "/not/a/library/folder", digital)}]
            """;
        using var app = arr.Serve(factory);
        var client = Client(app.CreateClient());
        UpcomingEndpoints.ForgetArrCalls();

        var browseA = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{a}/browse", UriKind.Relative));
        var browseB = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{b}/browse", UriKind.Relative));

        Assert.Contains("upcoming", RowIds(browseA));
        Assert.Equal(["Due in A", "Scanned into A"], Titles(browseA).Where(t => t != "Nowhere Yet"));
        // Not in any library's folder: it goes to the oldest movie library, and B is newer than A.
        Assert.Equal(["Due in B"], Titles(browseB));
    }

    [Fact]
    public async Task Browse_ComingSoon_SwitchedOff_DoesNotAskRadarr()
    {
        var (a, _, aFolder, _) = await TwoMovieLibrariesAsync();
        var arr = new FakeArr();
        var digital = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd'T'00:00:00'Z'", CultureInfo.InvariantCulture);
        arr.Responses[$"GET //radarr.test/api/v3/calendar?{Range()}"] = $"[{Movie(1, "Due in A", 1001, $"{aFolder}/Due in A", digital)}]";
        using var app = arr.Serve(factory);
        var admin = Client(app.CreateClient());
        var created = await (await admin.PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = $"Off {Guid.NewGuid():N}"[..20] }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await admin.PostAsJsonAsync(new Uri($"/api/v1/profiles/{created.GetProperty("id").GetString()}/select", UriKind.Relative), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var profile = Client(app.CreateClient());
        profile.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);
        var off = new[] { new { id = "upcoming", enabled = false } };
        (await profile.PutAsJsonAsync(new Uri("/api/v1/settings/layout", UriKind.Relative), new { home = Array.Empty<object>(), movies = off, shows = Array.Empty<object>() }))
            .EnsureSuccessStatusCode();
        UpcomingEndpoints.ForgetArrCalls();
        arr.Requests.Clear();

        var browse = await profile.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/libraries/{a}/browse", UriKind.Relative));

        Assert.DoesNotContain("upcoming", RowIds(browse));
        Assert.Empty(browse.GetProperty("upcoming").EnumerateArray());
        Assert.DoesNotContain(arr.Requests, r => r.Contains("calendar", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Browse_RadarrDown_StillLoadsWithoutTheRow()
    {
        var (a, _, _, _) = await TwoMovieLibrariesAsync();
        using var app = new FakeArr().Serve(factory); // every call a 404
        var client = Client(app.CreateClient());
        UpcomingEndpoints.ForgetArrCalls();

        var response = await client.GetAsync(new Uri($"/api/v1/libraries/{a}/browse", UriKind.Relative));
        var browse = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("upcoming", RowIds(browse));
        Assert.Contains("recent", RowIds(browse));
    }

    /// <summary>A, then B a moment newer, each with a film so Browse has a row; "Scanned into A" is in A already.</summary>
    private async Task<(Guid A, Guid B, string AFolder, string BFolder)> TwoMovieLibrariesAsync()
    {
        var aFolder = $"/Volumes/Films/a-{Guid.NewGuid():N}";
        var bFolder = $"/Volumes/Films/b-{Guid.NewGuid():N}";
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var a = new Library { Name = $"Films A {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = aFolder }] };
        var b = new Library { Name = $"Films B {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = bFolder }], CreatedAt = a.CreatedAt.AddSeconds(1) };
        db.Libraries.AddRange(a, b);
        db.MediaItems.AddRange(
            new MediaItem { LibraryId = a.Id, Kind = MediaKind.Movie, Title = "Scanned into A", SortTitle = "scanned into a", TmdbId = "1003", Path = $"{aFolder}/Scanned" },
            new MediaItem { LibraryId = b.Id, Kind = MediaKind.Movie, Title = "Already in B", SortTitle = "already in b", TmdbId = $"{Random.Shared.Next(100000, 999999)}", Path = $"{bFolder}/Already" });
        await db.SaveChangesAsync();
        return (a.Id, b.Id, aFolder, bFolder);
    }

    private static string Range()
    {
        var today = DateTime.UtcNow.Date;
        return $"start={today.AddDays(-2):yyyy-MM-dd}&end={today.AddDays(10):yyyy-MM-dd}&includeSeries=true&includeEpisodeImages=true&unmonitored=false";
    }

    private static string Movie(int id, string title, int tmdb, string path, string digital) =>
        $$"""{"id": {{id}}, "title": "{{title}}", "tmdbId": {{tmdb}}, "path": "{{path}}", "digitalRelease": "{{digital}}", "hasFile": false}""";

    private static List<string?> RowIds(JsonElement browse) => [.. browse.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("id").GetString())];

    private static List<string?> Titles(JsonElement browse) => [.. browse.GetProperty("upcoming").EnumerateArray().Select(u => u.GetProperty("title").GetString()).Order()];

    private static HttpClient Client(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }
}
