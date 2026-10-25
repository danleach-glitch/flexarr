using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flexarr.Api.Podcasts;
using Flexarr.Core.Data;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for podcast hosts and Apple's directory: answers by full URL, with ETags and ranges.</summary>
public sealed class FakePodcastHost : HttpMessageHandler
{
    public ConcurrentDictionary<string, (byte[] Body, string Type)> Files { get; } = new();

    /// <summary>Redirects by URL: (status, where to).</summary>
    public ConcurrentDictionary<string, (int Status, string Location)> Redirects { get; } = new();

    public ConcurrentQueue<HttpRequestMessage> Requests { get; } = new();

    public void Feed(string url, string xml) => Files[url] = (Encoding.UTF8.GetBytes(xml), "application/rss+xml");

    public void Json(string url, string json) => Files[url] = (Encoding.UTF8.GetBytes(json), "application/json");

    public static string ETag(byte[] body) => $"\"{Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body))[..16]}\"";

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Enqueue(request);
        var url = request.RequestUri!.AbsoluteUri;
        if (Redirects.TryGetValue(url, out var redirect))
        {
            var moved = new HttpResponseMessage((HttpStatusCode)redirect.Status);
            moved.Headers.Location = new Uri(redirect.Location);
            return Task.FromResult(moved);
        }

        if (!Files.TryGetValue(url, out var file))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        var etag = ETag(file.Body);
        if (request.Headers.TryGetValues("If-None-Match", out var match) && match.Contains(etag))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));
        }

        HttpResponseMessage response;
        if (request.Headers.Range?.Ranges.SingleOrDefault() is { From: { } from } range)
        {
            var to = Math.Min(range.To ?? file.Body.Length - 1, file.Body.Length - 1);
            response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent(file.Body[(int)from..((int)to + 1)]) };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, file.Body.Length);
        }
        else
        {
            response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(file.Body) };
        }

        response.Headers.ETag = new EntityTagHeaderValue(etag);
        response.Headers.AcceptRanges.Add("bytes");
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(file.Type);
        return Task.FromResult(response);
    }
}

public sealed class PodcastTests : IDisposable
{
    private const string FeedUrl = "https://feeds.example.com/history";

    private readonly FlexarrApiFactory _factory = new();
    private readonly FakePodcastHost _host = new();
    private readonly WebApplicationFactory<Program> _server;

