using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Core.Tests;

/// <summary>The Lidarr calls behind music requests: what each one sends, so a request never pulls in more than it should.</summary>
public class LidarrRequestClientTests
{
    private static readonly ArrInstance Lidarr = new() { Name = "Lidarr", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr.test"), ApiKey = "k" };
    private static readonly LidarrPlacement Placement = new("/data/music", 1, 2);

    [Fact]
    public async Task Lookups_ReadArtistsAndAlbums_WithTheirMusicBrainzIds()
    {
        var handler = new Recorder();
        handler.Answers["GET /api/v1/artist/lookup"] = File.ReadAllText(Path.Combine("Fixtures", "lidarr-artist-lookup.json"));
        handler.Answers["GET /api/v1/album/lookup"] = File.ReadAllText(Path.Combine("Fixtures", "lidarr-album-lookup.json"));
        var client = new ArrClient(new HttpClient(handler));

        var artists = await client.LookupArtistsAsync(Lidarr, "wet leg");
        var albums = await client.LookupAlbumsAsync(Lidarr, "wet leg moisturizer");

        Assert.Equal("Wet Leg", artists[0].ArtistName);
        Assert.Equal("90744b3e-363a-458e-8da1-e3e392a489c4", artists[0].ForeignArtistId);
        Assert.Equal(0, artists[0].Id);
        Assert.Equal("moisturizer", albums[0].Title);
        Assert.Equal("660fd111-0d68-4d0e-8b83-ff5ca450bfa3", albums[0].ForeignAlbumId);
        Assert.Equal("Wet Leg", albums[0].Artist?.ArtistName);
        Assert.StartsWith("https://", albums[0].RemoteCover, StringComparison.Ordinal);
        Assert.Contains("term=wet%20leg%20moisturizer", handler.Requests[1].Uri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddingAnAlbum_MonitorsOnlyThatAlbum_AndSearchesForIt()
    {
        var handler = new Recorder();
        handler.Answers["POST /api/v1/album"] = """{"id":7,"artistId":3,"title":"moisturizer"}""";
        var client = new ArrClient(new HttpClient(handler));

        var album = await client.AddAlbumAsync(Lidarr, "album-mbid", "moisturizer", "artist-mbid", "Wet Leg", Placement);

        Assert.Equal(7, album.Id);
        var body = handler.Requests.Single().Body!;
        Assert.Equal("album-mbid", body["foreignAlbumId"]!.GetValue<string>());
        Assert.True(body["monitored"]!.GetValue<bool>());
        Assert.True(body["addOptions"]!["searchForNewAlbum"]!.GetValue<bool>());
        Assert.Equal("manual", body["addOptions"]!["addType"]!.GetValue<string>());
        var artist = body["artist"]!;
        Assert.Equal("artist-mbid", artist["foreignArtistId"]!.GetValue<string>());
        Assert.Equal("none", artist["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("none", artist["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.False(artist["addOptions"]!["searchForMissingAlbums"]!.GetValue<bool>());
        Assert.Equal("/data/music", artist["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(1, artist["qualityProfileId"]!.GetValue<int>());
        Assert.Equal(2, artist["metadataProfileId"]!.GetValue<int>());
    }

    [Fact]
    public async Task FollowingANewArtist_GetsTheirLatestAlbum_AndNewReleases()
    {
        var handler = new Recorder();
        handler.Answers["POST /api/v1/artist"] = """{"id":3,"artistName":"Wet Leg"}""";
        var client = new ArrClient(new HttpClient(handler));

        await client.AddFollowedArtistAsync(Lidarr, "artist-mbid", "Wet Leg", Placement);

        var body = handler.Requests.Single().Body!;
        Assert.Equal("new", body["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("latest", body["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.True(body["addOptions"]!["searchForMissingAlbums"]!.GetValue<bool>());
    }

    [Fact]
    public async Task FollowingAnArtistLidarrHas_KeepsEverythingElseAboutThem()
    {
        var handler = new Recorder();
        handler.Answers["GET /api/v1/artist/3"] = """{"id":3,"artistName":"Wet Leg","monitored":false,"monitorNewItems":"none","path":"/data/music/Wet Leg","qualityProfileId":4}""";
        handler.Answers["PUT /api/v1/artist/3"] = """{"id":3,"artistName":"Wet Leg"}""";
        var client = new ArrClient(new HttpClient(handler));

        await client.FollowArtistAsync(Lidarr, 3);

        var put = handler.Requests.Last();
        Assert.Equal("PUT", put.Method);
        Assert.True(put.Body!["monitored"]!.GetValue<bool>());
        Assert.Equal("new", put.Body["monitorNewItems"]!.GetValue<string>());
        Assert.Equal("/data/music/Wet Leg", put.Body["path"]!.GetValue<string>());
        Assert.Equal(4, put.Body["qualityProfileId"]!.GetValue<int>());
    }

    [Fact]
    public async Task AnAlbumLidarrHas_IsMonitoredThenSearched()
    {
        var handler = new Recorder();
        handler.Answers["PUT /api/v1/album/monitor"] = "[]";
        handler.Answers["POST /api/v1/command"] = "{}";
        var client = new ArrClient(new HttpClient(handler));

        await client.SetAlbumsMonitoredAsync(Lidarr, [7], monitored: true);
        await client.SearchAlbumsAsync(Lidarr, [7]);

        Assert.Equal([7], handler.Requests[0].Body!["albumIds"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.True(handler.Requests[0].Body!["monitored"]!.GetValue<bool>());
        Assert.Equal("AlbumSearch", handler.Requests[1].Body!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task RootFolders_CarryTheirDefaultProfiles()
    {
        var handler = new Recorder();
        handler.Answers["GET /api/v1/rootfolder"] = """[{"id":1,"path":"/data/music","defaultQualityProfileId":3,"defaultMetadataProfileId":2,"defaultMonitorOption":"all"}]""";
        var client = new ArrClient(new HttpClient(handler));

        var folder = Assert.Single(await client.GetLidarrRootFoldersAsync(Lidarr));

        Assert.Equal(new LidarrRootFolder(1, "/data/music", 3, 2), folder);
    }

    /// <summary>Answers "METHOD /path" (query string aside) with canned JSON, and records each request and its body.</summary>
    private sealed class Recorder : HttpMessageHandler
    {
        public Dictionary<string, string> Answers { get; } = [];
        public List<(string Method, string Uri, JsonNode? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.Method.Method, request.RequestUri!.AbsoluteUri, body));
            return Answers.TryGetValue($"{request.Method.Method} {request.RequestUri.AbsolutePath}", out var json)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }
    }
}
