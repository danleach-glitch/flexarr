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
    public async Task AWholeShowWithASeasonOnDisk_ClosesTheRequestThatAlsoAskedForIt()
    {
        _arr.Responses["GET /api/v1/tv/8002"] = """
            {"id":8002,"mediaInfo":{"id":67,"status":4,"tvdbId":82,"requests":[
              {"id":3,"status":2,"type":"tv","seasons":[{"seasonNumber":1},{"seasonNumber":2},{"seasonNumber":3}]}]}}
            """;
        var series = """{"id":6,"title":"Show","seasons":[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true},{"seasonNumber":3,"monitored":true}]}""";
        _arr.Responses["GET //sonarr.test/api/v3/series?tvdbId=82"] = $"[{series}]";
        _arr.Responses["GET //sonarr.test/api/v3/series/6"] = series;
        _arr.Responses["PUT //sonarr.test/api/v3/series/6"] = series;
        _arr.Responses["PUT //sonarr.test/api/v3/episode/monitor"] = "[]";
        _arr.Responses["GET //sonarr.test/api/v3/episode?seriesId=6&includeImages=true"] = """
            [{"id":101,"seasonNumber":1,"episodeNumber":1,"hasFile":true,"monitored":true},
             {"id":201,"seasonNumber":2,"episodeNumber":1,"hasFile":false,"monitored":true},
             {"id":301,"seasonNumber":3,"episodeNumber":1,"hasFile":false,"monitored":true}]
            """;
        Deletes("/api/v1/request/3");
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Series, TmdbId = 8002 });
            return await db.SaveChangesAsync();
        });

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "tv", tmdbId = 8002 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/request/3", _arr.Requests);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE /api/v1/media", StringComparison.Ordinal) || r.StartsWith("DELETE /api/v3/series", StringComparison.Ordinal));
        Assert.True(await InDbAsync(db => db.MediaRequests.AllAsync(r => r.TmdbId != 8002 || r.FulfilledAt != null)));
    }

    /// <summary>Sets up show <paramref name="tmdb"/> in Seerr and Sonarr, and catches what Sonarr is told to monitor.</summary>
    private (Func<Dictionary<int, bool>> Seasons, Func<string?> Episodes) AShow(int tmdb, int seriesId, string requests, string seasons, string episodes)
    {
        _arr.Responses[$"GET /api/v1/tv/{tmdb}"] = $$"""{"id":{{tmdb}},"mediaInfo":{"id":{{tmdb}},"status":4,"tvdbId":{{tmdb}},"requests":""" + requests + "}}";
        var series = $$"""{"id":{{seriesId}},"title":"Show","seasons":{{seasons}}}""";
        _arr.Responses[$"GET //sonarr.test/api/v3/series?tvdbId={tmdb}"] = $"[{series}]";
        _arr.Responses[$"GET //sonarr.test/api/v3/series/{seriesId}"] = series;
        _arr.Responses[$"PUT //sonarr.test/api/v3/series/{seriesId}"] = series;
        _arr.Responses["PUT //sonarr.test/api/v3/episode/monitor"] = "[]";
        _arr.Responses[$"GET //sonarr.test/api/v3/episode?seriesId={seriesId}&includeImages=true"] = episodes;
        string? putSeries = null, putEpisodes = null;
        _arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == $"/api/v3/series/{seriesId}")
            {
                putSeries = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
            else if (r.Method == HttpMethod.Put && r.RequestUri!.AbsolutePath == "/api/v3/episode/monitor")
            {
                putEpisodes = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        };
        return (() => JsonDocument.Parse(putSeries!).RootElement.GetProperty("seasons").EnumerateArray()
            .ToDictionary(s => s.GetProperty("seasonNumber").GetInt32(), s => s.GetProperty("monitored").GetBoolean()), () => putEpisodes);
    }

    [Fact]
    public async Task AWholeShow_OnlyStopsTheSeasonsItsRequestsAskedFor()
    {
        var (seasons, _) = AShow(8003, 7, """[{"id":4,"status":2,"type":"tv","seasons":[{"seasonNumber":2}]}]""",
            """[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true},{"seasonNumber":3,"monitored":true}]""",
            """
            [{"id":101,"seasonNumber":1,"episodeNumber":1,"hasFile":true,"monitored":true},
             {"id":201,"seasonNumber":2,"episodeNumber":1,"hasFile":false,"monitored":true},
             {"id":301,"seasonNumber":3,"episodeNumber":1,"hasFile":false,"monitored":true}]
            """);
        Deletes("/api/v1/request/4");

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "tv", tmdbId = 8003 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/request/4", _arr.Requests);
        Assert.Equal(new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true }, seasons()); // season 3: the admin's own
    }

    [Fact]
    public async Task AWholeShow_StopsAPartlyDownloadedSeason_AndKeepsItsFiles()
    {
        var (seasons, episodes) = AShow(8004, 8, """[{"id":5,"status":2,"type":"tv","seasons":[{"seasonNumber":1},{"seasonNumber":2},{"seasonNumber":3}]}]""",
            """[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true},{"seasonNumber":3,"monitored":true}]""",
            """
            [{"id":101,"seasonNumber":1,"episodeNumber":1,"hasFile":true,"monitored":true,"airDateUtc":"2020-01-01T00:00:00Z"},
             {"id":201,"seasonNumber":2,"episodeNumber":1,"hasFile":true,"monitored":true,"airDateUtc":"2021-01-01T00:00:00Z"},
             {"id":202,"seasonNumber":2,"episodeNumber":2,"hasFile":false,"monitored":true,"airDateUtc":"2021-01-08T00:00:00Z"},
             {"id":301,"seasonNumber":3,"episodeNumber":1,"hasFile":false,"monitored":true,"airDateUtc":"2022-01-01T00:00:00Z"}]
            """);
        Deletes("/api/v1/request/5");
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Series, TmdbId = 8004 });
            return await db.SaveChangesAsync();
        });

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "tv", tmdbId = 8004 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/request/5", _arr.Requests);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE /api/v3/series", StringComparison.Ordinal));
        Assert.Equal(new Dictionary<int, bool> { [1] = true, [2] = false, [3] = false }, seasons());
        var unmonitored = JsonDocument.Parse(episodes()!).RootElement.GetProperty("episodeIds").EnumerateArray().Select(e => e.GetInt32()).Order();
        Assert.Equal([202, 301], unmonitored);
        Assert.True(await InDbAsync(db => db.MediaRequests.AllAsync(r => r.TmdbId != 8004 || r.FulfilledAt != null)));
    }

    [Fact]
    public async Task AWholeShow_KeepsASeasonOnDiskMissingOnlyAnUnmonitoredEpisode_AndStopsOneMissingAnUndatedEpisode()
    {
        var (seasons, episodes) = AShow(8005, 9, """[{"id":6,"status":2,"type":"tv","seasons":[{"seasonNumber":1},{"seasonNumber":2},{"seasonNumber":3}]}]""",
            """[{"seasonNumber":1,"monitored":true},{"seasonNumber":2,"monitored":true},{"seasonNumber":3,"monitored":true}]""",
            """
            [{"id":101,"seasonNumber":1,"episodeNumber":1,"hasFile":true,"monitored":true,"airDateUtc":"2020-01-01T00:00:00Z"},
             {"id":102,"seasonNumber":1,"episodeNumber":2,"hasFile":false,"monitored":false,"airDateUtc":"2020-01-08T00:00:00Z"},
             {"id":201,"seasonNumber":2,"episodeNumber":1,"hasFile":true,"monitored":true},
             {"id":202,"seasonNumber":2,"episodeNumber":2,"hasFile":false,"monitored":true},
             {"id":301,"seasonNumber":3,"episodeNumber":1,"hasFile":true,"monitored":true,"airDateUtc":"2022-01-01T00:00:00Z"},
             {"id":302,"seasonNumber":3,"episodeNumber":2,"hasFile":false,"monitored":true,"airDateUtc":"2999-01-01T00:00:00Z"}]
            """);
        Deletes("/api/v1/request/6");

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "tv", tmdbId = 8005 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/request/6", _arr.Requests);
        Assert.Equal(new Dictionary<int, bool> { [1] = true, [2] = false, [3] = true }, seasons());
        var unmonitored = JsonDocument.Parse(episodes()!).RootElement.GetProperty("episodeIds").EnumerateArray().Select(e => e.GetInt32()).Order();
        Assert.Equal([202], unmonitored);
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
    public async Task AFilmAddedInRadarrByHand_NobodyRequested_IsLeftAlone()
    {
        _arr.Responses["GET /api/v1/movie/7004"] = """{"id":7004,"mediaInfo":{"id":57,"status":3,"requests":[]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7004"] = """[{"id":6,"title":"Film","hasFile":false}]""";
        Deletes("/api/v3/movie/6?deleteFiles=false&addImportExclusion=false", "/api/v1/media/57");

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7004 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal) || r.StartsWith("PUT", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFlexarrRequestSeerrHasLost_IsStillTakenBack()
    {
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7005"] = """[{"id":7,"title":"Film","hasFile":false}]""";
        Deletes("/api/v3/movie/7?deleteFiles=false&addImportExclusion=false");
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 7005 });
            return await db.SaveChangesAsync();
        });

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7005 });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v3/movie/7?deleteFiles=false&addImportExclusion=false", _arr.Requests);
        Assert.True(await InDbAsync(db => db.MediaRequests.AllAsync(r => r.TmdbId != 7005 || r.FulfilledAt != null)));
    }

    [Fact]
    public async Task ATimeout_SaysWhoIsntAnswering()
    {
        _arr.OnRequest = r =>
        {
            if (r.RequestUri!.Host == "seerr.test")
            {
                throw new TaskCanceledException("timed out", new TimeoutException());
            }
        };

        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7006 });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Seerr isn’t answering", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABadMediaType_IsRefused()
    {
        var response = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "album", tmdbId = 1 });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ARequestHeadedForALibraryHiddenFromAProfile_IsntThatProfilesToUndo()
    {
        // The test server maps /data (as Radarr and Seerr see it) to /Volumes/Media/data.
        var family = new Library { Name = "Family films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/Volumes/Media/data/family" }], CreatedAt = DateTime.UtcNow.AddDays(-2) };
        var parents = new Library { Name = "Parents' films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/Volumes/Media/data/parents" }] };
        await InDbAsync(async db =>
        {
            db.Libraries.AddRange(family, parents);
            db.MediaRequests.Add(new MediaRequest { ProfileId = PlaybackState.DefaultUser, Kind = MediaKind.Movie, TmdbId = 7201 });
            return await db.SaveChangesAsync();
        });
        // In Radarr under the parents' folder; not in Radarr yet, but Seerr sent it there; and one for the family's.
        _arr.Responses["GET /api/v1/movie/7201"] = """{"id":7201,"mediaInfo":{"id":61,"status":3,"requests":[{"id":21,"status":2,"type":"movie"}]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7201"] = """[{"id":31,"title":"Theirs","hasFile":false,"path":"/data/parents/Theirs (2026)"}]""";
        _arr.Responses["GET /api/v1/movie/7202"] = """{"id":7202,"mediaInfo":{"id":62,"status":2,"requests":[{"id":22,"status":1,"type":"movie","rootFolder":"/data/parents"}]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7202"] = "[]";
        _arr.Responses["GET /api/v1/movie/7203"] = """{"id":7203,"mediaInfo":{"id":63,"status":3,"requests":[{"id":23,"status":2,"type":"movie"}]}}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7203"] = """[{"id":33,"title":"Everyone's","hasFile":false,"path":"/data/family/Everyone's (2026)"}]""";
        Deletes("/api/v3/movie/31?deleteFiles=false&addImportExclusion=false", "/api/v1/request/21", "/api/v1/media/61", "/api/v1/request/22", "/api/v1/media/62",
            "/api/v3/movie/33?deleteFiles=false&addImportExclusion=false", "/api/v1/request/23", "/api/v1/media/63");
        var id = (await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Teen", hiddenLibraryIds = new[] { parents.Id } })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api("/session/device"), new { deviceName = "Teen's phone" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var teen = _server.CreateClient();
        teen.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.True((await teen.PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).IsSuccessStatusCode);

        // Answered as though there were nothing to take back, and nothing is touched.
        var theirs = await teen.PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7201 });
        var sentThere = await teen.PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7202 });

        Assert.Equal(HttpStatusCode.NoContent, theirs.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, sentThere.StatusCode);
        Assert.DoesNotContain(_arr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
        Assert.True(await InDbAsync(db => db.MediaRequests.AnyAsync(r => r.TmdbId == 7201 && r.FulfilledAt == null)));

        // One headed for a library it sees is its to undo, as before; and the main profile undoes any.
        var everyones = await teen.PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7203 });
        Assert.Equal(HttpStatusCode.NoContent, everyones.StatusCode);
        Assert.Equal(["DELETE /api/v3/movie/33?deleteFiles=false&addImportExclusion=false", "DELETE /api/v1/request/23", "DELETE /api/v1/media/63"],
            _arr.Requests.Where(r => r.StartsWith("DELETE", StringComparison.Ordinal)));
        var mine = await Client().PostAsJsonAsync(Api("/requests/unrequest"), new { mediaType = "movie", tmdbId = 7201 });
        Assert.Equal(HttpStatusCode.NoContent, mine.StatusCode);
        Assert.Contains("DELETE /api/v3/movie/31?deleteFiles=false&addImportExclusion=false", _arr.Requests);
        Assert.Contains("DELETE /api/v1/request/21", _arr.Requests);
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
        // Seerr's own filters: only open requests come back (one twice), and an old Seerr without "failed".
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=pending&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=processing&sort=added"] = """
            {"results":[
              {"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7101,"status":3}},
              {"id":2,"status":2,"type":"tv","media":{"id":12,"tmdbId":8101,"tvdbId":91,"status":4},"seasons":[{"seasonNumber":2}]},
              {"id":5,"status":2,"type":"movie","media":{"id":15,"tmdbId":7104,"status":7}},
              {"id":6,"status":2,"type":"movie","media":{"id":16,"tmdbId":7105,"status":3}},
              {"id":7,"status":2,"type":"movie","media":{"id":17,"tmdbId":7106,"status":3}},
              {"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7101,"status":3}}]}
            """;
        _arr.OnRequest = r =>
        {
            if (r.RequestUri!.Query.Contains("filter=failed", StringComparison.Ordinal))
            {
                throw new HttpRequestException("Bad Request", null, HttpStatusCode.BadRequest);
            }
        };
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7101"] = """[{"id":21,"title":"Film","year":2026,"status":"released","path":"/media/movies/Film (2026)"}]""";
        _arr.Responses[RadarrQueue] = """{"page":1,"pageSize":200,"totalRecords":1,"records":[{"id":5,"movieId":21,"size":100,"sizeleft":25,"status":"downloading"}]}""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7105"] = "[]"; // taken out of Radarr since: nobody's looking
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7106"] = """[{"id":23,"title":"Here","hasFile":true}]"""; // in, the library just hasn't caught up
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

    private void SeerrIsWaitingFor(params string[] requests)
    {
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=pending&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=failed&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=processing&sort=added"] = $$"""{"results":[{{string.Join(',', requests)}}]}""";
    }

    [Fact]
    public async Task Requested_LeavesOutAFilmThatsIn_AndPlacesEachTitleInItsLibrary()
    {
        var films = new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] };
        var films4k = new Library { Name = "Films 4K", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies4k" }], CreatedAt = DateTime.UtcNow.AddMinutes(1) };
        var shows = new Library { Name = "Shows", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = "/media/tv" }] };
        var anime = new Library { Name = "Anime", Type = LibraryType.TvShows, Folders = [new LibraryFolder { Path = "/media/anime" }], CreatedAt = DateTime.UtcNow.AddMinutes(1) };
        // A film that's in whatever Seerr thinks, a show that's partly in (in the second shows library), and a film
        // elsewhere with the same TMDB id as that show.
        var here = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Here", SortTitle = "here", TmdbId = "7401" };
        var partly = new MediaItem { LibraryId = anime.Id, Kind = MediaKind.Series, Title = "Partly", SortTitle = "partly", TmdbId = "8401" };
        var sameNumber = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Same Number", SortTitle = "same number", TmdbId = "8401" };
        await InDbAsync(async db =>
        {
            db.AddRange(films, films4k, shows, anime, here, partly, sameNumber);
            return await db.SaveChangesAsync();
        });
        SeerrIsWaitingFor(
            """{"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7401,"status":3}}""",
            """{"id":2,"status":2,"type":"tv","media":{"id":12,"tmdbId":8401,"tvdbId":95,"status":4},"seasons":[{"seasonNumber":3}]}""",
            """{"id":3,"status":2,"type":"movie","media":{"id":13,"tmdbId":7402,"status":3}}""",
            """{"id":4,"status":1,"type":"movie","media":{"id":14,"tmdbId":7403,"status":2},"rootFolder":"/media/movies4k"}""");
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7401"] = """[{"id":31,"title":"Here","year":2020,"status":"released","path":"/media/movies/Here (2020)"}]""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7402"] = """[{"id":32,"title":"In 4K","year":2026,"status":"released","path":"/media/movies4k/In 4K (2026)"}]""";
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7403"] = "[]"; // waiting to be approved, so not in Radarr yet: Seerr names it, and says where it'll go
        _arr.Responses["GET /api/v1/movie/7403"] = """{"id":7403,"title":"Only Seerr Knows","releaseDate":"2027-02-01","posterPath":"/s.jpg"}""";
        _arr.Responses["GET //sonarr.test/api/v3/series?tvdbId=95"] = """[{"id":33,"title":"Partly","year":2022,"status":"continuing","path":"/media/tv/Partly"}]""";

        List<(string Type, int TmdbId, string Title, Guid? Item)> Rows(JsonElement list) =>
            [.. list.EnumerateArray().Select(r => (r.GetProperty("mediaType").GetString()!, r.GetProperty("tmdbId").GetInt32(), r.GetProperty("title").GetString()!,
                r.GetProperty("libraryItemId").ValueKind == JsonValueKind.Null ? (Guid?)null : r.GetProperty("libraryItemId").GetGuid()))];
        var all = Rows(await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested")));
        async Task<List<int>> InAsync(Library library) =>
            [.. Rows(await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/requested?libraryId={library.Id}"))).Select(r => r.TmdbId)];

        Assert.Equal([("movie", 7402, "In 4K", null), ("movie", 7403, "Only Seerr Knows", null), ("tv", 8401, "Partly", partly.Id)], all);
        Assert.Empty(await InAsync(films));
        Assert.Equal([7402, 7403], await InAsync(films4k)); // by Radarr's folder, and by the folder Seerr sent it to
        Assert.Empty(await InAsync(shows)); // Sonarr's folder is here, but the show itself is in the other library
        Assert.Equal([8401], await InAsync(anime));
    }

    [Fact]
    public async Task Requested_AsksRadarrAboutAFewTitlesAtATime_NotOneAfterAnother_AndNotAllAtOnce()
    {
        await InDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] });
            return await db.SaveChangesAsync();
        });
        var films = Enumerable.Range(7501, 10).ToList();
        SeerrIsWaitingFor([.. films.Select(tmdb => $$$"""{"id":{{{tmdb}}},"status":2,"type":"movie","media":{"id":{{{tmdb}}},"tmdbId":{{{tmdb}}},"status":3}}""")]);
        foreach (var tmdb in films)
        {
            _arr.Responses[$"GET //radarr.test/api/v3/movie?tmdbId={tmdb}"] = $$"""[{"id":{{tmdb}},"title":"Film {{tmdb}}","year":2026,"status":"released","path":"/media/movies/Film {{tmdb}}"}]""";
        }

        // Radarr answers nothing about a film until it has been asked about four: asked one after another, the first
        // would never be answered. It then waits a moment for any more, to see how many it's asked about at once.
        var asking = 0;
        var most = 0;
        var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _arr.Before = async (request, ct) =>
        {
            if (!request.RequestUri!.Query.StartsWith("?tmdbId=", StringComparison.Ordinal))
            {
                return;
            }

            var now = Interlocked.Increment(ref asking);
            InterlockedMax(ref most, now);
            if (now == 4)
            {
                await Task.Delay(300, ct);
                four.TrySetResult();
            }

            await four.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            Interlocked.Decrement(ref asking);
        };

        var rows = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray().ToList();

        Assert.Equal(films.Select(tmdb => $"Film {tmdb}"), rows.Select(r => r.GetProperty("title").GetString()));
        Assert.All(rows, r => Assert.Equal("searching", r.GetProperty("state").GetString()));
        Assert.Equal(4, most);
        Assert.All(films, tmdb => Assert.Single(_arr.Requests, r => r == $"GET /api/v3/movie?tmdbId={tmdb}"));

        static void InterlockedMax(ref int target, int value)
        {
            int seen;
            while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
            {
            }
        }
    }

    [Fact]
    public async Task Requested_LooksUpOnlyTheRequestedTitlesInTheLibrary_NotEveryFilmAndShow()
    {
        var films = new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] };
        await InDbAsync(async db =>
        {
            db.AddRange(films, new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Here", SortTitle = "here", TmdbId = "7601" });
            return await db.SaveChangesAsync();
        });
        SeerrIsWaitingFor("""{"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7602,"status":3}}""");
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7602"] = """[{"id":41,"title":"Coming","year":2026,"status":"released","path":"/media/movies/Coming (2026)"}]""";
        var sql = new Statements();
        using var server = _server.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<FlexarrDbContext>(o => o.AddInterceptors(sql))));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var rows = (await client.GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray().ToList();

        Assert.Equal("Coming", Assert.Single(rows).GetProperty("title").GetString());
        // Both readings of titles by TMDB id (what the profile sees, and what's anywhere) name the ids they want.
        var lookups = sql.Run.Where(statement => statement.Contains("\"m\".\"TmdbId\" IS NOT NULL", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, lookups.Count);
        Assert.All(lookups, statement => Assert.Matches("""
            "m"\."TmdbId" (IN \(|= )@
            """.Trim(), statement));
    }

    /// <summary>Every statement the server sent the database.</summary>
    private sealed class Statements : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> Run { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Run.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    [Fact]
    public async Task Requested_AsksARadarrThatDoesntAnswer_OnlyOnce_EvenAcrossRefreshes()
    {
        await InDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] });
            return await db.SaveChangesAsync();
        });
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=pending&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=failed&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=processing&sort=added"] = """
            {"results":[
              {"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7201,"status":3}},
              {"id":2,"status":2,"type":"movie","media":{"id":12,"tmdbId":7202,"status":3}},
              {"id":3,"status":2,"type":"movie","media":{"id":13,"tmdbId":7203,"status":3}}]}
            """;
        foreach (var tmdb in new[] { 7201, 7202, 7203 })
        {
            _arr.Responses[$"GET /api/v1/movie/{tmdb}"] = $$"""{"id":{{tmdb}},"title":"Film {{tmdb}}","releaseDate":"2026-01-01"}""";
        }

        _arr.OnRequest = r =>
        {
            if (r.RequestUri!.Host == "radarr.test")
            {
                throw new HttpRequestException("Connection refused (radarr.test:7878)");
            }
        };

        var rows = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray().ToList();
        var refreshed = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray().ToList();

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal("searching", r.GetProperty("state").GetString()));
        Assert.Equal(3, refreshed.Count);
        Assert.Single(_arr.Requests, r => r.StartsWith("GET /api/v3/", StringComparison.Ordinal)); // the refresh didn't wait on it again
    }

    [Fact]
    public async Task Requested_KeepsWhatRadarrSaidOfATitle_UntilARadarrWebhookArrives()
    {
        await InDbAsync(async db =>
        {
            db.Libraries.Add(new Library { Name = "Films", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = "/media/movies" }] });
            return await db.SaveChangesAsync();
        });
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=pending&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=failed&sort=added"] = """{"results":[]}""";
        _arr.Responses["GET /api/v1/request?take=100&skip=0&filter=processing&sort=added"] = """
            {"results":[{"id":1,"status":2,"type":"movie","media":{"id":11,"tmdbId":7401,"status":3}}]}
            """;
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7401"] = """[{"id":3,"title":"Film","year":2025,"hasFile":false,"status":"released"}]""";
        int Asked() => _arr.Requests.Count(r => r == "GET /api/v3/movie?tmdbId=7401");

        var first = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray().ToList();
        Assert.Equal("searching", Assert.Single(first).GetProperty("state").GetString());
        Assert.Equal(1, Asked());

        // Seerr's list and the queue are kept only half a minute; once they've gone, the title's answer is still kept.
        var cache = (Microsoft.Extensions.Caching.Memory.MemoryCache)_server.Services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
        foreach (var key in cache.Keys.OfType<string>().Where(k => k.StartsWith("seerr-requested:", StringComparison.Ordinal) || k.StartsWith("requested-queue:", StringComparison.Ordinal)).ToList())
        {
            cache.Remove(key);
        }

        Assert.Single((await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray());
        Assert.Equal(1, Asked());

        // Radarr says the file has come: it's asked again, and the film is no longer waiting.
        _arr.Responses["GET //radarr.test/api/v3/movie?tmdbId=7401"] = """[{"id":3,"title":"Film","year":2025,"hasFile":true,"status":"released"}]""";
        (await Client().PostAsJsonAsync(Api("/webhooks/radarr"), new { eventType = "Grab" })).EnsureSuccessStatusCode();
        Assert.Empty((await Client().GetFromJsonAsync<JsonElement>(Api("/requests/requested"))).EnumerateArray());
        Assert.Equal(2, Asked());
    }
}
