using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Taking a film or show request back, and the list of what Seerr is waiting for.</summary>
public sealed class RequestUndoTests : IAsyncLifetime
{
    private const string RadarrQueue = "GET //radarr.test/api/v3/queue?page=1&pageSize=200&includeMovie=true";
    private const string SonarrQueue = "GET //sonarr.test/api/v3/queue?page=1&pageSize=200&includeSeries=true&includeEpisode=true";
    private const string NoRecords = """{"page":1,"pageSize":200,"totalRecords":0,"records":[]}""";

    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeArr _arr = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _arr.Serve(_factory).WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Seerr:BaseUrl", "http://seerr.test");
            b.UseSetting("Flexarr:Seerr:ApiKey", "key");
            b.ConfigureTestServices(s => s.AddHttpClient<SeerrClient>().ConfigurePrimaryHttpMessageHandler(() => _arr));
        });
        _arr.Responses[RadarrQueue] = NoRecords;
        _arr.Responses[SonarrQueue] = NoRecords;
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _arr.Dispose();
    }

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private void Deletes(params string[] paths)
    {
        foreach (var path in paths)
        {
            _arr.Responses[$"DELETE {path}"] = "{}";
        }
    }

    private async Task<T> InDbAsync<T>(Func<FlexarrDbContext, Task<T>> work)
    {
        using var scope = _server.Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>());
    }

    [Fact]
    public async Task AFilm_LeavesRadarrAndItsQueue_AndSeerrForgetsIt_ForEveryone()
    {
        _arr.Responses["GET /api/v1/movie/7001"] = """{"id":7001,"mediaInfo":{"id":55,"status":3,"requests":[{"id":9,"status":2,"type":"movie"}]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7001"] = """[{"id":3,"title":"Film","hasFile":false}]""";
        _arr.Responses[RadarrQueue] = """{"page":1,"pageSize":200,"totalRecords":1,"records":[{"id":77,"movieId":3,"downloadId":"d1","size":10,"sizeleft":5}]}""";
        Deletes("/api/v3/queue/77?removeFromClient=true&blocklist=false", "/api/v3/movie/3?deleteFiles=false&addImportExclusion=false", "/api/v1/request/9",
            "/api/v1/media/55");
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 7001 });
            return await db.SaveChangesAsync();
        });

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7001 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v3/queue/77?removeFromClient=true&blocklist=false", _arr.Requests);
        Assert.Contains("DELETE /api/v3/movie/3?deleteFiles=false&addImportExclusion=false", _arr.Requests);
        Assert.Contains("DELETE /api/v1/request/9", _arr.Requests);
        Assert.Contains("DELETE /api/v1/media/55", _arr.Requests);
        Assert.True(await InDbAsync(db => db.MediaRequests.AllAsync(r => r.TmdbId != 7001 || r.FulfilledAt != null)));
    }

    [Fact]
    public async Task AFilmRadarrHasOnDisk_IsNeverTouched()
    {
        _arr.Responses["GET /api/v1/movie/7002"] = """{"id":7002,"mediaInfo":{"id":56,"status":3,"requests":[{"id":10,"status":2,"type":"movie"}]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7002"] = """[{"id":4,"title":"Film","hasFile":true}]""";
        Deletes("/api/v1/request/10", "/api/v1/media/56");

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7002 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE /api/v3/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OneSeasonOfAPartlyHadShow_OnlyThatSeasonStopsAndItsRequestGoes()
    {
        _arr.Responses["GET /api/v1/tv/8001"] = """
            {"id":8001,"mediaInfo":{"id":66,"status":4,"tvdbId":81,"requests":[
              {"id":1,"status":2,"type":"tv","seasons":[{"seasonNumber":2}]},
              {"id":2,"status":2,"type":"tv","seasons":[{"seasonNumber":1},{"seasonNumber":3}]}]}}
            """;
        var series = """{"id":5,"title":"Show","seasons":[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true},{"seasonNumber":3,"monitored":true}]}""";
        _arr.Responses["GET //sonarr.test/api/v3/series?tvdbId=81"] = $"[{series}]";
        _arr.Responses["GET //sonarr.test/api/v3/series/5"] = series;
        _arr.Responses["PUT //sonarr.test/api/v3/series/5"] = series;
        _arr.Responses["PUT //sonarr.test/api/v3/episode/monitor"] = "[]";
        _arr.Responses["GET //sonarr.test/api/v3/episode?seriesId=5&includeImages=true"] = """
            [{"id":101,"seasonNumber":1,"episodeNumber":1,"hasFile":true,"monitored":true},
             {"id":201,"seasonNumber":2,"episodeNumber":1,"hasFile":false,"monitored":true},
             {"id":301,"seasonNumber":3,"episodeNumber":1,"hasFile":false,"monitored":true}]
            """;
        string? putSeries = null;
        _arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v3/series/5")
            {
                putSeries = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        };
        Deletes("/api/v1/request/1");

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "tv", tmdbId = 8001, seasons = new[] { 2 } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/request/1", _arr.Requests);
        Assert.DoesNotContain("DELETE /api/v1/request/2", _arr.Requests);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE /api/v1/media", StringComparison.Ordinal) || r.StartsWith("DELETE /api/v3/series", StringComparison.Ordinal));
        var seasons = JsonDocument.Parse(putSeries!).RootElement.GetProperty("seasons").EnumerateArray()
            .ToDictionary(s => s.GetProperty("seasonNumber").GetInt32(), s => s.GetProperty("monitored").GetBoolean());
        Assert.Equal(new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true }, seasons);
    }

    [Fact]
    public async Task NothingToTakeBack_IsDone_AndChangesNothing()
    {
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7003"] = "[]";

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7003 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ABadMediaType_IsRefused()
    {
        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "album", tmdbId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Requested_ListsWhatsOpen_WithProgress_AndKeepsToALibrary()
    {
        var movies = new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] };
        var shows = new Library { Name = "Shows", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = "/media/tv" }] };
        await InDbAsync(async db =>
        {
            db.Libraries.AddRange(movies, shows);
            return await db.SaveChangesAsync();
        });
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=all&sort=added"] = """
            {"results":[
              {"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7101,"status":3}},
              {"id":2,"status":2,"type":"tv","media":{"id":12,"tmdbId":8101,"tvdbId":91,"status":4},"seasons":[{"seasonNumber":2}]},
              {"id":3,"status":5,"type":"movie","media":{"id":13,"tmdbId":7102,"status":5}},
              {"id":4,"status":3,"type":"movie","media":{"id":14,"tmdbId":7103,"status":2}},
              {"id":5,"status":2,"type":"movie","media":{"id":15,"tmdbId":7104,"status":7}}]}
            """;
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7101"] = """[{"id":21,"title":"Film","year":2026,"status":"released","path":"/media/movies/Film (2026)"}]""";
        _arr.Responses[RadarrQueue] = """{"page":1,"pageSize":200,"totalRecords":1,"records":[{"id":5,"movieId":21,"size":100,"sizeleft":25,"status":"downloading"}]}""";
        _arr.Responses["GET //sonarr.test/api/v3/series?tvdbId=91"] = """[{"id":22,"title":"Show","year":2024,"status":"continuing","path":"/media/tv/Show"}]""";

        var all = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"));
        var films = await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/requested?libraryId={movies.Id}"));

        var rows = all.EnumerateArray().ToList();
        Assert.Equal(["Film", "Show"], rows.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal("downloading", rows[0].GetProperty("state").GetString());
        Assert.Equal(0.75, rows[0].GetProperty("progress").GetDouble());
        Assert.Equal("searching", rows[1].GetProperty("state").GetString());
        Assert.Equal([2], rows[1].GetProperty("seasons").EnumerateArray().Select(s => s.GetInt32()));
        Assert.Equal(7101, Assert.Single(films.EnumerateArray().ToList()).GetProperty("tmdbId").GetInt32());
    }
}