    public PodcastTests()
    {
        _server = _factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddHttpClient<PodcastFetcher>().ConfigurePrimaryHttpMessageHandler(() => _host);
            services.AddHttpClient<PodcastDirectory>().ConfigurePrimaryHttpMessageHandler(() => _host);
        }));
        _host.Feed(FeedUrl, Feed(3));
        for (var i = 1; i <= 4; i++)
        {
            _host.Files[$"https://cdn.example.com/ep{i}.mp3"] = (Enumerable.Range(0, 1000).Select(b => (byte)(b + i)).ToArray(), "audio/mpeg");
        }
    }

    public void Dispose()
    {
        _server.Dispose();
        _factory.Dispose();
        _host.Dispose();
    }

    /// <summary>A feed of <paramref name="count"/> episodes a week apart, newest first, with a trailer before them all.</summary>
    private static string Feed(int count, string title = "The History Hour")
    {
        var items = new StringBuilder();
        for (var i = count; i >= 1; i--)
        {
            var date = new DateTime(2026, 9, 1, 9, 0, 0, DateTimeKind.Utc).AddDays(7 * i);
            items.Append(System.Globalization.CultureInfo.InvariantCulture, $"""
                <item><title>Episode {i}</title><guid>ep-{i}</guid><pubDate>{date:R}</pubDate>
                <description>&lt;p&gt;Notes for {i}&lt;/p&gt;</description>
                <enclosure url="https://cdn.example.com/ep{i}.mp3" length="1000" type="audio/mpeg"/><itunes:duration>40:00</itunes:duration></item>
                """);
        }

        return $"""
            <rss version="2.0" xmlns:itunes="http://www.itunes.com/dtds/podcast-1.0.dtd"><channel>
            <title>{title}</title><itunes:author>Jane Smith</itunes:author><itunes:image href="https://cdn.example.com/show.jpg"/>
            <itunes:category text="History"/>
            {items}
            <item><title>Trailer</title><guid>trailer</guid><pubDate>Mon, 01 Jun 2026 09:00:00 GMT</pubDate>
            <enclosure url="https://cdn.example.com/trailer.mp3" type="audio/mpeg"/><itunes:episodeType>trailer</itunes:episodeType></item>
            </channel></rss>
            """;
    }

    private HttpClient Client(string? profile = null)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profile is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profile);
        }

        return client;
    }

    private static Uri Api(string path) => new($"/api/v1/{path}", UriKind.Relative);

    /// <summary>The built-in Podcasts library's folder (made on first start).</summary>
    private async Task<string> AddLibraryAsync()
    {
        var libraries = await GetAsync(Client(), "libraries?all=true");
        var podcasts = Assert.Single(libraries.EnumerateArray(), l => l.GetProperty("type").GetString() == "Podcasts");
        return podcasts.GetProperty("folders")[0].GetString()!;
    }

    private static async Task<JsonElement> AddPodcastAsync(HttpClient client, string url = FeedUrl)
    {
        var response = await client.PostAsJsonAsync(Api("podcasts"), new { feedUrl = url });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<JsonElement> GetAsync(HttpClient client, string path) => await client.GetFromJsonAsync<JsonElement>(Api(path));

    [Fact]
    public async Task Podcasts_AreBuiltIn_WithALibraryThatCantBeRemovedOrDoubled()
    {
        var client = Client();
        var status = await GetAsync(client, "podcasts/status");
        Assert.True(status.GetProperty("available").GetBoolean());

        var folder = await AddLibraryAsync();
        Assert.True(Directory.Exists(folder));
        var id = (await GetAsync(client, "libraries?all=true")).EnumerateArray().Single(l => l.GetProperty("type").GetString() == "Podcasts").GetProperty("id").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.DeleteAsync(Api($"libraries/{id}"))).StatusCode);
        var second = await client.PostAsJsonAsync(Api("libraries"), new { name = "More podcasts", type = "Podcasts", folders = new[] { _factory.MediaDirectory } });
        Assert.Equal(HttpStatusCode.BadRequest, second.StatusCode);

        // Its folder can move (beside the rest of the media, say).
        var moved = Directory.CreateDirectory(Path.Combine(_factory.MediaDirectory, "Podcasts")).FullName;
        (await client.PutAsJsonAsync(Api($"libraries/{id}"), new { name = "Podcasts", type = "Podcasts", folders = new[] { moved } })).EnsureSuccessStatusCode();
        Assert.Equal(moved, await AddLibraryAsync());
    }

    [Fact]
    public async Task AddingAFeed_ThenSubscribing_ShowsItsEpisodesNewestFirst()
    {
        await AddLibraryAsync();
        var client = Client();

        var podcast = await AddPodcastAsync(client);
        var id = podcast.GetProperty("id").GetString();
        Assert.Equal("The History Hour", podcast.GetProperty("title").GetString());
        Assert.Equal("Jane Smith", podcast.GetProperty("author").GetString());
        Assert.Equal(4, podcast.GetProperty("episodeCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, podcast.GetProperty("subscription").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, podcast.GetProperty("artwork").GetProperty("poster").ValueKind);
        Assert.Equal("History", podcast.GetProperty("categories")[0].GetString());

        // The same feed again is the same podcast.
        Assert.Equal(id, (await AddPodcastAsync(client)).GetProperty("id").GetString());
        Assert.Empty((await GetAsync(client, "podcasts")).EnumerateArray());

        var subscribed = await (await client.PutAsJsonAsync(Api($"podcasts/{id}/subscription"), new { })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(JsonValueKind.Null, subscribed.GetProperty("subscription").ValueKind);
        Assert.Equal(id, Assert.Single((await GetAsync(client, "podcasts")).EnumerateArray()).GetProperty("id").GetString());

        var episodes = await GetAsync(client, $"podcasts/{id}/episodes");
        Assert.Equal(4, episodes.GetProperty("total").GetInt32());
        Assert.Equal(["Episode 3", "Episode 2", "Episode 1", "Trailer"], episodes.GetProperty("items").EnumerateArray().Select(e => e.GetProperty("title").GetString()));
        var newest = episodes.GetProperty("items")[0];
        Assert.Equal("Notes for 3", newest.GetProperty("overview").GetString());
        Assert.Equal(2400, newest.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("The History Hour", newest.GetProperty("podcastTitle").GetString());

        var oldest = await GetAsync(client, $"podcasts/{id}/episodes?sort=oldest&limit=1");
        Assert.Equal("Trailer", oldest.GetProperty("items")[0].GetProperty("title").GetString());

        var fresh = await GetAsync(client, "podcasts/new");
        Assert.Equal("Episode 3", fresh[0].GetProperty("title").GetString());

        // Another profile follows nothing.
        var other = await (await client.PostAsJsonAsync(Api("profiles"), new { name = "Claire" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await client.PostAsJsonAsync(Api($"profiles/{other.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        Assert.Empty((await GetAsync(Client(token), "podcasts")).EnumerateArray());

        (await client.DeleteAsync(Api($"podcasts/{id}/subscription"))).EnsureSuccessStatusCode();
        Assert.Empty((await GetAsync(client, "podcasts")).EnumerateArray());
    }

    [Fact]
    public async Task Playing_RelaysTheHostsFile_WithRanges_AndResumes()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        var episode = (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items")[0].GetProperty("id").GetString();

        var playback = await (await client.PostAsJsonAsync(Api("playback"), new { itemId = episode, profile = new { } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("DirectPlay", playback.GetProperty("method").GetString());
        Assert.Equal(2400, playback.GetProperty("durationSeconds").GetDouble());
        Assert.Equal("The History Hour", playback.GetProperty("item").GetProperty("seriesTitle").GetString());

        var stream = new HttpRequestMessage(HttpMethod.Get, new Uri(playback.GetProperty("url").GetString()!, UriKind.Relative));
        stream.Headers.Range = new RangeHeaderValue(100, 199);
        var part = await _server.CreateClient().SendAsync(stream); // the session is the credential, as for players
        Assert.Equal(HttpStatusCode.PartialContent, part.StatusCode);
        Assert.Equal("audio/mpeg", part.Content.Headers.ContentType?.MediaType);
        Assert.Equal("bytes 100-199/1000", part.Content.Headers.ContentRange?.ToString());
        Assert.Equal(_host.Files["https://cdn.example.com/ep3.mp3"].Body[100..200], await part.Content.ReadAsByteArrayAsync());

        // Podcasts resume from wherever they were left, even early on.
        var sid = playback.GetProperty("sessionId").GetString();
        (await client.PostAsJsonAsync(Api($"playback/{sid}/progress"), new { positionSeconds = 12 })).EnsureSuccessStatusCode();
        var listening = Assert.Single((await GetAsync(client, "podcasts/continue")).EnumerateArray());
        Assert.Equal(12, listening.GetProperty("userData").GetProperty("positionSeconds").GetDouble());
        var again = await (await client.PostAsJsonAsync(Api("playback"), new { itemId = episode, profile = new { } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(12, again.GetProperty("startPositionSeconds").GetDouble());

        // Marking the show played marks every episode.
        (await client.PostAsJsonAsync(Api($"items/{id}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        Assert.Equal(0, (await GetAsync(client, $"podcasts/{id}")).GetProperty("unplayedCount").GetInt32());
        Assert.Empty((await GetAsync(client, "podcasts/continue")).EnumerateArray());
    }

    [Fact]
    public async Task Refreshing_AsksWhetherTheFeedChanged_AndAddsNewEpisodes()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();

        // Unchanged: the host says so (304) and nothing is read.
        (await client.PostAsync(Api($"podcasts/{id}/refresh"), null)).EnsureSuccessStatusCode();
        Assert.Contains(_host.Requests, r => r.Headers.Contains("If-None-Match"));

        _host.Feed(FeedUrl, Feed(4));
        var refreshed = await (await client.PostAsync(Api($"podcasts/{id}/refresh"), null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, refreshed.GetProperty("episodeCount").GetInt32());
        Assert.Equal("Episode 4", (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items")[0].GetProperty("title").GetString());

        // Moved for good: the new address is kept.
        const string moved = "https://new-host.example.com/history.xml";
        _host.Redirects[FeedUrl] = (301, moved);
        _host.Feed(moved, Feed(4, "The History Hour (new host)"));
        refreshed = await (await client.PostAsync(Api($"podcasts/{id}/refresh"), null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(moved, refreshed.GetProperty("feedUrl").GetString());
        Assert.Equal("The History Hour (new host)", refreshed.GetProperty("title").GetString());

        // A host that stops answering is noted, not fatal.
        _host.Files.TryRemove(moved, out _);
        refreshed = await (await client.PostAsync(Api($"podcasts/{id}/refresh"), null)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("The feed isn't there any more.", refreshed.GetProperty("error").GetString());
        Assert.Equal(5, refreshed.GetProperty("episodeCount").GetInt32());
    }

    [Fact]
    public async Task AutoPlay_GoesOnToTheShowsNextEpisode_ThenTheNewestFromFollowedShows()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        await client.PutAsJsonAsync(Api($"podcasts/{id}/subscription"), new { });
        var episodes = (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items").EnumerateArray()
            .ToDictionary(e => e.GetProperty("title").GetString()!, e => e.GetProperty("id").GetString()!);

        // After episode 1 comes episode 2 (the next one out), skipping one already heard.
        await client.PostAsJsonAsync(Api($"items/{episodes["Episode 2"]}/watched"), new { watched = true });
        Assert.Equal("Episode 3", (await GetAsync(client, $"podcasts/episodes/{episodes["Episode 1"]}/next")).GetProperty("title").GetString());

        // After the newest, the newest unplayed of the shows followed.
        Assert.Equal("Episode 1", (await GetAsync(client, $"podcasts/episodes/{episodes["Episode 3"]}/next")).GetProperty("title").GetString());

        // Nothing left: 204.
        await client.PostAsJsonAsync(Api($"items/{id}/watched"), new { watched = true });
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Api($"podcasts/episodes/{episodes["Episode 3"]}/next"))).StatusCode);
    }

    [Fact]
    public async Task AutoDownloads_AreTheNewestUnplayedEpisodes_OfShowsThatWantThem()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        await client.PutAsJsonAsync(Api($"podcasts/{id}/subscription"), new { });

        // On, keeping 3, at the usual speed, unless the profile says otherwise.
        var preferences = await GetAsync(client, "podcasts/preferences");
        Assert.True(preferences.GetProperty("autoDownload").GetBoolean());
        Assert.Equal(3, preferences.GetProperty("keepEpisodes").GetInt32());
        Assert.Equal(1, preferences.GetProperty("speed").GetDouble());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Api("podcasts/preferences"), new { keepEpisodes = 0 })).StatusCode);
        var changed = await (await client.PutAsJsonAsync(Api("podcasts/preferences"), new { keepEpisodes = 2, speed = 1.5 })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((true, 2, 1.5), (changed.GetProperty("autoDownload").GetBoolean(), changed.GetProperty("keepEpisodes").GetInt32(), changed.GetProperty("speed").GetDouble()));
        var episodes = (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items").EnumerateArray().ToList();

        var wanted = await GetAsync(client, "podcasts/auto-downloads");
        Assert.Equal([episodes[0].GetProperty("id").GetString(), episodes[1].GetProperty("id").GetString()],
            wanted.EnumerateArray().Select(w => w.GetProperty("episodeId").GetString()));

        // Played: the next one down takes its place. Never the trailer.
        await client.PostAsJsonAsync(Api($"items/{episodes[0].GetProperty("id").GetString()}/watched"), new { watched = true });
        await client.PutAsJsonAsync(Api("podcasts/preferences"), new { keepEpisodes = 10 });
        wanted = await GetAsync(client, "podcasts/auto-downloads");
        Assert.Equal(["Episode 2", "Episode 1"], wanted.EnumerateArray().Select(w => episodes.First(e => e.GetProperty("id").GetString() == w.GetProperty("episodeId").GetString()).GetProperty("title").GetString()));

        await client.PutAsJsonAsync(Api("podcasts/preferences"), new { autoDownload = false });
        Assert.Empty((await GetAsync(client, "podcasts/auto-downloads")).EnumerateArray());
    }

    [Fact]
    public async Task DownloadingAnEpisode_FetchesItIntoThePodcastsFolder_UntilThePhoneHasIt()
    {
        var folder = await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        var episode = (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items")[0].GetProperty("id").GetString();

        var option = Assert.Single((await GetAsync(client, $"items/{episode}/download-options")).EnumerateArray());
        Assert.Equal(1000, option.GetProperty("estimatedBytes").GetInt64());

        var created = await (await client.PostAsJsonAsync(Api("offline"), new { itemId = episode, quality = "Medium" })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Original", created.GetProperty("quality").GetString());
        var downloadId = created.GetProperty("id").GetString()!;
        JsonElement ready = default;
        for (var i = 0; i < 50; i++)
        {
            await _server.Services.GetRequiredService<Flexarr.Api.Downloads.DownloadPreparer>().RunOnceAsync(CancellationToken.None);
            ready = await GetAsync(client, $"offline/{downloadId}");
            if (ready.GetProperty("status").GetString() is "Ready" or "Failed")
            {
                break;
            }
        }

        Assert.Equal("Ready", ready.GetProperty("status").GetString());
        Assert.Equal($"{Guid.Parse(downloadId):N}.mp3", ready.GetProperty("fileName").GetString());
        Assert.Equal(2400, ready.GetProperty("durationSeconds").GetDouble());
        var kept = Assert.Single(Directory.GetFiles(Path.Combine(folder, "The History Hour")));

        var file = await client.GetAsync(Api($"offline/{downloadId}/file"));
        Assert.Equal("audio/mpeg", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal(_host.Files["https://cdn.example.com/ep3.mp3"].Body, await file.Content.ReadAsByteArrayAsync());

        (await client.PostAsync(Api($"offline/{downloadId}/received"), null)).EnsureSuccessStatusCode();
        Assert.False(File.Exists(kept));
    }

    [Fact]
    public async Task OpmlImport_SubscribesToEachFeed_AndExportGivesThemBack()
    {
        await AddLibraryAsync();
        _host.Feed("https://feeds.example.com/other", Feed(1, "Another Show"));
        var client = Client();
        const string opml = """
            <?xml version="1.0"?><opml version="1.0"><head><title>Pocket Casts Feeds</title></head><body>
            <outline text="feeds">
              <outline type="rss" text="History" xmlUrl="https://feeds.example.com/history"/>
              <outline type="rss" text="Other" xmlUrl="https://feeds.example.com/other"/>
              <outline type="rss" text="Gone" xmlUrl="https://feeds.example.com/gone"/>
            </outline></body></opml>
            """;

        var imported = await (await client.PostAsync(Api("podcasts/opml"), new StringContent(opml, Encoding.UTF8, "text/x-opml")))
            .Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, imported.GetProperty("added").GetInt32());
        Assert.Equal("https://feeds.example.com/gone", Assert.Single(imported.GetProperty("failed").EnumerateArray()).GetString());
        Assert.Equal(2, (await GetAsync(client, "podcasts")).GetArrayLength());

        var exported = await client.GetStringAsync(Api("podcasts/opml"));
        Assert.Contains("xmlUrl=\"https://feeds.example.com/history\"", exported, StringComparison.Ordinal);
        Assert.Contains("xmlUrl=\"https://feeds.example.com/other\"", exported, StringComparison.Ordinal);

        var invalid = await client.PostAsync(Api("podcasts/opml"), new StringContent("<html/>", Encoding.UTF8, "text/xml"));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    [Fact]
    public async Task Search_ComesFromApple_MarkingWhatsAlreadyFollowed()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        await client.PutAsJsonAsync(Api($"podcasts/{id}/subscription"), new { });
        await client.PutAsJsonAsync(Api("settings"), new { podcasts = new { country = "gb" } });
        await Task.Delay(500); // config.json is reloaded

        _host.Json("https://itunes.apple.com/search?media=podcast&entity=podcast&limit=40&country=gb&term=history", """
            {"resultCount":2,"results":[
              {"kind":"podcast","collectionId":1,"collectionName":"The History Hour","artistName":"Jane Smith","feedUrl":"https://feeds.example.com/history",
               "artworkUrl600":"https://is1-ssl.mzstatic.com/image/thumb/Podcasts1/v4/aa/show.jpg/600x600bb.jpg","primaryGenreName":"History","trackCount":3},
              {"kind":"podcast","collectionId":2,"collectionName":"Other History","artistName":"Someone","feedUrl":"https://feeds.example.com/other2",
               "collectionExplicitness":"explicit"}]}
            """);
        var results = (await GetAsync(client, "podcasts/search?q=history")).EnumerateArray().ToList();
        Assert.Equal(2, results.Count);
        Assert.Equal(id, results[0].GetProperty("podcastId").GetString());
        Assert.True(results[0].GetProperty("subscribed").GetBoolean());
        Assert.StartsWith("/api/v1/images/remote?url=", results[0].GetProperty("artwork").GetString(), StringComparison.Ordinal);
        Assert.False(results[1].GetProperty("subscribed").GetBoolean());
        Assert.True(results[1].GetProperty("explicit").GetBoolean());

        // The library's own search leaves podcasts to this one.
        var library = await GetAsync(client, "search?q=history");
        Assert.DoesNotContain(library.EnumerateObject().SelectMany(p => p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray() : []),
            i => i.TryGetProperty("kind", out var kind) && kind.GetString() == "Podcast");
    }

    [Fact]
    public async Task TheChartsCountry_IsASetting()
    {
        var client = Client();
        var bad = await client.PutAsJsonAsync(Api("settings"), new { podcasts = new { country = "xx" } });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var saved = await (await client.PutAsJsonAsync(Api("settings"), new { podcasts = new { country = "UK" } })).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("gb", saved.GetProperty("podcasts").GetProperty("country").GetString());
        Assert.Equal("gb", (await GetAsync(client, "podcasts/status")).GetProperty("country").GetString());
        Assert.Contains((await GetAsync(client, "podcasts/countries")).EnumerateArray(), c => c.GetProperty("code").GetString() == "us");
    }

    [Fact]
    public async Task APodcastNobodyFollows_IsClearedOutAfterAMonth()
    {
        await AddLibraryAsync();
        var client = Client();
        var kept = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        _host.Feed("https://feeds.example.com/other", Feed(1, "Another Show"));
        var dropped = (await AddPodcastAsync(client, "https://feeds.example.com/other")).GetProperty("id").GetString();
        await client.PutAsJsonAsync(Api($"podcasts/{kept}/subscription"), new { });

        using (var scope = _server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.MediaItems.Where(i => i.Kind == Core.Media.MediaKind.Podcast)
                .ExecuteUpdateAsync(u => u.SetProperty(i => i.AddedAt, DateTime.UtcNow.AddDays(-40)));
        }

        var worker = ActivatorUtilities.CreateInstance<PodcastRefreshWorker>(_server.Services);
        await worker.RunOnceAsync(CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(Api($"podcasts/{kept}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api($"podcasts/{dropped}"))).StatusCode);
    }

    [Fact]
    public async Task Seeking_GoesStraightToWhereTheRedirectsLed_UntilThatStopsWorking()
    {
        await AddLibraryAsync();
        var client = Client();
        var id = (await AddPodcastAsync(client)).GetProperty("id").GetString();
        var episode = (await GetAsync(client, $"podcasts/{id}/episodes")).GetProperty("items")[0].GetProperty("id").GetString();
        var sound = _host.Files["https://cdn.example.com/ep3.mp3"];
        _host.Files.TryRemove("https://cdn.example.com/ep3.mp3", out _);
        _host.Redirects["https://cdn.example.com/ep3.mp3"] = (302, "https://tracker.example.com/ep3.mp3");
        _host.Redirects["https://tracker.example.com/ep3.mp3"] = (302, "https://signed.example.com/ep3.mp3?sig=1");
        _host.Files["https://signed.example.com/ep3.mp3?sig=1"] = sound;

        var playback = await (await client.PostAsJsonAsync(Api("playback"), new { itemId = episode, profile = new { } })).Content.ReadFromJsonAsync<JsonElement>();
        var url = new Uri(playback.GetProperty("url").GetString()!, UriKind.Relative);
        var player = _server.CreateClient();
        Assert.Equal(sound.Body, await player.GetByteArrayAsync(url));

        while (_host.Requests.TryDequeue(out _))
        {
        }

        Assert.Equal(sound.Body, await player.GetByteArrayAsync(url));
        Assert.Equal("https://signed.example.com/ep3.mp3?sig=1", Assert.Single(_host.Requests).RequestUri!.AbsoluteUri);

        // The signed link ran out: back through the redirects to a new one.
        _host.Files.TryRemove("https://signed.example.com/ep3.mp3?sig=1", out _);
        _host.Redirects["https://tracker.example.com/ep3.mp3"] = (302, "https://signed.example.com/ep3.mp3?sig=2");
        _host.Files["https://signed.example.com/ep3.mp3?sig=2"] = sound;
        Assert.Equal(sound.Body, await player.GetByteArrayAsync(url));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.106", false)]
    [InlineData("100.101.102.103", false)] // a tailnet
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("::1", false)]
    [InlineData("fd7a:115c:a1e0::1", false)]
    [InlineData("::ffff:192.168.1.1", false)]
    [InlineData("0.0.0.0", false)]
    public void OnlyTheInternet_IsFetchedFrom(string address, bool allowed) =>
        Assert.Equal(allowed, PodcastFetcher.IsPublic(IPAddress.Parse(address)));
}
