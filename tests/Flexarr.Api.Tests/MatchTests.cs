using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Fix match, and the matching it corrects, against a fake Sonarr.</summary>
public sealed class MatchTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    // The 2024 show and the 1980 one that a search for "Shogun" finds first.
    private static readonly JsonObject Shogun2024 = Series(392573, "Shōgun", 2024);
    private static readonly JsonObject Shogun1980 = Series(80284, "Shogun", 1980);

    [Fact]
    public async Task ShowFoundBySearch_IsMatchedToSonarrsShow_WhenSonarrHasItsFolder()
    {
        // macOS spells the folder with a decomposed "ō"; Sonarr stores it composed.
        var sonarr = new FakeSonarr();
        sonarr.Library.Add(Managed(Shogun2024, 7, "/data/tv/Shōgun"));
        var app = WithSonarr(sonarr);
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shōgun", wrong: true);

        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = await db.Libraries.FirstAsync(l => db.MediaItems.Any(i => i.Id == id && i.LibraryId == l.Id));
            await scope.ServiceProvider.GetRequiredService<MetadataRefresher>().RefreshLibraryAsync(library, force: false, CancellationToken.None);
        }

        var item = await ItemAsync(app, id);
        Assert.Equal(("Shōgun", 2024, "392573", 7), (item.Title, item.Year, item.TvdbId, item.ArrId));
    }

    [Fact]
    public async Task FixMatch_AddsTheShowToSonarr_UnmonitoredWithNoSearch()
    {
        var sonarr = new FakeSonarr();
        var app = WithSonarr(sonarr);
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: true);

        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative), new { id = 392573 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var added = Assert.Single(sonarr.Added);
        Assert.Equal("/data/tv/Shogun", added["path"]!.GetValue<string>());
        Assert.Equal(392573, added["tvdbId"]!.GetValue<int>());
        Assert.False(added["monitored"]!.GetValue<bool>());
        Assert.False(added["addOptions"]!["searchForMissingEpisodes"]!.GetValue<bool>());
        Assert.Equal("none", added["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.Equal(3, added["qualityProfileId"]!.GetValue<int>());

        var item = await ItemAsync(app, id);
        Assert.Equal(("Shōgun", 2024, "392573", true), (item.Title, item.Year, item.TvdbId, item.MatchLocked));
        Assert.NotNull(item.ArrId);
        Assert.Null(item.ImdbId); // Nothing of the wrong match is kept.
    }

    [Fact]
    public async Task FixMatch_LinksAShowSonarrAlreadyHas_WithoutAddingIt()
    {
        var sonarr = new FakeSonarr();
        sonarr.Library.Add(Managed(Shogun2024, 9, "/data/tv/Shōgun (2024)"));
        var app = WithSonarr(sonarr);
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: true);

        var candidates = await Client(app).GetFromJsonAsync<JsonArray>(new Uri($"/api/v1/items/{id}/match?q=shogun", UriKind.Relative));
        var listed = candidates!.Select(c => (c!["id"]!.GetValue<int>(), c["current"]!.GetValue<bool>(), c["managedBy"]?.GetValue<string>())).ToList();
        Assert.Equal([(80284, true, null), (392573, false, "Sonarr")], listed);
        // Posters go through the server's signed artwork links: unsigned ones are turned away.
        var poster = candidates![1]!["poster"]!.GetValue<string>();
        Assert.Equal(ImageEndpoints.RemoteUrl("https://artworks.thetvdb.com/banners/posters/392573-1.jpg"), poster);
        Assert.Contains("&s=", poster, StringComparison.Ordinal);

        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative), new { id = 392573 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(sonarr.Added);
        var item = await ItemAsync(app, id);
        Assert.Equal(("Shōgun", 9, true), (item.Title, item.ArrId, item.MatchLocked));
    }

    [Fact]
    public async Task FixMatch_RefusesWhenSonarrHasTheFolderAsAnotherShow()
    {
        var sonarr = new FakeSonarr();
        sonarr.Library.Add(Managed(Shogun1980, 4, "/data/tv/Shogun"));
        var app = WithSonarr(sonarr);
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: false);

        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative), new { id = 392573 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Shogun (1980)", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Empty(sonarr.Added);
        Assert.False((await ItemAsync(app, id)).MatchLocked);
    }

    [Fact]
    public async Task FixedMatch_SurvivesARefresh_EvenWhenTheFolderSaysOtherwise()
    {
        // Sonarr has the folder as the 1980 show, and the 2024 one elsewhere; the hand-picked match wins.
        var sonarr = new FakeSonarr();
        sonarr.Library.Add(Managed(Shogun1980, 4, "/data/tv/Shogun"));
        sonarr.Library.Add(Managed(Shogun2024, 9, "/data/tv/Shōgun (2024)"));
        var app = WithSonarr(sonarr);
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: false);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var item = await db.MediaItems.FirstAsync(i => i.Id == id);
            (item.TvdbId, item.MatchLocked) = ("392573", true);
            await db.SaveChangesAsync();
            var library = await db.Libraries.FirstAsync(l => l.Id == item.LibraryId);
            await scope.ServiceProvider.GetRequiredService<MetadataRefresher>().RefreshLibraryAsync(library, force: true, CancellationToken.None);
        }

        var refreshed = await ItemAsync(app, id);
        Assert.Equal(("Shōgun", 9), (refreshed.Title, refreshed.ArrId));
    }

    [Fact]
    public async Task Ignore_KeepsTheMatchASearchFound_AndTakesItOffTheList()
    {
        var app = WithSonarr(new FakeSonarr());
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: true);
        var before = await Client(app).GetFromJsonAsync<JsonObject>(new Uri("/api/v1/library-health", UriKind.Relative));

        var response = await Client(app).PostAsync(new Uri($"/api/v1/items/{id}/match/keep", UriKind.Relative), null);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var after = await Client(app).GetFromJsonAsync<JsonObject>(new Uri("/api/v1/library-health", UriKind.Relative));
        Assert.Contains(before!["matchedByTitle"]!.AsArray(), i => i!["id"]!.GetValue<Guid>() == id);
        Assert.DoesNotContain(after!["matchedByTitle"]!.AsArray(), i => i!["id"]!.GetValue<Guid>() == id);
        var item = await ItemAsync(app, id);
        Assert.Equal(("Shogun", "80284", true), (item.Title, item.TvdbId, item.MatchLocked));
    }

    [Fact]
    public async Task FixMatch_IsForAdminsOnly()
    {
        var app = WithSonarr(new FakeSonarr());
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: true);

        var response = await app.CreateClient().PostAsJsonAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative), new { id = 392573 });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task WithoutSonarr_FixMatch_ChoosesFromTvMaze_AndKeepsTheChoice()
    {
        var tvmaze = new FakeTvMaze();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(services =>
        {
            services.AddHttpClient<TvMazeClient>().ConfigurePrimaryHttpMessageHandler(() => tvmaze);
            services.AddHttpClient<ImageCache>().ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
        }));
        var id = await AddShowAsync(app, "Shogun", "/Volumes/Media/data/tv/Shogun", wrong: false);

        var candidates = await Client(app).GetFromJsonAsync<List<MatchCandidateDto>>(new Uri($"/api/v1/items/{id}/match?q=Shogun", UriKind.Relative));
        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative), new { id = 60600 });

        Assert.Equal([(60600, "Shōgun", 2024, "TVmaze"), (1980, "Shogun", 1980, "TVmaze")], candidates!.Select(c => (c.Id, c.Title, c.Year!.Value, c.Source)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = await ItemAsync(app, id);
        Assert.Equal(("Shōgun", 2024, "60600", true, MetadataRefresher.TvMaze), (item.Title, item.Year, item.TvMazeId, item.MatchLocked, item.MetadataSource));
    }

    [Fact]
    public async Task WithoutRadarr_FixingAFilm_NeedsATmdbKey()
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Heat", SortTitle = "HEAT", Path = "/Volumes/Media/data/films/Heat" };
            db.AddRange(library, film);
            await db.SaveChangesAsync();
            id = film.Id;
        }

        var response = await Client(factory).GetAsync(new Uri($"/api/v1/items/{id}/match", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("TMDB key", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private WebApplicationFactory<Program> WithSonarr(FakeSonarr sonarr) => factory.WithWebHostBuilder(b =>
    {
        b.UseSetting("Flexarr:Arr:0:Name", "Sonarr");
        b.UseSetting("Flexarr:Arr:0:Kind", "Sonarr");
        b.UseSetting("Flexarr:Arr:0:BaseUrl", FakeSonarr.BaseUrl);
        b.UseSetting("Flexarr:Arr:0:ApiKey", "sonarr-key");
        b.ConfigureServices(services =>
        {
            services.AddHttpClient<ArrClient>().ConfigurePrimaryHttpMessageHandler(() => sonarr);
            // TVmaze and artwork are out of reach in tests.
            services.AddHttpClient<TvMazeClient>().ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
            services.AddHttpClient<ImageCache>().ConfigurePrimaryHttpMessageHandler(() => new NotFoundHandler());
        });
    });

    private static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A show in its own library; "wrong" gives it the 1980 show's details, as a title search did.</summary>
    private static async Task<Guid> AddShowAsync(WebApplicationFactory<Program> app, string title, string path, bool wrong)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var show = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Series, Title = title, SortTitle = title.ToUpperInvariant(), Path = path,
            Year = wrong ? 1980 : null, TvdbId = wrong ? "80284" : null, ImdbId = wrong ? "tt0080274" : null,
            MetadataSource = wrong ? "Sonarr lookup" : null, MetadataUpdatedAt = DateTime.UtcNow.AddDays(-2), PosterUrl = "https://artworks.thetvdb.com/1980.jpg",
        };
        db.AddRange(library, show);
        await db.SaveChangesAsync();
        return show.Id;
    }

    private static async Task<MediaItem> ItemAsync(WebApplicationFactory<Program> app, Guid id)
    {
        using var scope = app.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AsNoTracking().FirstAsync(i => i.Id == id);
    }

    private static JsonObject Series(int tvdbId, string title, int year) => new()
    {
        ["tvdbId"] = tvdbId, ["title"] = title, ["year"] = year, ["overview"] = $"{title} ({year})", ["network"] = "FX",
        ["images"] = new JsonArray(new JsonObject { ["coverType"] = "poster", ["remoteUrl"] = $"https://artworks.thetvdb.com/banners/posters/{tvdbId}-1.jpg" }),
        ["seasons"] = new JsonArray(), ["genres"] = new JsonArray(),
    };

    private static JsonObject Managed(JsonObject series, int id, string path)
    {
        var copy = series.DeepClone().AsObject();
        copy["id"] = id;
        copy["path"] = path;
        copy["qualityProfileId"] = 3;
        return copy;
    }

    /// <summary>TVmaze's search for "Shogun", and the 2024 show.</summary>
    private sealed class FakeTvMaze : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.RequestUri!.PathAndQuery switch
            {
                "/search/shows?q=Shogun" => """[{"score":0.9,"show":{"id":60600,"name":"Shōgun","premiered":"2024-02-27"}},{"score":0.8,"show":{"id":1980,"name":"Shogun","premiered":"1980-09-15"}}]""",
                "/shows/60600" => """{"id":60600,"name":"Shōgun","premiered":"2024-02-27","summary":"<p>Japan, 1600.</p>"}""",
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class NotFoundHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    /// <summary>Just enough of Sonarr's API: its library, search, adding a show, and episodes (all on disk).</summary>
    private sealed class FakeSonarr : HttpMessageHandler
    {
        public const string BaseUrl = "http://sonarr.test";

        public List<JsonObject> Library { get; } = [];

        public List<JsonObject> Added { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            JsonNode? body = (request.Method.Method, path) switch
            {
                ("GET", "/api/v3/series") => new JsonArray([.. Library.Select(s => s.DeepClone())]),
                ("GET", "/api/v3/series/lookup") => new JsonArray([.. Lookup(Uri.UnescapeDataString(request.RequestUri.Query)).Select(s => s.DeepClone())]),
                ("GET", "/api/v3/episode") => new JsonArray(new JsonObject { ["id"] = 1, ["seasonNumber"] = 1, ["episodeNumber"] = 1, ["hasFile"] = true, ["images"] = new JsonArray() }),
                ("GET", "/api/v3/qualityprofile") => new JsonArray(new JsonObject { ["id"] = 3, ["name"] = "HD" }),
                ("GET", "/api/v3/rootfolder") => new JsonArray(new JsonObject { ["id"] = 1, ["path"] = "/data/tv" }),
                ("POST", "/api/v3/series") => await AddAsync(request, cancellationToken),
                _ => null,
            };
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json") };
        }

        private static IEnumerable<JsonObject> Lookup(string query) =>
            query.Contains("tvdb:392573", StringComparison.Ordinal) ? [Shogun2024]
            : query.Contains("tvdb:80284", StringComparison.Ordinal) ? [Shogun1980]
            : [Shogun1980, Shogun2024];

        private async Task<JsonNode> AddAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var added = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!.AsObject();
            Added.Add(added);
            var series = Managed(added["tvdbId"]!.GetValue<int>() == 392573 ? Shogun2024 : Shogun1980, 100 + Library.Count, added["path"]!.GetValue<string>());
            Library.Add(series);
            return series.DeepClone();
        }
    }
}
