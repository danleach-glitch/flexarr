using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class SubtitleSearchTests : IClassFixture<FlexarrApiFactory>
{
    private readonly FlexarrApiFactory factory;

    public SubtitleSearchTests(FlexarrApiFactory factory)
    {
        this.factory = factory;
        // Bazarr's queued work is checked on quickly, and given up on soon.
        Flexarr.Api.Playback.SubtitleSearchEndpoints.PollEvery = TimeSpan.FromMilliseconds(20);
        Flexarr.Api.Playback.SubtitleSearchEndpoints.DownloadWait = TimeSpan.FromSeconds(5);
    }

    private const string Results = """
        {"data":[
          {"provider":"opensubtitlescom","subtitle":"a1","language":"en","hearing_impaired":"True","forced":"False","original_format":"False",
           "score":100,"release_info":["Film.2020.1080p.BluRay-GRP"],"matches":["hash","hearing_impaired"],"uploader":"someone"},
          {"provider":"podnapisi","subtitle":"b2","language":"en","hearing_impaired":"False","forced":"False","original_format":"False",
           "score":91.5,"release_info":["Film (2020) WEB"],"matches":["title"],"uploader":null}
        ]}
        """;

    [Fact]
    public async Task WithoutBazarr_SubtitleSearchIsNotOffered()
    {
        var client = Client(factory);

        var status = await client.GetFromJsonAsync<Status>(new Uri("/api/v1/subtitles/status", UriKind.Relative));

        Assert.False(status!.Configured);
    }

    [Fact]
    public async Task Searching_GivesATitleWithoutLanguagesBazarrsFirstProfile_AndListsWhatProvidersOffer()
    {
        var bazarr = new FakeBazarr();
        var (film, _) = await AddFilmAsync(radarrId: 7);
        bazarr.Film(7, "/data/movies/Film (2020)/Film (2020).mkv", profileId: null);
        bazarr.Responses["GET /api/system/languages/profiles"] = """[{"profileId":4,"name":"French"},{"profileId":2,"name":"English"}]""";
        bazarr.Responses["POST /api/movies"] = "";
        bazarr.Responses["GET /api/providers/movies?radarrid=7"] = Results;
        using var app = bazarr.Serve(factory);

        var search = await Client(app).GetFromJsonAsync<Search>(new Uri($"/api/v1/items/{film}/subtitles/search", UriKind.Relative));

        Assert.Equal("radarrid=7&profileid=2", bazarr.Forms["POST /api/movies"].Single());
        Assert.Equal(2, search!.Results.Count);
        Assert.Equal(("English (SDH)", "Film.2020.1080p.BluRay-GRP", true, 100), (search.Results[0].LanguageLabel, search.Results[0].Release, search.Results[0].MatchesFile, search.Results[0].Score));
        Assert.Equal(("English", false, 92), (search.Results[1].LanguageLabel, search.Results[1].MatchesFile, search.Results[1].Score));
    }

    [Fact]
    public async Task ThePickedResult_IsDownloaded_AndComesBackAsTheTrackItWillBe()
    {
        var bazarr = new FakeBazarr();
        var (film, video) = await AddFilmAsync(radarrId: 8);
        bazarr.Film(8, "/data/movies/Film (2020)/Film (2020).mkv", profileId: 1);
        bazarr.Responses["GET /api/providers/movies?radarrid=8"] = Results;
        bazarr.Responses["POST /api/providers/movies"] = "";
        bazarr.OnRequest = r => { if (r == "POST /api/providers/movies") File.WriteAllText(Path.ChangeExtension(video, ".en.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHi\n"); };
        using var app = bazarr.Serve(factory);
        var client = Client(app);
        var search = await client.GetFromJsonAsync<Search>(new Uri($"/api/v1/items/{film}/subtitles/search", UriKind.Relative));

        var response = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{film}/subtitles/download", UriKind.Relative), new { result = search!.Results[1].Id });
        var saved = await response.Content.ReadFromJsonAsync<Downloaded>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("radarrid=8&hi=False&forced=False&original_format=False&provider=podnapisi&subtitle=b2", bazarr.Forms["POST /api/providers/movies"].Single());
        Assert.Equal(("x0", "English"), (saved!.Key, saved.Label));
    }

    [Fact]
    public async Task LettingFlexarrPick_PrefersPlainSubtitles_OverHearingImpairedOnes()
    {
        var bazarr = new FakeBazarr();
        var (film, video) = await AddFilmAsync(radarrId: 9);
        bazarr.Film(9, "/data/movies/Film (2020)/Film (2020).mkv", profileId: 1);
        bazarr.Responses["GET /api/providers/movies?radarrid=9"] = Results;
        bazarr.Responses["POST /api/providers/movies"] = "";
        bazarr.OnRequest = r => { if (r == "POST /api/providers/movies") File.WriteAllText(Path.ChangeExtension(video, ".en.srt"), ""); };
        using var app = bazarr.Serve(factory);

        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{film}/subtitles/download", UriKind.Relative), new { });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("provider=podnapisi&subtitle=b2", bazarr.Forms["POST /api/providers/movies"].Single(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADownloadBazarrQueuedButCouldntDo_SaysSo_AsSoonAsItsJobEnds()
    {
        const string earlier = """{"job_id":4,"job_name":"Manually downloading Subtitles for Other (1999)","status":"completed"}""";
        var bazarr = new FakeBazarr();
        var (film, _) = await AddFilmAsync(radarrId: 12);
        bazarr.Film(12, "/data/movies/Film (2020)/Film (2020).mkv", profileId: 1);
        bazarr.Responses["GET /api/providers/movies?radarrid=12"] = Results;
        bazarr.Responses["GET /api/system/jobs"] = $$"""{"data":[{{earlier}}]}""";
        bazarr.Responses["POST /api/providers/movies"] = "";
        // Bazarr answers at once, and its job finishes later without a file (the site refused, say).
        bazarr.OnRequest = r =>
        {
            if (r == "POST /api/providers/movies")
            {
                bazarr.Responses["GET /api/system/jobs"] =
                    $$"""{"data":[{{earlier}},{"job_id":5,"job_name":"Manually downloading Subtitles for Film (2020)","status":"completed"}]}""";
            }
        };
        using var app = bazarr.Serve(factory);
        var started = DateTime.UtcNow;

        var response = await Client(app).PostAsJsonAsync(new Uri($"/api/v1/items/{film}/subtitles/download", UriKind.Relative), new { });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("didn't save the subtitles", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(4)); // not the whole wait
    }

    [Fact]
    public async Task AnotherCopyThanBazarrs_IsRefused_SinceItsSubtitlesWouldLandElsewhere()
    {
        var bazarr = new FakeBazarr();
        var (film, _) = await AddFilmAsync(radarrId: 10);
        bazarr.Film(10, "/data/movies/Film (2020)/Film (2020) Remux.mkv", profileId: 1);
        using var app = bazarr.Serve(factory);

        var response = await Client(app).GetAsync(new Uri($"/api/v1/items/{film}/subtitles/search", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Film (2020) Remux.mkv", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.DoesNotContain(bazarr.Requests, r => r.StartsWith("GET /api/providers", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATitleRadarrDoesntManage_IsRefused()
    {
        var bazarr = new FakeBazarr();
        var (film, _) = await AddFilmAsync(radarrId: null);
        using var app = bazarr.Serve(factory);

        var response = await Client(app).GetAsync(new Uri($"/api/v1/items/{film}/subtitles/search", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Empty(bazarr.Requests);
    }

    [Fact]
    public async Task ReSyncing_RunsInTheBackground_OnTheFileAsBazarrSeesIt()
    {
        var bazarr = new FakeBazarr();
        var (film, video) = await AddFilmAsync(radarrId: 11);
        File.WriteAllText(Path.ChangeExtension(video, ".en.srt"), "");
        bazarr.Film(11, "/data/movies/Film (2020)/Film (2020).mkv", profileId: 1);
        bazarr.Responses["PATCH /api/subtitles"] = "";
        var release = new TaskCompletionSource();
        bazarr.Hold = r => r == "PATCH /api/subtitles" ? release.Task : Task.CompletedTask;
        // Re-synced: Bazarr writes the file again.
        bazarr.OnRequest = r => { if (r == "PATCH /api/subtitles") File.WriteAllText(Path.ChangeExtension(video, ".en.srt"), "re-timed"); };
        using var app = bazarr.Serve(factory);
        var client = Client(app);

        var started = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{film}/subtitles/sync", UriKind.Relative), new { key = "x0" });
        var again = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{film}/subtitles/sync", UriKind.Relative), new { key = "x0" });
        release.SetResult();
        Sync? state = null;
        for (var i = 0; i < 50 && state?.State != "done"; i++)
        {
            await Task.Delay(20);
            state = await client.GetFromJsonAsync<Sync>(new Uri($"/api/v1/items/{film}/subtitles/sync", UriKind.Relative));
        }

        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        Assert.Equal(("done", "x0"), (state!.State, state.Key));
        Assert.Equal(
            "action=sync&language=en&path=%2Fdata%2Fmovies%2FFilm+%282020%29%2FFilm+%282020%29.en.srt&type=movie&id=11&forced=False&hi=False",
            bazarr.Forms["PATCH /api/subtitles"].Single());
    }

    private static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A Radarr film with one file, "Film (2020).mkv", in a folder of its own.</summary>
    private async Task<(Guid Film, string Video)> AddFilmAsync(int? radarrId)
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, Guid.NewGuid().ToString("N"), "Film (2020)")).FullName;
        var video = Path.Combine(folder, "Film (2020).mkv");
        File.WriteAllText(video, "video");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var movie = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", ArrId = radarrId,
            Files = [new MediaFile { Path = video, Size = 5, Height = 1080 }],
        };
        db.AddRange(library, movie);
        await db.SaveChangesAsync();
        return (movie.Id, video);
    }

    private sealed record Status(bool Configured, bool Reachable);

    private sealed record Result(string Id, string Provider, string LanguageLabel, string Release, bool HearingImpaired, int Score, bool MatchesFile);

    private sealed record Search(List<Result> Results);

    private sealed record Downloaded(string Key, string Label);

    private sealed record Sync(string State, string? Key, string? Error);

    /// <summary>Stands in for Bazarr: answers by "METHOD /api/path?query" and records the forms it was sent.</summary>
    private sealed class FakeBazarr : HttpMessageHandler
    {
        public ConcurrentDictionary<string, string> Responses { get; } = new();

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentDictionary<string, ConcurrentQueue<string>> Forms { get; } = new();

        public Action<string>? OnRequest { get; set; }

        /// <summary>Holds a request until the task finishes.</summary>
        public Func<string, Task> Hold { get; set; } = _ => Task.CompletedTask;

        public void Film(int radarrId, string path, int? profileId) =>
            Responses[$"GET /api/movies?radarrid[]={radarrId}"] =
                $$"""{"data":[{"radarrId":{{radarrId}},"path":"{{path}}","profileId":{{(profileId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}},"subtitles":[]}]}""";

        public WebApplicationFactory<Program> Serve(FlexarrApiFactory factory) => factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Flexarr:Bazarr:BaseUrl", "http://bazarr.test");
            builder.UseSetting("Flexarr:Bazarr:ApiKey", "key");
            builder.ConfigureTestServices(services => services.AddHttpClient<BazarrClient>().ConfigurePrimaryHttpMessageHandler(() => this));
        });

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var key = $"{request.Method} {Uri.UnescapeDataString(request.RequestUri!.PathAndQuery)}";
            Requests.Enqueue(key);
            if (request.Content is { } content)
            {
                Forms.GetOrAdd(key, _ => new ConcurrentQueue<string>()).Enqueue(await content.ReadAsStringAsync(cancellationToken));
            }

            await Hold(key);
            OnRequest?.Invoke(key);
            return Responses.TryGetValue(key, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
