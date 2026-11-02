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
            });
        });
        _lidarr.OnRequest = r => _sent.Enqueue(($"{r.Method} {r.RequestUri!.AbsolutePath}", r.Content is null ? null : JsonNode.Parse(r.Content.ReadAsStringAsync().Result)));
        _lidarr.Responses["GET /ping"] = "OK";
        _lidarr.Responses["GET /api/v1/rootfolder"] = """[{"id":1,"path":"/data/music","defaultQualityProfileId":2,"defaultMetadataProfileId":3}]""";
        _lidarr.Responses["GET /api/v1/artist"] = "[]";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wet%20Leg"] = File.ReadAllText(Fixture("lidarr-artist-lookup.json"));
        _lidarr.Responses["GET /api/v1/album/lookup?term=Wet%20Leg%20moisturizer"] = File.ReadAllText(Fixture("lidarr-album-lookup.json"));
        _lidarr.Responses["POST /api/v1/album"] = """{"id":9,"artistId":3,"title":"moisturizer"}""";
        _lidarr.Responses["POST /api/v1/artist"] = """{"id":3,"artistName":"Wet Leg"}""";
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
    public async Task FollowingAnArtist_WatchesForNewReleasesOnly()
    {
        await MusicLibraryAsync();
        var response = await Client().PostAsJsonAsync(Api("/requests/music"), new { kind = "artist", deezerId = 134334152 });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var add = _sent.Single(s => s.Request == "POST /api/v1/artist").Body!;
        Assert.Equal(("new", "future"), (add["monitorNewItems"]!.GetValue<string>(), add["addOptions"]!["monitor"]!.GetValue<string>()));
        Assert.False(add["addOptions"]!["searchForMissingAlbums"]!.GetValue<bool>());
        Assert.Equal(MediaKind.Artist, (await InDbAsync(db => db.MediaRequests.SingleAsync())).Kind);
    }

    [Fact]
    public async Task ALibraryArtist_GetsTheAlbumsItLacks_AndArtistsLikeThem()
    {
        var (_, artist, _) = await SeedOwnedAsync();

        var extras = await Client().GetFromJsonAsync<JsonElement>(Api($"/requests/music/library-artist/{artist.Id}"));

        var missing = Assert.Single(extras.GetProperty("missing").EnumerateArray());
        Assert.Equal(("moisturizer", 730000661L), (missing.GetProperty("title").GetString(), missing.GetProperty("deezerId").GetInt64()));
        Assert.Equal(["Yard Act", "Dry Cleaning"], extras.GetProperty("similar").EnumerateArray().Select(a => a.GetProperty("title").GetString()));
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
}
