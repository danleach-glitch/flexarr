using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Watchlist;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// The watchlist: adding library titles and ones Seerr knows, where each has got to, titles leaving once watched, and
/// requesting only when the profile asks for it. Each test has a server of its own, with a stand-in Seerr.
/// </summary>
public sealed class WatchlistTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeArr _seerr = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Seerr:BaseUrl", "http://seerr.test");
            b.UseSetting("Flexarr:Seerr:ApiKey", "key");
            b.ConfigureTestServices(s => s.AddHttpClient<SeerrClient>().ConfigurePrimaryHttpMessageHandler(() => _seerr));
        });
        // Placing a request answers with an empty body, as Seerr's does.
        _seerr.Responses["POST /api/v1/request"] = "{}";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _seerr.Dispose();
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task SeedAsync(Action<FlexarrDbContext> seed)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<T> InDbAsync<T>(Func<FlexarrDbContext, Task<T>> query)
    {
        using var scope = _server.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>());
    }

    private static Library Films() => new() { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };

    private static MediaItem Film(Library library, string title, string tmdb) =>
        new() { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant(), Year = 2021, TmdbId = tmdb };

    /// <summary>A show with one episode, so there's something to play.</summary>
    private static (Library Library, MediaItem Show, MediaItem Episode, MediaItem[] All) Show(string? tmdb)
    {
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Severance", SortTitle = "severance", TmdbId = tmdb };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, Parent = show };
        var episode = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Good News About Hell", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1, Parent = season };
        return (library, show, episode, [show, season, episode]);
    }

    private void SeerrKnows(string mediaType, int tmdbId, string title, string date, int? status = null, string[]? streamingInGb = null) =>
        _seerr.Responses[$"GET /api/v1/{mediaType}/{tmdbId}"] = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = tmdbId,
            [mediaType == "movie" ? "title" : "name"] = title,
            [mediaType == "movie" ? "releaseDate" : "firstAirDate"] = date,
            ["posterPath"] = "/poster.jpg",
            ["mediaInfo"] = status is { } s ? new { status = s } : null,
            ["watchProviders"] = streamingInGb is null ? null : new object[]
            {
                new { iso_3166_1 = "US", flatrate = new[] { new { name = "Hulu" } } },
                new { iso_3166_1 = "GB", flatrate = streamingInGb.Select(n => new { name = n }).ToArray() },
            },
        });

    private async Task<JsonElement> ListAsync() => await Client().GetFromJsonAsync<JsonElement>(Api("/watchlist"));

    private int Requests() => _seerr.Requests.Count(r => r == "POST /api/v1/request");

    [Fact]
    public async Task ALibraryFilm_IsReady_OnItsPage_AndComesOff()
    {
        var library = Films();
        var film = Film(library, "Dune", "438631");
        await SeedAsync(db => db.AddRange(library, film));

        var added = await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        var entry = (await ListAsync()).EnumerateArray().Single();
        var page = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{film.Id}"));
        var contains = await Client().GetFromJsonAsync<JsonElement>(Api("/watchlist/contains?mediaType=movie&tmdbId=438631"));
        var removed = await Client().DeleteAsync(Api($"/watchlist?libraryItemId={film.Id}"));

        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        Assert.Equal("Ready", entry.GetProperty("status").GetString());
        Assert.Equal(film.Id, entry.GetProperty("item").GetProperty("id").GetGuid());
        Assert.True(page.GetProperty("onWatchlist").GetBoolean());
        Assert.True(contains.GetProperty("onWatchlist").GetBoolean());
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Empty((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task AddingTwice_KeepsOne_AndAnEpisodeStandsForItsShow()
    {
        var (library, show, episode, all) = Show("95396");
        await SeedAsync(db => { db.Libraries.Add(library); db.MediaItems.AddRange(all); });

        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = show.Id });
        var again = await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = episode.Id });
        var byTmdb = await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "tv", tmdbId = 95396 });

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(HttpStatusCode.OK, byTmdb.StatusCode);
        var entry = (await ListAsync()).EnumerateArray().Single();
        Assert.Equal(("tv", "Ready"), (entry.GetProperty("mediaType").GetString(), entry.GetProperty("status").GetString()));
    }

    [Fact]
    public async Task AFilm_LeavesOnceWatched_AndAShowOnceStarted_ButNotForWatchingBeforeItWentOn()
    {
        var films = Films();
        var film = Film(films, "Arrival", "329865");
        var (tv, show, episode, all) = Show("95396");
        await SeedAsync(db =>
        {
            db.Libraries.AddRange(films, tv);
            db.MediaItems.Add(film);
            db.MediaItems.AddRange(all);
            // Seen years ago: put on the list to watch again, it stays.
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddYears(-2) });
        });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = show.Id });
        Assert.Equal(2, (await ListAsync()).GetArrayLength());

        (await Client().PostAsJsonAsync(Api($"/items/{film.Id}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        // Marking it watched again keeps when it was watched, so play it through instead.
        await SeedAsync(db =>
        {
            var state = db.PlaybackStates.Single(p => p.MediaItemId == film.Id);
            state.LastPlayedAt = DateTime.UtcNow.AddMinutes(1);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = episode.Id, PositionSeconds = 600, LastPlayedAt = DateTime.UtcNow.AddMinutes(1) });
        });

        Assert.Empty((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task TitlesNotInTheLibrary_SayWhereTheyveGotTo()
    {
        SeerrKnows("movie", 1001, "Not Out Yet", DateTime.UtcNow.AddMonths(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        SeerrKnows("movie", 1002, "Being Fetched", "2024-01-01", status: 3);
        SeerrKnows("tv", 1003, "Old Show", "2010-05-01");

        foreach (var (type, id) in new[] { ("movie", 1001), ("movie", 1002), ("tv", 1003) })
        {
            (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = type, tmdbId = id })).EnsureSuccessStatusCode();
        }

        var statuses = (await ListAsync()).EnumerateArray().ToDictionary(e => e.GetProperty("title").GetString()!, e => e.GetProperty("status").GetString());
        Assert.Equal("Coming", statuses["Not Out Yet"]);
        Assert.Equal("OnItsWay", statuses["Being Fetched"]);
        Assert.Equal("NotAvailable", statuses["Old Show"]);
        Assert.Equal(0, Requests());
    }

    [Fact]
    public async Task WithAutoRequestOn_AMissingTitleIsRequested_OnceAndOnlyWhenNobodyHasYet()
    {
        SeerrKnows("movie", 2001, "Wanted", "2023-06-01");
        SeerrKnows("movie", 2002, "Already Asked For", "2023-06-01", status: 2);
        var library = Films();
        var film = Film(library, "Have It", "2003");
        await SeedAsync(db => db.AddRange(library, film));

        var setting = await Client().PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });
        var wanted = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 2001 })).Content.ReadFromJsonAsync<JsonElement>();
        var asked = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 2002 })).Content.ReadFromJsonAsync<JsonElement>();
        var have = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id })).Content.ReadFromJsonAsync<JsonElement>();
        var again = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 2001 })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.True(setting.IsSuccessStatusCode);
        Assert.True((await setting.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("autoRequestWatchlist").GetBoolean());
        Assert.True(wanted.GetProperty("requested").GetBoolean());
        Assert.Equal("OnItsWay", wanted.GetProperty("entry").GetProperty("status").GetString());
        Assert.False(asked.GetProperty("requested").GetBoolean());
        Assert.False(have.GetProperty("requested").GetBoolean());
        Assert.False(again.GetProperty("requested").GetBoolean());
        Assert.Equal(1, Requests());
        Assert.Equal(2001, (await InDbAsync(db => db.MediaRequests.SingleAsync())).TmdbId);
    }

    [Fact]
    public async Task TitlesFromTrakt_AreNeverRequested_AndRemovingDoesntCancel()
    {
        SeerrKnows("movie", 3001, "From Trakt", "2023-06-01");
        SeerrKnows("movie", 3002, "Added Here", "2023-06-01");
        await Client().PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });

        WatchlistAdded trakt;
        using (var scope = _server.Services.CreateScope())
        {
            var watchlist = scope.ServiceProvider.GetRequiredService<WatchlistService>();
            trakt = (await watchlist.AddAsync("owner", new WatchlistAdd(null, "movie", 3001), WatchlistSource.Trakt, CancellationToken.None)).Added!;
        }

        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 3002 });
        var removed = await Client().DeleteAsync(Api("/watchlist?mediaType=movie&tmdbId=3002"));

        Assert.False(trakt.Requested);
        Assert.Equal("trakt", trakt.Entry.Source);
        Assert.Equal(HttpStatusCode.NoContent, removed.StatusCode);
        Assert.Equal(1, Requests()); // only the one added here, and nothing sent to take it back
        Assert.DoesNotContain(_seerr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AProfileThatCantSeeFilms_CantAddOrRequestThem()
    {
        SeerrKnows("movie", 4001, "Hidden Kind", "2023-06-01");
        var library = Films();
        await SeedAsync(db => db.Libraries.Add(library));
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Kid", hiddenLibraryIds = new[] { library.Id } })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        await Client().PutAsJsonAsync(Api($"/profiles/{id}/watchlist-requests"), new { autoRequest = true });
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var kid = Client();
        kid.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        var response = await kid.PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 4001 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal(0, Requests());
    }

    [Fact]
    public async Task OnlyTheAdmin_TurnsOnAutoRequest()
    {
        // A TV on the main profile, locked: it watches, but doesn't manage the server.
        await _factory.SetMainPinAsync("2580");
        var device = (await (await Client().PostAsJsonAsync(Api("/session/device"), new { deviceName = "TV" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var tv = _server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", device);
        await tv.PostAsJsonAsync(Api("/admin/lock"), new { });

        var response = await tv.PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Home_HasARowOfWatchlistTitlesReadyToPlay()
    {
        SeerrKnows("movie", 5001, "Not Here", "2023-06-01");
        var library = Films();
        var film = Film(library, "Here", "5002");
        await SeedAsync(db => db.AddRange(library, film));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 5001 });

        var home = await Client().GetFromJsonAsync<JsonElement>(Api("/home"));

        var row = home.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "watchlist");
        Assert.Equal(film.Id, row.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task SearchMarksTitlesOnTheWatchlist()
    {
        SeerrKnows("movie", 7001, "Wanted", "2023-06-01");
        _seerr.Responses["GET /api/v1/search?query=wanted&page=1"] =
            """{"page":1,"totalPages":1,"results":[{"id":7001,"mediaType":"movie","title":"Wanted"},{"id":7002,"mediaType":"movie","title":"Wanted Too"}]}""";
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 7001 });

        var found = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/search?q=wanted"));

        Assert.Equal([true, false], found.EnumerateArray().Select(t => t.GetProperty("onWatchlist").GetBoolean()));
    }

    [Fact]
    public async Task TakingOff_KeepsATombstone_AndAddingAgainBringsItBack()
    {
        SeerrKnows("movie", 8001, "Back Again", "2023-06-01");
        var first = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8001 })).Content.ReadFromJsonAsync<JsonElement>();
        var id = first.GetProperty("entry").GetProperty("id").GetGuid();

        await Client().DeleteAsync(Api($"/watchlist/{id}"));
        var gone = await ListAsync();
        var tombstone = await InDbAsync(db => db.WatchlistEntries.SingleAsync(w => w.Id == id));
        var again = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8001 })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Empty(gone.EnumerateArray());
        Assert.Equal(WatchlistRemoval.Removed, tombstone.RemovedReason);
        Assert.Equal(id, again.GetProperty("entry").GetProperty("id").GetGuid());
        Assert.Single((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task AddingAFilmWatchedSinceItWentOn_PutsItBackRatherThanFailing()
    {
        var library = Films();
        var film = Film(library, "Rewatch", "8101");
        await SeedAsync(db => db.AddRange(library, film));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        await SeedAsync(db =>
        {
            db.WatchlistEntries.Single().AddedAt = DateTime.UtcNow.AddHours(-2);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddHours(-1) });
        });

        var again = await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        var watched = await InDbAsync(db => db.WatchlistEntries.CountAsync(w => w.RemovedReason == WatchlistRemoval.Watched));

        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(0, watched); // brought back, not left as watched
        Assert.Single((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task MarkingAShowUnwatched_DoesntTakeItOff()
    {
        var (library, show, episode, all) = Show("8201");
        await SeedAsync(db => { db.Libraries.Add(library); db.MediaItems.AddRange(all); });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = show.Id });
        // What "Mark show as unwatched" leaves: no place, not watched, but played just now.
        await SeedAsync(db => db.PlaybackStates.Add(new PlaybackState { MediaItemId = episode.Id, Watched = false, PositionSeconds = 0, LastPlayedAt = DateTime.UtcNow.AddMinutes(1) }));

        Assert.Single((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task TheSameTitleAddedTwoWays_BecomesOneEntryWhenItsIdsMatch()
    {
        var (library, show, _, all) = Show(null!);
        await SeedAsync(db => { db.Libraries.Add(library); db.MediaItems.AddRange(all); });
        SeerrKnows("tv", 8301, "Severance", "2022-02-18");
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = show.Id }); // no TMDB id yet
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "tv", tmdbId = 8301 }); // from Discover
        await SeedAsync(db => db.MediaItems.Single(i => i.Id == show.Id).TmdbId = "8301"); // Fix match

        var list = await Client().GetAsync(Api("/watchlist"));
        var home = await Client().GetAsync(Api("/home"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        var entry = (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single();
        Assert.Equal("Ready", entry.GetProperty("status").GetString());
        // The one that stood took the TMDB id, so Discover still marks it, and adding it again finds it.
        var keys = await Client().GetFromJsonAsync<JsonElement>(Api("/watchlist/keys"));
        Assert.Contains("tv:8301", keys.GetProperty("titles").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal(HttpStatusCode.OK, (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "tv", tmdbId = 8301 })).StatusCode);        Assert.Equal(1, await InDbAsync(db => db.WatchlistEntries.CountAsync(w => w.RemovedAt == null)));
    }

    [Fact]
    public async Task ASeerrThatAnswersWithSomethingElse_StillLetsTheListShow()
    {
        SeerrKnows("movie", 8401, "Fine", "2023-06-01");
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8401 });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8402 }); // Seerr doesn't know it: refused
        // Seerr now answers with a proxy's login page.
        _seerr.Responses["GET /api/v1/movie/8401"] = "<html>Sign in</html>";
        using (var scope = _server.Services.CreateScope())
        {
            ((Microsoft.Extensions.Caching.Memory.MemoryCache)scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()).Clear();
        }

        var list = await Client().GetAsync(Api("/watchlist"));

        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.Equal("NotAvailable", (await list.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray().Single().GetProperty("status").GetString());
    }

    [Fact]
    public async Task TitlesNotInTheLibrary_SayWhereTheyStream()
    {
        _seerr.Responses["GET /api/v1/settings/main"] = """{"streamingRegion":"GB"}""";
        SeerrKnows("movie", 8501, "Streaming", "2014-11-05", streamingInGb: ["Netflix", "Amazon Prime Video", "Amazon Prime Video with Ads", "HBO Max Amazon Channel", "Now TV"]);
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8501 });

        var entry = (await ListAsync()).EnumerateArray().Single();

        Assert.Equal(["Netflix", "Amazon Prime Video", "Now TV"], entry.GetProperty("streaming").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Keys_ListLibraryTitlesAndTmdbIds_WithoutAskingSeerr()
    {
        SeerrKnows("movie", 8601, "Outside", "2023-06-01");
        var library = Films();
        var film = Film(library, "Inside", "8602");
        await SeedAsync(db => db.AddRange(library, film));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8601 });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8602 }); // in the library, by TMDB id
        var asked = _seerr.Requests.Count;

        var keys = await Client().GetFromJsonAsync<JsonElement>(Api("/watchlist/keys"));

        Assert.Equal([film.Id], keys.GetProperty("libraryItemIds").EnumerateArray().Select(e => e.GetGuid()));
        Assert.Equal(["movie:8601", "movie:8602"], keys.GetProperty("titles").EnumerateArray().Select(e => e.GetString()).Order());
        Assert.Equal(asked, _seerr.Requests.Count);
    }

    [Fact]
    public async Task OneProfile_CantSeeOrRemoveAnothersList()
    {
        SeerrKnows("movie", 8701, "Mine", "2023-06-01");
        var added = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 8701 })).Content.ReadFromJsonAsync<JsonElement>();
        var id = added.GetProperty("entry").GetProperty("id").GetGuid();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Other" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var other = Client();
        other.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        var theirs = await other.GetFromJsonAsync<JsonElement>(Api("/watchlist"));
        var remove = await other.DeleteAsync(Api($"/watchlist/{id}"));
        var removeByTitle = await other.DeleteAsync(Api("/watchlist?mediaType=movie&tmdbId=8701"));

        Assert.Empty(theirs.EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, remove.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, removeByTitle.StatusCode);
        Assert.Single((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task AnAssistant_OnlyAddsAnExactMatch_AndOtherwiseOffersCandidates()
    {
        _seerr.Responses["GET /api/v1/search?query=dune&page=1"] =
            """{"page":1,"totalPages":1,"results":[{"id":438631,"mediaType":"movie","title":"Dune","releaseDate":"2021-09-15"},{"id":841,"mediaType":"movie","title":"Dune","releaseDate":"1984-12-14"},{"id":693134,"mediaType":"movie","title":"Dune: Part Two","releaseDate":"2024-02-27"}]}""";
        _seerr.Responses["GET /api/v1/search?query=Dune&page=1"] = _seerr.Responses["GET /api/v1/search?query=dune&page=1"];
        SeerrKnows("movie", 438631, "Dune", "2021-09-15");
        using var scope = _server.Services.CreateScope();
        var services = scope.ServiceProvider;
        Task<string> add(string title, int? year) => Mcp.WatchlistTools.AddAsync(services.GetRequiredService<WatchlistService>(), services.GetRequiredService<SeerrClient>(),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions>>(), services.GetRequiredService<Profiles.CurrentProfile>(),
            CancellationToken.None, title: title, year: year);

        var ambiguous = await Assert.ThrowsAsync<InvalidOperationException>(() => add("dune", null));
        var added = await add("Dune", 2021);

        Assert.Contains("tmdbId 438631", ambiguous.Message, StringComparison.Ordinal);
        Assert.Contains("tmdbId 841", ambiguous.Message, StringComparison.Ordinal);
        Assert.StartsWith("Added Dune (2021)", added, StringComparison.Ordinal);
        Assert.Equal(0, Requests());
    }

    [Fact]
    public async Task ASavedHomeLayout_GetsTheWatchlistRowAfterContinueWatching()
    {
        var saved = new[] { new { id = "continue", enabled = true }, new { id = "top-movies", enabled = true } };
        var layout = await (await Client().PutAsJsonAsync(Api("/settings/layout"), new { home = saved, movies = Array.Empty<object>(), shows = Array.Empty<object>() }))
            .Content.ReadFromJsonAsync<JsonElement>();

        var ids = layout.GetProperty("home").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.Equal(ids.IndexOf("continue") + 1, ids.IndexOf("watchlist"));
    }

    [Fact]
    public async Task AddingBackATitleThatsNowInTheLibrary_IsntAnnouncedAsArriving()
    {
        SeerrKnows("movie", 9001, "Came In", "2023-06-01");
        var first = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9001 })).Content.ReadFromJsonAsync<JsonElement>();
        await Client().DeleteAsync(Api($"/watchlist/{first.GetProperty("entry").GetProperty("id").GetGuid()}"));
        var library = Films();
        var film = Film(library, "Came In", "9001");
        await SeedAsync(db => db.AddRange(library, film));

        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });

        Assert.NotNull((await InDbAsync(db => db.WatchlistEntries.SingleAsync())).ArrivedAt);
    }

    [Fact]
    public async Task AddingBackByTmdbId_ATitleNowInTheLibrary_LinksItAndIsntRequested()
    {
        await Client().PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });
        SeerrKnows("movie", 9011, "Came In", "2023-06-01");
        var first = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9011 })).Content.ReadFromJsonAsync<JsonElement>();
        await Client().DeleteAsync(Api($"/watchlist/{first.GetProperty("entry").GetProperty("id").GetGuid()}"));
        var library = Films();
        var film = Film(library, "Came In", "9011");
        await SeedAsync(db => db.AddRange(library, film));
        _seerr.Requests.Clear();

        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9011 });

        Assert.Equal(film.Id, (await InDbAsync(db => db.WatchlistEntries.SingleAsync())).MediaItemId);
        Assert.Equal(0, Requests());
    }

    [Fact]
    public async Task OneTitleSeerrAnswersBadly_DoesntStopTheOthersSayingWhereTheyAre()
    {
        SeerrKnows("movie", 9021, "Broken", "2023-06-01");
        SeerrKnows("movie", 9022, "Coming", DateTime.UtcNow.AddMonths(3).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9021 });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9022 });
        _seerr.Responses["GET /api/v1/movie/9021"] = "<html>Error</html>";
        using (var scope = _server.Services.CreateScope())
        {
            ((Microsoft.Extensions.Caching.Memory.MemoryCache)scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()).Clear();
        }

        var list = await ListAsync();

        Assert.Equal("Coming", list.EnumerateArray().Single(e => e.GetProperty("tmdbId").GetInt32() == 9022).GetProperty("status").GetString());
    }

    [Fact]
    public async Task AFilmWatchedOffline_LeavesEvenThoughThePlayIsDatedEarlier()
    {
        var library = Films();
        var film = Film(library, "On The Plane", "9031");
        var other = Film(library, "Watched Later", "9032");
        await SeedAsync(db => db.AddRange(library, film, other));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        // Something watched since sets the newest play, then the phone reports one from earlier.
        await SeedAsync(db => db.PlaybackStates.Add(new PlaybackState { MediaItemId = other.Id, Watched = true, LastPlayedAt = DateTime.UtcNow.AddMinutes(1) }));
        Assert.Single((await ListAsync()).EnumerateArray());

        (await Client().PostAsJsonAsync(Api($"/items/{film.Id}/offline-progress"),
            new { positionSeconds = 0, finished = true, playedAt = DateTime.UtcNow.AddSeconds(30) })).EnsureSuccessStatusCode();

        Assert.Empty((await ListAsync()).EnumerateArray());
    }

    [Fact]
    public async Task WhenSeerrDoesntAnswer_NothingIsAutoRequested()
    {
        await Client().PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });
        SeerrKnows("movie", 9101, "Was Known", "2023-06-01");
        var first = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9101 })).Content.ReadFromJsonAsync<JsonElement>();
        await Client().DeleteAsync(Api($"/watchlist/{first.GetProperty("entry").GetProperty("id").GetGuid()}"));
        _seerr.Requests.Clear();
        _seerr.Responses.Clear(); // Seerr gone: every lookup fails
        using (var scope = _server.Services.CreateScope())
        {
            ((Microsoft.Extensions.Caching.Memory.MemoryCache)scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()).Clear();
        }

        var again = await (await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9101 })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.False(again.GetProperty("requested").GetBoolean());
        Assert.DoesNotContain(_seerr.Requests, r => r.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ready_ListsLibraryTitlesToPlay_WithoutAskingSeerr()
    {
        SeerrKnows("movie", 9201, "Outside", "2023-06-01");
        var library = Films();
        var film = Film(library, "Inside", "9202");
        await SeedAsync(db => db.AddRange(library, film));
        await Client().PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = film.Id });
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 9201 });
        var asked = _seerr.Requests.Count;

        var ready = await Client().GetFromJsonAsync<JsonElement>(Api("/watchlist/ready?take=4"));

        Assert.Equal([film.Id], ready.EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(asked, _seerr.Requests.Count);
    }

    [Fact]
    public async Task AnAssistantsBareTmdbId_IsAddedButOnlyRequestedOnceChosen()
    {
        await Client().PutAsJsonAsync(Api("/profiles/owner/watchlist-requests"), new { autoRequest = true });
        SeerrKnows("movie", 9301, "Guessed", "2023-06-01");
        using var scope = _server.Services.CreateScope();
        var services = scope.ServiceProvider;
        Task<string> add(bool chosen) => Mcp.WatchlistTools.AddAsync(services.GetRequiredService<WatchlistService>(), services.GetRequiredService<SeerrClient>(),
            services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions>>(), services.GetRequiredService<Profiles.CurrentProfile>(),
            CancellationToken.None, tmdbId: 9301, kind: "film", chosen: chosen);

        var guessed = await add(false);
        var asked = Requests();
        var confirmed = await add(true);

        Assert.Contains("not requested", guessed, StringComparison.Ordinal);
        Assert.Equal(0, asked);
        Assert.Contains("requested it", confirmed, StringComparison.Ordinal);
        Assert.Equal(1, Requests());
    }

    [Fact]
    public async Task ATitleThatComesIn_IsReady()
    {
        SeerrKnows("movie", 6001, "Arriving", "2023-06-01", status: 3);
        await Client().PostAsJsonAsync(Api("/watchlist"), new { mediaType = "movie", tmdbId = 6001 });
        Assert.Equal("OnItsWay", (await ListAsync()).EnumerateArray().Single().GetProperty("status").GetString());

        var library = Films();
        var film = Film(library, "Arriving", "6001");
        await SeedAsync(db => db.AddRange(library, film));

        var entry = (await ListAsync()).EnumerateArray().Single();
        Assert.Equal("Ready", entry.GetProperty("status").GetString());
        Assert.Equal(film.Id, entry.GetProperty("libraryItemId").GetGuid());
    }
}
