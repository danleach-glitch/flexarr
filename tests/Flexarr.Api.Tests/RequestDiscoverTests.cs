using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// Run alone: a request or an unrequest anywhere in the process drops what's kept of Seerr's answers
/// (<see cref="Flexarr.Api.Endpoints.RequestRequested.Forget"/> is shared), so other tests' requests would make the
/// counts here wobble.
/// </summary>
[CollectionDefinition(nameof(RequestDiscoverTests), DisableParallelization = true)]
public sealed class RequestDiscoverCollection;

/// <summary>Discover, "search everything" and an outside title's page, against a stand-in Seerr: what they show, and what they ask Seerr and the database for.</summary>
[Collection(nameof(RequestDiscoverTests))]
public sealed class RequestDiscoverTests : IAsyncLifetime
{
    private const string Trending = "GET /api/v1/discover/trending?page=1";
    private const string Movies = "GET /api/v1/discover/movies?page=1";
    private const string Shows = "GET /api/v1/discover/tv?page=1*";
    private const string Upcoming = "GET /api/v1/discover/movies/upcoming?page=1";

    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeSeerr _seerr = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Seerr:BaseUrl", "http://seerr.test");
            b.UseSetting("Flexarr:Seerr:ApiKey", "key");
            b.ConfigureTestServices(s => s.AddHttpClient<SeerrClient>().ConfigurePrimaryHttpMessageHandler(() => _seerr));
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _seerr.Dispose();
    }

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static string Page(params string[] results) => $$"""{"page":1,"totalPages":1,"results":[{{string.Join(',', results)}}]}""";

    private static string Title(string mediaType, int id, string title, int? status = null) =>
        $$"""{"id":{{id}},"mediaType":"{{mediaType}}","{{(mediaType == "movie" ? "title" : "name")}}":"{{title}}","{{(mediaType == "movie" ? "releaseDate" : "firstAirDate")}}":"2025-03-01","posterPath":"/p{{id}}.jpg","voteAverage":7.26{{(status is { } s ? $$""","mediaInfo":{"status":{{s}}}""" : "")}}}""";

    /// <summary>Films in a library of their own, each with a TMDB id.</summary>
    private async Task<List<Guid>> AddFilmsAsync(params (string Title, string TmdbId)[] films)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var items = films.Select(f => new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = f.Title, SortTitle = f.Title.ToLowerInvariant(), TmdbId = f.TmdbId }).ToList();
        db.Add(library);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return [.. items.Select(i => i.Id)];
    }

    private static List<(int TmdbId, string Title, string Availability, Guid? LibraryItemId)> Items(JsonElement row) =>
        [.. row.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("tmdbId").GetInt32(), i.GetProperty("title").GetString()!,
            i.GetProperty("availability").GetString()!, i.GetProperty("libraryItemId").ValueKind == JsonValueKind.Null ? (Guid?)null : i.GetProperty("libraryItemId").GetGuid()))];

    private void SeerrLists()
    {
        _seerr.Responses[Trending] = Page(Title("movie", 101, "Here Already"), Title("tv", 201, "Asked For", status: 3), """{"id":999,"mediaType":"person","name":"Someone"}""");
        _seerr.Responses[Movies] = Page(Title("movie", 102, "Seerr Says It's In", status: 5), Title("movie", 103, "Not Yet"));
        _seerr.Responses[Upcoming] = Page(Title("movie", 104, "Next Year"));
    }

    [Fact]
    public async Task Discover_IsSeerrsLists_InOrder_MarkedWithWhatsInTheLibrary_AndAListThatFailsIsLeftOut()
    {
        var here = (await AddFilmsAsync(("Here Already", "101")))[0];
        SeerrLists(); // Seerr answers 404 for the new shows: that row is left out, the others stay.

        var rows = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();

        Assert.Equal(["trending", "movies", "upcoming"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(["Trending this week", "Popular movies", "Coming to cinemas"], rows.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal([(101, "Here Already", "Available", here), (201, "Asked For", "Requested", null)], Items(rows[0]));
        Assert.Equal([(102, "Seerr Says It's In", "Available", null), (103, "Not Yet", "None", null)], Items(rows[1]));
        Assert.Equal([(104, "Next Year", "None", null)], Items(rows[2]));
        var first = rows[0].GetProperty("items")[0];
        Assert.Equal((2025, 7.3, false), (first.GetProperty("year").GetInt32(), first.GetProperty("rating").GetDouble(), first.GetProperty("onWatchlist").GetBoolean()));
        Assert.Contains("p101.jpg", first.GetProperty("poster").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discover_AsksSeerrForItsListsOnce_ForVisitsCloseTogether_ButMarksThemAfreshEachTime()
    {
        SeerrLists();
        _seerr.Responses[Shows] = Page(Title("tv", 202, "New Show"));
        _seerr.Responses["POST /api/v1/request"] = "{}";

        var before = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();
        // Between two visits a film arrives in the library and another goes on the watchlist.
        var arrived = (await AddFilmsAsync(("Not Yet", "103")))[0];
        _seerr.Responses["GET /api/v1/movie/104"] = Title("movie", 104, "Next Year");
        (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 104 })).EnsureSuccessStatusCode();
        var after = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();

        Assert.Equal(["trending", "movies", "tv", "upcoming"], after.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal((103, "Not Yet", "None", null), Items(before[1])[1]);
        Assert.Equal((103, "Not Yet", "Available", arrived), Items(after[1])[1]);
        Assert.False(before[3].GetProperty("items")[0].GetProperty("onWatchlist").GetBoolean());
        Assert.True(after[3].GetProperty("items")[0].GetProperty("onWatchlist").GetBoolean());
        Assert.Equal(4, _seerr.Requests.Count(r => r.StartsWith("GET /api/v1/discover/", StringComparison.Ordinal)));

        // A request changes what Seerr says of a title (it's now asked for), so the lists are fetched again after one.
        _seerr.Responses[Movies] = Page(Title("movie", 102, "Seerr Says It's In", status: 5), Title("movie", 105, "Just Asked For", status: 3));
        (await Client().PostAsJsonAsync(Api("/requests"), new { mediaType = "movie", tmdbId = 105 })).EnsureSuccessStatusCode();
        var requested = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();

        Assert.Equal((105, "Just Asked For", "Requested", null), Items(requested[1])[1]);
        Assert.Equal(8, _seerr.Requests.Count(r => r.StartsWith("GET /api/v1/discover/", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Discover_AsksAgainNextTime_ForAListSeerrDidntAnswer()
    {
        SeerrLists();

        await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"));
        _seerr.Responses[Shows] = Page(Title("tv", 202, "New Show"));
        var rows = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();

        Assert.Equal(["trending", "movies", "tv", "upcoming"], rows.Select(r => r.GetProperty("id").GetString()));
        Assert.Equal(2, _seerr.Requests.Count(r => r.StartsWith("GET /api/v1/discover/tv?", StringComparison.Ordinal)));
        Assert.Equal(1, _seerr.Requests.Count(r => r == Trending));
    }

    [Fact]
    public async Task Discover_AsksSeerrForItsFourListsTogether()
    {
        SeerrLists();
        _seerr.Responses[Shows] = Page(Title("tv", 202, "New Show"));
        // Seerr answers none of the lists until it has been asked for all four: asked one after another, the first
        // would never be answered.
        var asked = 0;
        var all = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _seerr.Before = async (request, ct) =>
        {
            if (request.RequestUri!.AbsolutePath.StartsWith("/api/v1/discover/", StringComparison.Ordinal))
            {
                if (Interlocked.Increment(ref asked) == 4)
                {
                    all.TrySetResult();
                }

                await all.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            }
        };

        var rows = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/discover"))).EnumerateArray().ToList();

        Assert.Equal(["trending", "movies", "tv", "upcoming"], rows.Select(r => r.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task SearchEverything_MarksWhatsInTheLibrary_ByKindAndTmdbId_TheFirstCopyWhenThereAreTwo()
    {
        // The same film in two libraries (the first added is the one marked), and a film whose TMDB id a show shares.
        var first = (await AddFilmsAsync(("Twice", "301"), ("Film 302", "302")))[0];
        await AddFilmsAsync(("Twice", "301"));
        _seerr.Responses["GET /api/v1/search?query=twice&page=1"] = Page(Title("movie", 301, "Twice"), Title("tv", 302, "Show 302"), Title("movie", 303, "Elsewhere", status: 2),
            """{"id":301,"mediaType":"person","name":"Someone"}""");

        var found = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/search?q=twice"));

        Assert.Equal([(301, "Twice", "Available", first), (302, "Show 302", "None", null), (303, "Elsewhere", "Requested", null)],
            found.EnumerateArray().Select(i => (i.GetProperty("tmdbId").GetInt32(), i.GetProperty("title").GetString()!, i.GetProperty("availability").GetString()!,
                i.GetProperty("libraryItemId").ValueKind == JsonValueKind.Null ? (Guid?)null : i.GetProperty("libraryItemId").GetGuid())));
    }

    [Fact]
    public async Task AnOutsideTitlesPage_SaysWhetherItsHere_AndMarksItsRecommendations()
    {
        var films = await AddFilmsAsync(("Here", "401"), ("Also Here", "402"));
        foreach (var id in new[] { 401, 403 })
        {
            _seerr.Responses[$"GET /api/v1/movie/{id}"] = Title("movie", id, id == 401 ? "Here" : "Not Here");
            _seerr.Responses[$"GET /api/v1/movie/{id}/recommendations"] = Page(Title("movie", 402, "Also Here"), Title("movie", 404, "Nor This", status: 3), Title("tv", 402, "A Show"));
        }

        var here = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/title/movie/401"));
        var notHere = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/title/movie/403"));

        Assert.Equal(("Here", "Available", films[0]), (here.GetProperty("title").GetString(), here.GetProperty("availability").GetString(), here.GetProperty("libraryItemId").GetGuid()));
        Assert.Equal(("Not Here", "None", JsonValueKind.Null), (notHere.GetProperty("title").GetString(), notHere.GetProperty("availability").GetString(), notHere.GetProperty("libraryItemId").ValueKind));
        foreach (var page in new[] { here, notHere })
        {
            Assert.Equal([(402, "Also Here", "Available", films[1]), (404, "Nor This", "Requested", null), (402, "A Show", "None", null)],
                page.GetProperty("recommendations").EnumerateArray().Select(i => (i.GetProperty("tmdbId").GetInt32(), i.GetProperty("title").GetString()!,
                    i.GetProperty("availability").GetString()!, i.GetProperty("libraryItemId").ValueKind == JsonValueKind.Null ? (Guid?)null : i.GetProperty("libraryItemId").GetGuid())));
        }
    }

    [Fact]
    public async Task MarkingWhatsInTheLibrary_LooksUpOnlyTheTitlesOnThePage_NotEveryFilmAndShow()
    {
        await AddFilmsAsync(("Here Already", "101"), ("Twice", "301"), ("Here", "401"));
        SeerrLists();
        _seerr.Responses["GET /api/v1/search?query=twice&page=1"] = Page(Title("movie", 301, "Twice"));
        _seerr.Responses["GET /api/v1/movie/401"] = Title("movie", 401, "Here");
        _seerr.Responses["GET /api/v1/movie/401/recommendations"] = Page(Title("movie", 101, "Here Already"));
        var sql = new Statements();
        using var server = _server.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<FlexarrDbContext>(o => o.AddInterceptors(sql))));
        var client = server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        foreach (var path in new[] { "/requests/discover", "/requests/search?q=twice", "/requests/title/movie/401" })
        {
            sql.Run.Clear();
            (await client.GetAsync(Api(path))).EnsureSuccessStatusCode();

            // Whatever reads titles by their TMDB id names the ids it wants, which SQLite finds by index: no statement
            // collects every film and show with one.
            var lookups = sql.Run.Where(statement => statement.Contains("\"m\".\"TmdbId\" IS NOT NULL", StringComparison.Ordinal)).ToList();
            Assert.NotEmpty(lookups);
            Assert.All(lookups, statement => Assert.Matches("""
                "m"\."TmdbId" (IN \(|= )@
                """.Trim(), statement));
        }
    }

    [Fact]
    public async Task WhetherRequestsAreSetUp_IsRememberedForAWhile_NotAskedOfSeerrEveryTime()
    {
        _seerr.Responses["GET /api/v1/settings/main"] = "{}";

        var first = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/status"));
        var again = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/status"));

        Assert.Equal((true, true), (first.GetProperty("configured").GetBoolean(), first.GetProperty("reachable").GetBoolean()));
        Assert.Equal((true, true), (again.GetProperty("configured").GetBoolean(), again.GetProperty("reachable").GetBoolean()));
        Assert.Equal(1, _seerr.Requests.Count(r => r == "GET /api/v1/settings/main"));
    }

    [Fact]
    public async Task ASeerrThatRefusesOrIsntThere_IsNotReachable()
    {
        // No answer set up for the ping: Seerr says 404, as one with the wrong address would.
        var status = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/status"));

        Assert.Equal((true, false), (status.GetProperty("configured").GetBoolean(), status.GetProperty("reachable").GetBoolean()));
    }

    [Fact]
    public async Task ASeerrThatDoesntAnswer_IsNotReachable_AfterFiveSeconds_AndIsntWaitedOnAgainStraightAway()
    {
        // Its machine is off or asleep: the connection just hangs.
        _seerr.Before = (_, ct) => Task.Delay(Timeout.Infinite, ct);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        var response = await Client().GetAsync(Api("/requests/status"));
        var waited = clock.Elapsed;
        var status = await response.Content.ReadFromJsonAsync<JsonElement>();
        clock.Restart();
        var again = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/status"));

        // It used to hang for the HTTP client's fifteen seconds and then fail with a server error.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal((true, false), (status.GetProperty("configured").GetBoolean(), status.GetProperty("reachable").GetBoolean()));
        Assert.InRange(waited, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(12));
        Assert.False(again.GetProperty("reachable").GetBoolean());
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"the second ask took {clock.Elapsed}");
        Assert.Equal(1, _seerr.Requests.Count(r => r == "GET /api/v1/settings/main"));
    }

    /// <summary>Every statement the server sent the database.</summary>
    private sealed class Statements : Microsoft.EntityFrameworkCore.Diagnostics.DbCommandInterceptor
    {
        public ConcurrentQueue<string> Run { get; } = new();

        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData eventData,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Run.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>Stands in for Seerr: answers by "METHOD /path?query" (a key ending in * answers anything that starts with it), 404 otherwise.</summary>
    private sealed class FakeSeerr : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string> Responses { get; } = new();

        public ConcurrentQueue<string> Requests { get; } = new();

        /// <summary>Runs as each request arrives, before it's answered.</summary>
        public Func<HttpRequestMessage, CancellationToken, Task>? Before { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {request.RequestUri!.PathAndQuery}";
            Requests.Enqueue(key);
            if (Before is { } before)
            {
                await before(request, cancellationToken);
            }

            var body = Responses.TryGetValue(key, out var exact) ? exact
                : Responses.FirstOrDefault(r => r.Key.EndsWith('*') && key.StartsWith(r.Key[..^1], StringComparison.Ordinal)).Value;
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
