using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A show's page: the seasons and episodes Sonarr (or Seerr) knows that the library doesn't, and what's coming.</summary>
public sealed class ShowMissingTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

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
        _arr.Responses["POST /api/v1/request"] = "{}";
        _arr.Responses["PUT /api/v3/episode/monitor"] = "[]";
        _arr.Responses["POST /api/v3/command"] = "{}";
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _arr.Dispose();
    }

    private static SonarrEpisode Ep(int season, int episode, DateTime? aired, bool hasFile = false, bool monitored = false) =>
        new() { Id = season * 100 + episode, SeasonNumber = season, EpisodeNumber = episode, Title = $"S{season}E{episode}", AirDateUtc = aired, HasFile = hasFile, Monitored = monitored };

    private static ShowMissingEndpoints.Have Have(params (int Season, int Episode)[] episodes) =>
        new(episodes.ToHashSet(), episodes.Select(e => e.Season).ToHashSet());

    [Fact]
    public void SeasonsWithoutAPoster_GetTheShows()
    {
        var show = new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Title = "Show", SortTitle = "Show", PosterUrl = "https://example.test/poster.jpg" };
        IReadOnlyList<MissingSeasonDto> seasons =
        [
            new(2, null, 10, 2021, null, AvailabilityDto.None),
            new(3, null, 8, 2023, "/api/v1/images/remote?url=own", AvailabilityDto.None),
        ];

        var filled = ShowMissingEndpoints.WithShowPoster(seasons, show);

        Assert.StartsWith($"/api/v1/images/items/{show.Id}/poster", filled[0].Poster);
        Assert.Equal("/api/v1/images/remote?url=own", filled[1].Poster);
        Assert.Null(ShowMissingEndpoints.WithShowPoster(seasons, new MediaItem { Id = Guid.NewGuid(), Kind = MediaKind.Series, Title = "Bare", SortTitle = "Bare" })[0].Poster);
    }

    [Fact]
    public void WholeSeasons_AndPartlyHadSeasonsEpisodes_AreMissing_WithCounts()
    {
        var aired = Now.AddYears(-1);
        IReadOnlyList<SonarrEpisode> episodes =
        [
            Ep(1, 1, aired), Ep(1, 2, aired), Ep(1, 3, aired),
            Ep(2, 1, aired), Ep(2, 2, aired, monitored: true),
            Ep(3, 1, aired, hasFile: true), // Sonarr has it: on its way in
        ];

        var gaps = ShowMissingEndpoints.Gaps(episodes, [new SonarrSeason(2, true, null)], Have((1, 1), (1, 3)), Now);

        var episode = Assert.Single(gaps.Episodes);
        Assert.Equal((1, 2), (episode.SeasonNumber, episode.EpisodeNumber));
        var season = Assert.Single(gaps.Seasons);
        Assert.Equal((2, 2, AvailabilityDto.Requested), (season.SeasonNumber, season.Episodes, season.Availability));
        Assert.Equal([new SeasonCountDto(1, 2, 3), new SeasonCountDto(3, 1, 1)], gaps.Counts);
    }

    [Fact]
    public void Specials_AndUndatedEpisodes_AreLeftOut_AndTheNextFiveAreUpcoming()
    {
        IReadOnlyList<SonarrEpisode> episodes =
        [
            Ep(0, 1, Now.AddYears(-2)), Ep(1, 1, Now.AddDays(-7)), Ep(1, 9, null),
            .. Enumerable.Range(2, 7).Select(e => Ep(1, e, Now.AddDays(e))),
        ];

        var gaps = ShowMissingEndpoints.Gaps(episodes, [], Have((1, 1)), Now);

        Assert.Empty(gaps.Seasons);
        Assert.Empty(gaps.Episodes);
        Assert.Equal([2, 3, 4, 5, 6], gaps.Upcoming.Select(u => u.EpisodeNumber));
        Assert.Equal(new SeasonCountDto(1, 1, 1), Assert.Single(gaps.Counts));
    }

    [Fact]
    public void SeerrSeasons_ThatHaveAired_AndArentHad_AreMissing()
    {
        var title = JsonDocument.Parse("""
            {"seasons": [
              {"seasonNumber": 0, "airDate": "2019-01-01", "episodeCount": 3},
              {"seasonNumber": 1, "airDate": "2019-02-01", "episodeCount": 8},
              {"seasonNumber": 2, "airDate": "2021-02-01", "episodeCount": 10, "name": "Season 2", "posterPath": "/s2.jpg"},
              {"seasonNumber": 3, "airDate": "2030-02-01", "episodeCount": 10},
              {"seasonNumber": 4, "airDate": null, "episodeCount": 0}
            ], "mediaInfo": {"seasons": [{"seasonNumber": 2, "status": 3}]}}
            """).RootElement;

        var missing = ShowMissingEndpoints.SeerrSeasons(title, new HashSet<int> { 1 }, DateOnly.FromDateTime(Now));

        var season = Assert.Single(missing);
        Assert.Equal((2, "Season 2", 10, 2021, AvailabilityDto.Requested), (season.SeasonNumber, season.Name, season.Episodes, season.Year, season.Availability));
        Assert.NotNull(season.Poster);
    }

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private async Task<MediaItem> SeedShowAsync(string tmdb, int arrId)
    {
        var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Gaps", SortTitle = "gaps", TmdbId = tmdb, MetadataSource = "Sonarr", ArrId = arrId };
        var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, Parent = show };
        var episode = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "One", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1, Parent = season };
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.AddRange(library, show, season, episode);
        await db.SaveChangesAsync();
        return show;
    }

    private void SonarrHas(int seriesId, params SonarrEpisode[] episodes)
    {
        _arr.Responses[$"GET /api/v3/series/{seriesId}"] = JsonSerializer.Serialize(new { id = seriesId, title = "Gaps", seasons = new[] { new { seasonNumber = 1, monitored = true } } });
        _arr.Responses[$"GET /api/v3/episode?seriesId={seriesId}&includeImages=true"] = JsonSerializer.Serialize(episodes, Web);
    }

    [Fact]
    public async Task AShowsPage_ListsWhatSonarrHasThatTheLibraryDoesnt_AndSearchAsksSonarrForIt()
    {
        var aired = DateTime.UtcNow.AddYears(-1);
        var show = await SeedShowAsync("9001", 41);
        SonarrHas(41, Ep(1, 1, aired), Ep(1, 2, aired), Ep(1, 3, aired), Ep(2, 1, aired), Ep(2, 2, DateTime.UtcNow.AddDays(3)));

        var gaps = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{show.Id}/missing"));
        var search = await Client().PostAsJsonAsync(Api($"/items/{show.Id}/missing/search"), new { seasonNumber = 1 });
        var nothing = await Client().PostAsJsonAsync(Api($"/items/{show.Id}/missing/search"), new { seasonNumber = 3 });

        Assert.Equal("sonarr", gaps.GetProperty("source").GetString());
        Assert.True(gaps.GetProperty("canRequest").GetBoolean());
        Assert.True(gaps.GetProperty("canSearch").GetBoolean());
        Assert.Equal([2, 3], gaps.GetProperty("episodes").EnumerateArray().Select(e => e.GetProperty("episodeNumber").GetInt32()));
        Assert.Equal(2, gaps.GetProperty("seasons").EnumerateArray().Single().GetProperty("seasonNumber").GetInt32());
        Assert.Equal(2, gaps.GetProperty("upcoming").EnumerateArray().Single().GetProperty("seasonNumber").GetInt32());
        Assert.Equal(HttpStatusCode.Accepted, search.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, nothing.StatusCode);
        Assert.Contains("POST /api/v3/command", _arr.Requests);
    }

    [Fact]
    public async Task ASeasonRequest_SendsSeerrJustThoseSeasons()
    {
        _arr.Responses["GET /api/v1/tv/9002"] = """{"id": 9002, "name": "Gaps", "externalIds": {"tvdbId": 42}}""";
        string? body = null;
        _arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/request")
            {
                body = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        };

        var sent = await Client().PostAsJsonAsync(Api("/requests"), new { mediaType = "tv", tmdbId = 9002, seasons = new[] { 3, 2, 0 } });

        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        Assert.Equal([2, 3], JsonDocument.Parse(body!).RootElement.GetProperty("seasons").EnumerateArray().Select(s => s.GetInt32()));
    }

    [Fact]
    public async Task WithNoSonarrAnswer_SeerrsSeasonsStandIn()
    {
        var show = await SeedShowAsync("9003", 43);
        _arr.Responses["GET /api/v1/tv/9003"] = """{"id": 9003, "seasons": [{"seasonNumber": 1, "airDate": "2019-01-01", "episodeCount": 8}, {"seasonNumber": 2, "airDate": "2020-01-01", "episodeCount": 8}]}""";

        var gaps = await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{show.Id}/missing"));

        Assert.Equal("seerr", gaps.GetProperty("source").GetString());
        Assert.False(gaps.GetProperty("canSearch").GetBoolean());
        Assert.Equal(2, gaps.GetProperty("seasons").EnumerateArray().Single().GetProperty("seasonNumber").GetInt32());
    }
}
