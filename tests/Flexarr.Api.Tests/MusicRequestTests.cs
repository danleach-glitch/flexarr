using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Music discovery and requests, against a stand-in Lidarr and Deezer.</summary>
public sealed class MusicRequestTests : IAsyncLifetime
{
    private const string WetLeg = "90744b3e-363a-458e-8da1-e3e392a489c4";
    private const string Moisturizer = "660fd111-0d68-4d0e-8b83-ff5ca450bfa3";

    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeArr _lidarr = new();
    private readonly FakeArr _deezer = new();
    private readonly FakeMusicBrainz _musicBrainz = new();
    private readonly ConcurrentQueue<(string Request, JsonNode? Body)> _sent = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Arr:0:Name", "Lidarr");
            b.UseSetting("Flexarr:Arr:0:Kind", "Lidarr");
            b.UseSetting("Flexarr:Arr:0:BaseUrl", "http://lidarr.test");
            b.UseSetting("Flexarr:Arr:0:ApiKey", "key");
            b.ConfigureTestServices(s =>
            {
                s.AddHttpClient<ArrClient>().ConfigurePrimaryHttpMessageHandler(() => _lidarr);
                s.AddHttpClient<DeezerClient>().ConfigurePrimaryHttpMessageHandler(() => _deezer);
                s.AddHttpClient(nameof(DeezerClient)).ConfigurePrimaryHttpMessageHandler(() => _deezer);
                s.AddHttpClient<MusicBrainzClient>().ConfigurePrimaryHttpMessageHandler(() => _musicBrainz);
            });
        });
        _lidarr.OnRequest = r => _sent.Enqueue(($"{r.Method} {r.RequestUri!.AbsolutePath}", r.Content is null ? null : JsonNode.Parse(r.Content.ReadAsStringAsync().Result)));
        _lidarr.Responses["GET /ping"] = "OK";
        _lidarr.Responses["GET /api/v1/rootfolder"] = """[{"id":1,"path":"/data/music","defaultQualityProfileId":2,"defaultMetadataProfileId":3}]""";
        _lidarr.Responses["GET /api/v1/artist"] = "[]";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = File.ReadAllText(Fixture("lidarr-artist-lookup.json"));
        _lidarr.Responses["GET /api/v1/album/lookup?term=Wet%20Leg%20moisturizer"] = File.ReadAllText(Fixture("lidarr-album-lookup.json"));
        _lidarr.Responses["POST /api/v1/album"] = """{"id":9,"artistId":3,"title":"moisturizer"}""";
        _lidarr.Responses["POST /api/v1/artist"] = """{"id":3,"artistName":"Wet Leg","path":"/data/music/Wet Leg"}""";
        _lidarr.Responses["PUT /api/v1/album/monitor"] = "[]";
        _lidarr.Responses["POST /api/v1/command"] = "{}";

        var album = File.ReadAllText(Fixture("deezer-album.json"));
        var wetLeg = """{"id":134334152,"name":"Wet Leg","picture_xl":"https://cdn-images.dzcdn.net/images/artist/6df9f45478aa0c897b028e06eb189159/1000x1000-000000-80-0-0.jpg","nb_album":14,"nb_fan":63124}""";
        _deezer.Responses["GET /album/730000661"] = album;
        _deezer.Responses["GET /artist/134334152"] = wetLeg;
        _deezer.Responses["GET /artist/134334152/albums?limit=100"] = $$"""{"data":[{{AlbumJson(730000661, "moisturizer", "2025-07-11")}},{{AlbumJson(1, "Wet Leg", "2022-04-08")}}]}""";
        _deezer.Responses["GET /artist/134334152/related?limit=20"] = """{"data":[{"id":5,"name":"Yard Act"},{"id":6,"name":"Dry Cleaning"}]}""";
        _deezer.Responses["GET /search/artist?q=Wet%20Leg&limit=5"] = $$"""{"data":[{{wetLeg}}]}""";
        _deezer.Responses["GET /chart/0/albums?limit=30"] = $$"""{"data":[{{AlbumJson(730000661, "moisturizer", null, true)}},{{AlbumJson(1, "Wet Leg", null, true)}}]}""";
        _deezer.Responses["GET /genre"] = """{"data":[{"id":0,"name":"All"},{"id":152,"name":"Rock","picture_xl":"https://cdn-images.dzcdn.net/images/misc/abc/1000x1000-000000-80-0-0.jpg"}]}""";
        _deezer.Responses["GET /genre/152/artists"] = $$"""{"data":[{{wetLeg}}]}""";
        _deezer.Responses["GET /search/artist?q=wet%20leg&limit=8"] = $$"""{"data":[{{wetLeg}}]}""";
        _deezer.Responses["GET /search/album?q=wet%20leg&limit=16"] = $$"""{"data":[{{AlbumJson(730000661, "moisturizer", null, true)}}]}""";
        return Task.CompletedTask;
    }

    private static string AlbumJson(long id, string title, string? date, bool withArtist = false) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["id"] = id, ["title"] = title, ["release_date"] = date, ["record_type"] = "album",
            ["cover_xl"] = "https://cdn-images.dzcdn.net/images/cover/8a22859ecaf89fcb363298f165377b83/1000x1000-000000-80-0-0.jpg",
            ["artist"] = withArtist ? new { id = 134334152, name = "Wet Leg" } : null,
        });

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _lidarr.Dispose();
        _deezer.Dispose();
        _musicBrainz.Dispose();
    }

    private static string Fixture(string name) => Path.Combine("Fixtures", name);

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client(string? profileToken = null)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private async Task<(Library Library, MediaItem Artist, MediaItem Album)> SeedOwnedAsync()
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music, Folders = [new LibraryFolder { Path = "/Volumes/Media/data/music" }] };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Wet Leg", SortTitle = "wet leg" };
        var album = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Album, Title = "Wet Leg", SortTitle = "wet leg", Parent = artist };
        db.Libraries.Add(library);
        db.MediaItems.AddRange(artist, album);
        await db.SaveChangesAsync();
        return (library, artist, album);
    }

    /// <summary>A music library (with nothing in it yet) at Lidarr's root folder: music requests need one to land in.</summary>
    private async Task<Library> MusicLibraryAsync(string folder = "/Volumes/Media/data/music", string name = "Music")
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"{name} {Guid.NewGuid():N}", Type = LibraryType.Music, Folders = [new LibraryFolder { Path = folder }] };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library;
    }

    private async Task<T> InDbAsync<T>(Func<FlexarrDbContext, Task<T>> query)
    {
        using var scope = _server.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>());
    }

    [Fact]
    public async Task Status_IsOnWithLidarr_AndOffWhenTheSwitchIs()
    {
        await MusicLibraryAsync();
        var on = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/status"));
        await Client().PutAsJsonAsync(Api("/settings"), new { allowMusicRequests = false });
        var off = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/status"));
        var settings = await Client().GetFromJsonAsync<JsonElement>(Api("/settings"));
        var refused = await Client().GetAsync(Api("/requests/music/discover"));
        await Client().PutAsJsonAsync(Api("/settings"), new { allowMusicRequests = true });

        Assert.True(on.GetProperty("configured").GetBoolean());
        Assert.True(on.GetProperty("reachable").GetBoolean());
        Assert.False(off.GetProperty("configured").GetBoolean());
        Assert.False(settings.GetProperty("allowMusicRequests").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
    }

    [Fact]
    public async Task WithNoMusicLibrary_MusicRequestsArentOffered()
    {
        var status = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/status"));
        var request = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
    }

    [Fact]
    public async Task ARequest_GoesToALibraryTheProfileCanSee_NeverAHiddenOne()
    {
        _lidarr.Responses["GET /api/v1/rootfolder"] = """[{"id":1,"path":"/data/music","defaultQualityProfileId":2,"defaultMetadataProfileId":3},{"id":2,"path":"/data/sam-music","defaultQualityProfileId":2,"defaultMetadataProfileId":3}]""";
        var dads = await MusicLibraryAsync();
        await MusicLibraryAsync("/Volumes/Media/data/sam-music", "Sam's music");
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}"[..12], hiddenLibraryIds = new[] { dads.Id } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var response = await Client(token).PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/data/sam-music", _sent.Single(s => s.Request == "POST /api/v1/album").Body!["artist"]!["rootFolderPath"]!.GetValue<string>());
    }

    [Fact]
    public async Task Discover_MarksWhatsInTheLibrary_AndHasGenres()
    {
        var (_, _, owned) = await SeedOwnedAsync();

        var discover = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/discover"));

        var charts = discover.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("id").GetString() == "music-charts").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(("None", "Wet Leg"), (charts[0].GetProperty("availability").GetString(), charts[0].GetProperty("artist").GetString()));
        Assert.Equal("Available", charts[1].GetProperty("availability").GetString());
        Assert.Equal(owned.Id, charts[1].GetProperty("libraryItemId").GetGuid());
        Assert.StartsWith("/api/v1/images/remote?url=https%3A%2F%2Fcdn-images.dzcdn.net", charts[0].GetProperty("image").GetString(), StringComparison.Ordinal);
        var genre = Assert.Single(discover.GetProperty("genres").EnumerateArray());
        Assert.Equal("Rock", genre.GetProperty("name").GetString());
        var rock = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/genres/152"));
        Assert.Equal("Available", rock.GetProperty("artists")[0].GetProperty("availability").GetString());
    }

    [Fact]
    public async Task ARowDeezerDoesntAnswerFor_IsLeftOut_NotThePage()
    {
        await MusicLibraryAsync();
        _deezer.Responses.TryRemove("GET /chart/0/albums?limit=30", out _);

        var response = await Client().GetAsync(Api("/requests/music/discover"));
        var discover = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain(discover.GetProperty("rows").EnumerateArray(), r => r.GetProperty("id").GetString() == "music-charts");
        Assert.Single(discover.GetProperty("genres").EnumerateArray());
    }

    [Fact]
    public async Task Search_FindsArtistsAndAlbums()
    {
        await MusicLibraryAsync();
        var found = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/search?q=wet%20leg"));

        Assert.Equal("Wet Leg", found.GetProperty("artists")[0].GetProperty("title").GetString());
        Assert.Equal("moisturizer", found.GetProperty("albums")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task RequestingAnAlbum_LidarrHasntTheArtistOf_AddsJustThatAlbum_AndShowsItRequested()
    {
        await MusicLibraryAsync();
        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var add = _sent.Single(s => s.Request == "POST /api/v1/album").Body!;
        Assert.Equal(Moisturizer, add["foreignAlbumId"]!.GetValue<string>());
        Assert.Equal(WetLeg, add["artist"]!["foreignArtistId"]!.GetValue<string>());
        Assert.Equal("none", add["artist"]!["addOptions"]!["monitor"]!.GetValue<string>());
        Assert.Equal(("/data/music", 2, 3), (add["artist"]!["rootFolderPath"]!.GetValue<string>(), add["artist"]!["qualityProfileId"]!.GetValue<int>(),
            add["artist"]!["metadataProfileId"]!.GetValue<int>()));
        Assert.DoesNotContain(_sent, s => s.Request == "POST /api/v1/artist");
        var request = await InDbAsync(db => db.MediaRequests.SingleAsync(r => r.Kind == MediaKind.Album));
        Assert.Equal((Moisturizer, WetLeg, "moisturizer", "Wet Leg"), (request.MusicBrainzId, request.ArtistMusicBrainzId, request.Title, request.ArtistName));
        var page = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/album/730000661"));
        Assert.Equal("Requested", page.GetProperty("availability").GetString());
        Assert.Equal(3, page.GetProperty("tracks").GetArrayLength());
    }

    [Fact]
    public async Task RequestingAnAlbum_LidarrHasTheArtistOf_MonitorsAndSearchesIt()
    {
        await MusicLibraryAsync();
        _lidarr.Responses["GET /api/v1/artist"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":true,"monitorNewItems":"none"}]""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}"}]""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = $$"""[{"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":false}]""";

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal([9], _sent.Single(s => s.Request == "PUT /api/v1/album/monitor").Body!["albumIds"]!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Equal("AlbumSearch", _sent.Single(s => s.Request == "POST /api/v1/command").Body!["name"]!.GetValue<string>());
        Assert.DoesNotContain(_sent, s => s.Request == "POST /api/v1/album");
    }

    [Fact]
    public async Task RequestingAnAlbum_ByAnArtistLidarrHasUnmonitored_MonitorsThemButNotTheirNewReleases()
    {
        await MusicLibraryAsync();
        _lidarr.Responses["GET /api/v1/artist"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":false,"monitorNewItems":"none"}]""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}"}]""";
        _lidarr.Responses["GET /api/v1/artist/3"] = $$"""{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":false,"monitorNewItems":"all"}""";
        _lidarr.Responses["PUT /api/v1/artist/3"] = """{"id":3,"artistName":"Wet Leg"}""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = $$"""[{"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":false}]""";

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var artist = _sent.Single(s => s.Request == "PUT /api/v1/artist/3").Body!;
        Assert.Equal((true, "none"), (artist["monitored"]!.GetValue<bool>(), artist["monitorNewItems"]!.GetValue<string>()));
        Assert.Equal([9], _sent.Single(s => s.Request == "PUT /api/v1/album/monitor").Body!["albumIds"]!.AsArray().Select(n => n!.GetValue<int>()));
    }

    [Fact]
    public async Task FollowingAnArtist_GetsTheirLatestAlbum_AndNewReleases()
    {
        await MusicLibraryAsync();
        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "artist", deezerId = 134334152 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var add = _sent.Single(s => s.Request == "POST /api/v1/artist").Body!;
        Assert.Equal(("new", "latest"), (add["monitorNewItems"]!.GetValue<string>(), add["addOptions"]!["monitor"]!.GetValue<string>()));
        Assert.True(add["addOptions"]!["searchForMissingAlbums"]!.GetValue<bool>());
        Assert.Equal(MediaKind.Artist, (await InDbAsync(db => db.MediaRequests.SingleAsync())).Kind);
    }

    [Fact]
    public async Task FollowingAnArtistLidarrHas_SearchesForTheirLatestAlbum()
    {
        await MusicLibraryAsync();
        _lidarr.Responses["GET /api/v1/artist"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":false,"monitorNewItems":"none"}]""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}"}]""";
        _lidarr.Responses["GET /api/v1/artist/3"] = $$"""{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":false,"monitorNewItems":"none"}""";
        _lidarr.Responses["PUT /api/v1/artist/3"] = """{"id":3,"artistName":"Wet Leg"}""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = $$$"""
            [{"id":8,"artistId":3,"title":"Wet Leg","albumType":"Album","releaseDate":"2022-04-08T00:00:00Z","monitored":false,"statistics":{"trackFileCount":12,"trackCount":12,"totalTrackCount":12,"sizeOnDisk":1}},
             {"id":9,"artistId":3,"title":"moisturizer","albumType":"Album","releaseDate":"2025-07-11T00:00:00Z","monitored":false},
             {"id":10,"artistId":3,"title":"Live at Glastonbury","albumType":"Album","secondaryTypes":["Live"],"releaseDate":"2025-09-01T00:00:00Z","monitored":false},
             {"id":11,"artistId":3,"title":"Next One","albumType":"Album","releaseDate":"2099-01-01T00:00:00Z","monitored":false}]
            """;

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "artist", deezerId = 134334152 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("new", _sent.Single(s => s.Request == "PUT /api/v1/artist/3").Body!["monitorNewItems"]!.GetValue<string>());
        Assert.Equal([9], _sent.Single(s => s.Request == "PUT /api/v1/album/monitor").Body!["albumIds"]!.AsArray().Select(n => n!.GetValue<int>()));
        var search = _sent.Single(s => s.Request == "POST /api/v1/command").Body!;
        Assert.Equal(("AlbumSearch", 9), (search["name"]!.GetValue<string>(), search["albumIds"]![0]!.GetValue<int>()));
        Assert.DoesNotContain(_sent, s => s.Request == "POST /api/v1/artist");
    }

    [Fact]
    public async Task AnAlbumByANameSeveralArtistsShare_IsFoundByItsBarcode()
    {
        await MusicLibraryAsync();
        const string LePlusBeau = "04120832-3d22-46d7-9e76-10ab72784e11";
        const string TeddyBear = "801b4b13-24d8-4293-94bd-21c5b4bc420e";
        _deezer.Responses["GET /album/1075783372"] =
            """{"id":1075783372,"title":"Le Plus Beau","upc":"3618116321222","release_date":"2026-10-30","record_type":"album","artist":{"id":7,"name":"TeddyBear"}}""";
        _musicBrainz.Releases["3618116321222"] = File.ReadAllText(Fixture("musicbrainz-release-barcode.json"));
        _lidarr.Responses[$"GET /api/v1/album/lookup?term=lidarr%3A{LePlusBeau}"] =
            $$$"""[{"title":"Le Plus Beau","foreignAlbumId":"{{{LePlusBeau}}}","releaseDate":"2026-10-30T00:00:00Z","artist":{"artistName":"TeddyBear","foreignArtistId":"{{{TeddyBear}}}"}}]""";
        _lidarr.Responses[$"GET /api/v1/artist/lookup?term=lidarr%3A{TeddyBear}"] = $$"""[{"artistName":"TeddyBear","foreignArtistId":"{{TeddyBear}}"}]""";

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 1075783372 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var add = _sent.Single(s => s.Request == "POST /api/v1/album").Body!;
        Assert.Equal((LePlusBeau, TeddyBear), (add["foreignAlbumId"]!.GetValue<string>(), add["artist"]!["foreignArtistId"]!.GetValue<string>()));
        Assert.True(add["addOptions"]!["searchForNewAlbum"]!.GetValue<bool>());
        Assert.DoesNotContain(_lidarr.Requests, r => r.Contains("term=TeddyBear", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlbumLidarrCantFind_SaysSo_AndIsntRetried()
    {
        await MusicLibraryAsync();
        _deezer.Responses["GET /album/42"] = """{"id":42,"title":"Nowhere","record_type":"album","artist":{"id":8,"name":"Nobody"}}""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Nobody"] = "[]";

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 42 });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("Lidarr can't find Nowhere by Nobody.", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString());
    }

    [Fact]
    public async Task ALibraryArtist_GetsTheAlbumsItLacks_AndArtistsLikeThem()
    {
        var (_, artist, _) = await SeedOwnedAsync();

        var extras = await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/music/library-artist/{artist.Id}"));

        var missing = Assert.Single(extras.GetProperty("missing").EnumerateArray());
        Assert.Equal(("moisturizer", 730000661L), (missing.GetProperty("title").GetString(), missing.GetProperty("deezerId").GetInt64()));
        Assert.Equal(["Yard Act", "Dry Cleaning"], extras.GetProperty("similar").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
        // Each says whether Lidarr follows them, so a library artist among them can show Following.
        Assert.All(extras.GetProperty("similar").EnumerateArray(), a => Assert.False(a.GetProperty("followed").GetBoolean()));
        Assert.Equal(134334152, extras.GetProperty("deezerId").GetInt64());
        Assert.False(extras.GetProperty("followed").GetBoolean());
    }

    [Fact]
    public async Task AProfileThatSeesNoMusic_CantRequestIt()
    {
        var (library, _, _) = await SeedOwnedAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}"[..12], hiddenLibraryIds = new[] { library.Id } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var sam = Client(token);

        var status = await sam.GetFromJsonAsync<JsonElement>(Api("/requests/music/status"));
        var request = await sam.PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.False(status.GetProperty("configured").GetBoolean());
        Assert.Equal(HttpStatusCode.Forbidden, request.StatusCode);
        Assert.DoesNotContain(_sent, s => s.Request.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALidarrThatDoesntAnswer_IsSaidSo()
    {
        await MusicLibraryAsync();
        _lidarr.Responses.TryRemove("GET /api/v1/rootfolder", out _);

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task ALidarrRefusal_IsSaidPlainly_WithoutLidarrsOwnWords()
    {
        await MusicLibraryAsync();
        _lidarr.Responses.TryRemove("POST /api/v1/album", out _);

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("Lidarr turned the request down. It may have it already.", problem.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task OneProfile_CanAskForSoMuchInADay()
    {
        await MusicLibraryAsync();
        using (var scope = _server.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            for (var i = 0; i < 30; i++)
            {
                db.MediaRequests.Add(new Flexarr.Core.Notifications.MediaRequest { ProfileId = "owner", Kind = MediaKind.Album, MusicBrainzId = $"rg-{i}", Title = $"Album {i}", ArtistName = "Someone" });
            }

            await db.SaveChangesAsync();
        }

        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.DoesNotContain(_sent, s => s.Request.StartsWith("POST", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AskingForOneAlbumAgainAndAgain_CountsTowardsTheDay()
    {
        await MusicLibraryAsync();
        _lidarr.Responses["GET /api/v1/artist"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","monitored":true,"monitorNewItems":"none"}]""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = $$"""[{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}"}]""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = $$"""[{"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":false}]""";
        var client = Client();
        for (var i = 0; i < 30; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await client.PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 })).StatusCode);
        }

        var response = await client.PostAsJsonAsync(Api("/requests/music"), new { kind = "album", deezerId = 730000661 });

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(30, _sent.Count(s => s.Request == "POST /api/v1/command"));
        Assert.Single(await InDbAsync(db => db.MediaRequests.ToListAsync()));
    }

    [Fact]
    public async Task TheMusicDestination_IsAProfilesOwn_AndOnlyAMusicLibrary()
    {
        var (library, _, _) = await SeedOwnedAsync();

        var destinations = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/destinations"));
        var chosen = await Client().PutAsJsonAsync(Api("/profiles/owner/request-music-library"), new { libraryId = library.Id });
        var profile = await chosen.Content.ReadFromJsonAsync<JsonElement>();
        var wrong = await Client().PutAsJsonAsync(Api("/profiles/owner/request-music-library"), new { libraryId = Guid.NewGuid() });
        // Choosing shows and films the old way leaves the music choice as it was.
        await Client().PutAsJsonAsync(Api("/profiles/owner/request-libraries"), new { showsLibraryId = (Guid?)null, filmsLibraryId = (Guid?)null });
        var kept = await InDbAsync(db => db.Profiles.Where(p => p.Id == "owner").Select(p => p.RequestMusicLibraryId).SingleAsync());
        await Client().PutAsJsonAsync(Api("/profiles/owner/request-music-library"), new { libraryId = (Guid?)null });

        Assert.True(destinations.EnumerateArray().Any(d => d.GetProperty("id").GetGuid() == library.Id && d.GetProperty("isDefault").GetBoolean()), destinations.GetRawText());
        Assert.Equal(library.Id, profile.GetProperty("requestMusicLibraryId").GetGuid());
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(library.Id, kept);
    }

    private const string WetLegInLidarr = $$"""{"id":3,"artistName":"Wet Leg","foreignArtistId":"{{WetLeg}}","path":"/data/music/Wet Leg","monitored":true,"monitorNewItems":"none"}""";

    [Fact]
    public async Task FollowingAnArtist_PutsThemInTheLibrary_AndUnfollowingTakesThemOutAgain()
    {
        var library = await MusicLibraryAsync();
        var followed = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "artist", deezerId = 134334152 });

        Assert.Equal(HttpStatusCode.Accepted, followed.StatusCode);
        var placeholder = await InDbAsync(db => db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Artist));
        Assert.Equal(("Wet Leg", true, library.Id, WetLeg, 3), (placeholder.Title, placeholder.Placeholder, placeholder.LibraryId, placeholder.MusicBrainzId, placeholder.ArrId));
        Assert.StartsWith("https://cdn-images.dzcdn.net/", placeholder.PosterUrl, StringComparison.Ordinal);
        var artists = await Client().GetFromJsonAsync<JsonElement>(Api($"/libraries/{library.Id}/items"));
        Assert.Contains("Wet Leg", artists.GetRawText(), StringComparison.Ordinal);
        // Now Lidarr follows them.
        var following = WetLegInLidarr.Replace("\"none\"", "\"new\"", StringComparison.Ordinal);
        _lidarr.Responses["GET /api/v1/artist"] = $"[{following}]";
        _lidarr.Responses["GET /api/v1/artist/3"] = following;
        _lidarr.Responses["PUT /api/v1/artist/3"] = following;
        // Following isn't having: their outside page still offers Following, not In your library.
        var outside = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/artist/134334152"));
        Assert.Equal(JsonValueKind.Null, outside.GetProperty("libraryItemId").ValueKind);

        var unfollowed = await Client().PostAsJsonAsync(Api("/requests/music/unfollow"), new { artistId = placeholder.Id });

        Assert.Equal(HttpStatusCode.NoContent, unfollowed.StatusCode);
        var put = _sent.Single(s => s.Request == "PUT /api/v1/artist/3").Body!;
        Assert.Equal(("none", true), (put["monitorNewItems"]!.GetValue<string>(), put["monitored"]!.GetValue<bool>()));
        Assert.False(await InDbAsync(db => db.MediaItems.AnyAsync(i => i.Kind == MediaKind.Artist)));
        Assert.NotNull((await InDbAsync(db => db.MediaRequests.SingleAsync(r => r.Kind == MediaKind.Artist))).FulfilledAt);
    }

    [Fact]
    public async Task FollowingAnArtistTheLibraryHas_AddsNoSecondArtist()
    {
        var (_, artist, _) = await SeedOwnedAsync();

        await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "artist", artistId = artist.Id });

        Assert.Equal(1, await InDbAsync(db => db.MediaItems.CountAsync(i => i.Kind == MediaKind.Artist)));
    }

    [Fact]
    public async Task Requested_ListsWhatLidarrAwaits_WithHowFarAlong()
    {
        await SeedOwnedAsync();
        var soon = DateTime.UtcNow.Date.AddDays(30).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
        _lidarr.Responses["GET /api/v1/artist"] = $"[{WetLegInLidarr}]";
        _lidarr.Responses["GET /api/v1/wanted/missing?page=1&pageSize=1000&includeArtist=false&monitored=true&sortKey=releaseDate&sortDirection=descending"] = $$"""
            {"page":1,"pageSize":200,"totalRecords":2,"records":[
              {"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":true,"releaseDate":"2025-07-11T00:00:00Z",
               "images":[{"coverType":"cover","url":"/MediaCover/Albums/9/cover.jpg?lastWrite=1"}]},
              {"id":8,"artistId":3,"title":"Wet Leg","foreignAlbumId":"rg-wet-leg","monitored":true,"releaseDate":"2022-04-08T00:00:00Z"}]}
            """;
        var today = DateTime.UtcNow.Date;
        _lidarr.Responses[$"GET /api/v1/calendar?start={today:yyyy-MM-dd}&end={today.AddYears(1):yyyy-MM-dd}&unmonitored=false&includeArtist=false"] =
            $$"""[{"id":11,"artistId":3,"title":"Next One","foreignAlbumId":"rg-next","monitored":true,"releaseDate":"{{soon}}"}]""";
        _lidarr.Responses["GET /api/v1/queue?page=1&pageSize=200&includeArtist=true&includeAlbum=true"] =
            """{"page":1,"pageSize":200,"totalRecords":1,"records":[{"id":5,"albumId":9,"artistId":3,"status":"downloading","trackedDownloadState":"downloading","size":100,"sizeleft":25}]}""";
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new Core.Notifications.MediaRequest { ProfileId = "owner", Kind = MediaKind.Album, MusicBrainzId = "rg-old", Title = "Old Album", ArtistName = "Someone" });
            return await db.SaveChangesAsync();
        });

        var requested = (await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/requested"))).EnumerateArray().ToList();

        Assert.Equal(["moisturizer", "Old Album", "Next One"], requested.Select(r => r.GetProperty("title").GetString()));
        Assert.Equal(("downloading", 0.75), (requested[0].GetProperty("state").GetString(), requested[0].GetProperty("progress").GetDouble()));
        Assert.StartsWith("/api/v1/images/arr?", requested[0].GetProperty("image").GetString(), StringComparison.Ordinal);
        Assert.Equal("searching", requested[1].GetProperty("state").GetString());
        Assert.Equal("upcoming", requested[2].GetProperty("state").GetString());
        // One library's view holds only artists in its folders (and leaves out requests it can't place).
        var elsewhere = await MusicLibraryAsync("/Volumes/Media/data/other-music", "Other");
        Assert.Empty((await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/music/requested?libraryId={elsewhere.Id}"))).EnumerateArray());
        var cover = await Client().GetAsync(new Uri(requested[0].GetProperty("image").GetString()!.Replace("&s=", "&s=0", StringComparison.Ordinal), UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, cover.StatusCode); // only the server's own signed links
    }

    private void LidarrAwaitsMoisturizer(string otherAlbums = "")
    {
        _lidarr.Responses["GET /api/v1/artist"] = $"[{WetLegInLidarr}]";
        _lidarr.Responses[$"GET /api/v1/album?foreignAlbumId={Moisturizer}"] = $$"""[{"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":true}]""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = $$"""[{"id":9,"artistId":3,"title":"moisturizer","foreignAlbumId":"{{Moisturizer}}","monitored":false}{{otherAlbums}}]""";
        _lidarr.Responses["GET /api/v1/queue?page=1&pageSize=200&includeArtist=true&includeAlbum=true"] =
            """{"page":1,"pageSize":200,"totalRecords":1,"records":[{"id":5,"albumId":9,"artistId":3,"status":"downloading","size":100,"sizeleft":25}]}""";
        _lidarr.Responses["DELETE /api/v1/queue/5?removeFromClient=true&blocklist=false"] = "{}";
        _lidarr.Responses["DELETE /api/v1/artist/3?deleteFiles=false&addImportListExclusion=false"] = "{}";
    }

    private Task<int> AskedForMoisturizerAsync(bool addedArtist) => InDbAsync(async db =>
    {
        db.MediaRequests.Add(new Core.Notifications.MediaRequest
        {
            ProfileId = "owner", Kind = MediaKind.Album, MusicBrainzId = Moisturizer, ArtistMusicBrainzId = WetLeg, Title = "moisturizer", ArtistName = "Wet Leg", AddedArtist = addedArtist,
        });
        return await db.SaveChangesAsync();
    });

    [Fact]
    public async Task Unrequesting_StopsLidarrWantingIt_DropsTheDownload_AndTakesOutTheArtistItAdded()
    {
        await MusicLibraryAsync();
        LidarrAwaitsMoisturizer();
        await AskedForMoisturizerAsync(addedArtist: true);

        var response = await Client().PostAsJsonAsync(Api("/requests/music/unrequest"), new { musicBrainzId = Moisturizer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var monitor = _sent.Single(s => s.Request == "PUT /api/v1/album/monitor").Body!;
        Assert.Equal((9, false), (monitor["albumIds"]![0]!.GetValue<int>(), monitor["monitored"]!.GetValue<bool>()));
        Assert.Contains(_sent, s => s.Request == "DELETE /api/v1/queue/5");
        Assert.Contains(_sent, s => s.Request == "DELETE /api/v1/artist/3");
        Assert.NotNull((await InDbAsync(db => db.MediaRequests.SingleAsync())).FulfilledAt);
        var page = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/album/730000661"));
        Assert.Equal("None", page.GetProperty("availability").GetString());
    }

    [Fact]
    public async Task Unrequesting_ADownloadThatJustLeftTheQueue_StillCancels()
    {
        await MusicLibraryAsync();
        LidarrAwaitsMoisturizer();
        _lidarr.Responses.TryRemove("DELETE /api/v1/queue/5?removeFromClient=true&blocklist=false", out _);
        await AskedForMoisturizerAsync(addedArtist: false);

        var response = await Client().PostAsJsonAsync(Api("/requests/music/unrequest"), new { musicBrainzId = Moisturizer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotNull((await InDbAsync(db => db.MediaRequests.SingleAsync())).FulfilledAt);
    }

    [Fact]
    public async Task UndoWhileLidarrIsAway_SaysSo_AndChangesNothing()
    {
        var (_, artist, _) = await SeedOwnedAsync();
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new Core.Notifications.MediaRequest { ProfileId = "owner", Kind = MediaKind.Artist, Title = "Wet Leg", MusicBrainzId = WetLeg });
            return await db.SaveChangesAsync();
        });
        _lidarr.Responses.TryRemove("GET /api/v1/artist", out _);

        var unfollow = await Client().PostAsJsonAsync(Api("/requests/music/unfollow"), new { artistId = artist.Id });
        var requested = await Client().GetAsync(Api("/requests/music/requested"));

        Assert.Equal(HttpStatusCode.BadGateway, unfollow.StatusCode);
        Assert.Null((await InDbAsync(db => db.MediaRequests.SingleAsync())).FulfilledAt);
        Assert.Equal(HttpStatusCode.BadGateway, requested.StatusCode);
    }

    [Fact]
    public async Task AMissingAlbumWithNoCoverAnywhere_ShowsItsArtist()
    {
        var (_, artist, _) = await SeedOwnedAsync();
        _lidarr.Responses["GET /api/v1/artist"] = """[{"id":3,"artistName":"Wet Leg","monitored":true,"monitorNewItems":"none","images":[{"coverType":"poster","url":"/MediaCover/Artists/3/poster.jpg?lastWrite=1"}]}]""";
        _lidarr.Responses["GET /api/v1/album?artistId=3"] = """[{"id":12,"artistId":3,"title":"OnlySee","foreignAlbumId":"rg-onlysee","monitored":true,"releaseDate":"1997-01-01T00:00:00Z"}]""";

        var extras = await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/music/library-artist/{artist.Id}"));

        var onlySee = extras.GetProperty("missing").EnumerateArray().Single(m => m.GetProperty("title").GetString() == "OnlySee");
        Assert.StartsWith("/api/v1/images/arr?url=arr%3A%2F%2FLidarr%2Fmediacover%2Fartist%2F3%2Fposter.jpg", onlySee.GetProperty("image").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALidarrThatTimesOutOnARealQuestion_ReadsAsNotAnswering_NotAsNotFollowing()
    {
        var (_, artist, _) = await SeedOwnedAsync();
        _lidarr.Responses.TryRemove("GET /api/v1/artist", out _); // Lidarr pings, but its artist list fails

        var extras = await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/music/library-artist/{artist.Id}"));
        var status = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/music/status"));

        Assert.False(extras.GetProperty("lidarrAnswered").GetBoolean());
        Assert.False(status.GetProperty("reachable").GetBoolean());
    }

    [Fact]
    public async Task Unrequesting_TidiesAnArtistAnEarlierRequestAdded_WhicheverIsCancelledLast()
    {
        await MusicLibraryAsync();
        LidarrAwaitsMoisturizer();
        await AskedForMoisturizerAsync(addedArtist: false);
        await InDbAsync(async db =>
        {
            db.MediaRequests.Add(new Core.Notifications.MediaRequest
            {
                ProfileId = "owner", Kind = MediaKind.Album, MusicBrainzId = "rg-first", ArtistMusicBrainzId = WetLeg, Title = "Wet Leg", ArtistName = "Wet Leg",
                AddedArtist = true, FulfilledAt = DateTime.UtcNow,
            });
            return await db.SaveChangesAsync();
        });

        var response = await Client().PostAsJsonAsync(Api("/requests/music/unrequest"), new { musicBrainzId = Moisturizer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(_sent, s => s.Request == "DELETE /api/v1/artist/3");
    }

    [Fact]
    public async Task Unrequesting_KeepsAnArtistWithOtherMusic_OrThatItDidntAdd()
    {
        await MusicLibraryAsync();
        LidarrAwaitsMoisturizer(""",{"id":8,"artistId":3,"title":"Wet Leg","monitored":false,"statistics":{"trackFileCount":12,"trackCount":12,"totalTrackCount":12,"sizeOnDisk":1}}""");
        await AskedForMoisturizerAsync(addedArtist: true);

        var response = await Client().PostAsJsonAsync(Api("/requests/music/unrequest"), new { musicBrainzId = Moisturizer });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(_sent, s => s.Request == "DELETE /api/v1/artist/3");
        // Nothing to take back is already done.
        Assert.Equal(HttpStatusCode.NoContent, (await Client().PostAsJsonAsync(Api("/requests/music/unrequest"), new { musicBrainzId = "rg-nobody-asked" })).StatusCode);
    }
}

/// <summary>Stands in for MusicBrainz: a barcode search answers with the release saved for any barcode it asks about, else none.</summary>
internal sealed class FakeMusicBrainz : HttpMessageHandler
{
    public ConcurrentDictionary<string, string> Releases { get; } = new();

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = Uri.UnescapeDataString(request.RequestUri!.Query);
        var body = Releases.FirstOrDefault(r => query.Contains(r.Key.TrimStart('0'), StringComparison.Ordinal)).Value ?? """{"count":0,"releases":[]}""";
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
