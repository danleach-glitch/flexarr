using System.Net;
using System.Net.Http.Json;
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

/// <summary>Fix match for albums and artists, and editing an album by hand, against a stand-in Lidarr and Deezer.</summary>
public sealed class MusicMatchTests : IAsyncLifetime
{
    private const string DeezerCover = "https://cdn-images.dzcdn.net/images/cover/8a22859ecaf89fcb363298f165377b83/1000x1000-000000-80-0-0.jpg";
    private const string Folder = "/Volumes/Media/data/music/Wet Leg/Moisturiser";

    private readonly FlexarrApiFactory _factory = new();
    private readonly FakeArr _lidarr = new();
    private readonly FakeArr _deezer = new();
    private WebApplicationFactory<Program> _server = null!;
    private Guid _artistId;
    private Guid _albumId;

    public async Task InitializeAsync()
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
                s.AddHttpClient<ImageCache>().ConfigurePrimaryHttpMessageHandler(() => new FakeArr());
            });
        });

        _lidarr.Responses["GET /api/v1/album?artistId=3"] = """
            [{"id":9,"artistId":3,"title":"moisturizer","releaseDate":"2025-07-11T00:00:00Z","albumType":"Album","images":[{"coverType":"cover","url":"/MediaCover/Albums/9/cover.jpg?lastWrite=1"}],"statistics":{"trackFileCount":12,"trackCount":12,"totalTrackCount":12,"sizeOnDisk":1}},
             {"id":10,"artistId":3,"title":"Wet Leg","releaseDate":"2022-04-08T00:00:00Z","albumType":"Album","images":[]}]
            """;
        _lidarr.Responses["GET /api/v1/album/lookup?term=Wetleg%20Moisturiser"] = "[]";
        _lidarr.Responses["GET /api/v1/album/10"] = """{"id":10,"artistId":3,"title":"Wet Leg","releaseDate":"2022-04-08T00:00:00Z","albumType":"Album","foreignAlbumId":"mb-wet-leg","images":[]}""";
        _lidarr.Responses["GET /api/v1/artist"] = """[{"id":3,"artistName":"Wet Leg","foreignArtistId":"mb-wetleg","monitored":true,"images":[]}]""";
        _lidarr.Responses["GET /api/v1/artist/lookup?term=Wetleg"] = "[]";
        _lidarr.Responses["GET /api/v1/artist/3"] = """{"id":3,"artistName":"Wet Leg","foreignArtistId":"mb-wetleg","monitored":true,"images":[]}""";
        _deezer.Responses["GET /search/album?q=Wetleg%20Moisturiser&limit=15"] =
            $$$"""{"data":[{"id":730000661,"title":"moisturizer","release_date":"2025-07-11","record_type":"album","nb_tracks":12,"cover_xl":"{{{DeezerCover}}}","artist":{"id":134334152,"name":"Wet Leg"}}]}""";
        _deezer.Responses["GET /album/730000661"] = File.ReadAllText(Path.Combine("Fixtures", "deezer-album.json"));
        _deezer.Responses["GET /search/artist?q=Wetleg&limit=15"] = """{"data":[{"id":134334152,"name":"Wet Leg","nb_album":14}]}""";

        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Wetleg", SortTitle = "wetleg", ArrId = 3, MetadataSource = "Lidarr" };
        var album = new MediaItem
        {
            LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = "Moisturiser", SortTitle = "moisturiser", Path = Folder,
            ArrId = 9, MetadataSource = "Lidarr", PosterUrl = $"embedded://{Folder}/01.flac", AccentColor = "#112233",
        };
        db.AddRange(library, artist, album);
        await db.SaveChangesAsync();
        (_artistId, _albumId) = (artist.Id, album.Id);
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
        _lidarr.Dispose();
        _deezer.Dispose();
    }

    [Fact]
    public async Task AlbumResults_AreLidarrsArtistsAlbumsThenDeezers_WithTheCurrentOneMarked()
    {
        var results = await Client().GetFromJsonAsync<List<MusicMatchDto>>(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative));

        // The album with files first, then the rest of the artist's, then Deezer's.
        Assert.Equal([("lidarr", "9", true), ("lidarr", "10", false), ("deezer", "730000661", false)], results!.Select(r => (r.Provider, r.Id, r.Current)));
        var lidarr = results!.Single(r => r.Id == "9");
        Assert.Equal(("arr://Lidarr/mediacover/album/9/cover.jpg?lastWrite=1", 12, AlbumKind.Album), (lidarr.CoverValue, lidarr.Tracks, lidarr.Type));
        Assert.StartsWith("/api/v1/images/arr?", lidarr.Cover, StringComparison.Ordinal);
        var deezer = results!.Single(r => r.Provider == "deezer");
        Assert.Equal((DeezerCover, "Wet Leg", 2025), (deezer.CoverValue, deezer.Artist, deezer.Year));
        Assert.StartsWith("/api/v1/images/remote?", deezer.Cover, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChoosingALidarrAlbum_LocksIt_KeepsTheTagsSpelling_AndChangesNothingInLidarr()
    {
        var response = await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative), new { provider = "lidarr", id = "10", instance = "Lidarr" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var album = await ItemAsync(_albumId);
        Assert.Equal(("Wet Leg", "Moisturiser", 10, "mb-wet-leg", true, false), (album.Title, album.OriginalTitle, album.ArrId, album.MusicBrainzId, album.MatchLocked, album.ArtworkLocked));
        Assert.DoesNotContain(_lidarr.Requests, r => !r.StartsWith("GET ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ChoosingADeezerAlbum_TakesItsDetailsAndCover()
    {
        var response = await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative), new { provider = "deezer", id = "730000661" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var album = await ItemAsync(_albumId);
        Assert.Equal(("moisturizer", "Moisturiser", "730000661", MetadataRefresher.Deezer, true, null), (album.Title, album.OriginalTitle, album.DeezerId, album.MetadataSource, album.MatchLocked, album.ArrId));
        Assert.Equal(DeezerCover, album.PosterUrl);
    }

    [Fact]
    public async Task TheCoverCanBeKept_OrChosenFromAnotherResult_AndIsLockedThen()
    {
        await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative), new { provider = "deezer", id = "730000661", cover = "current" });
        var kept = await ItemAsync(_albumId);
        await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative),
            new { provider = "lidarr", id = "10", instance = "Lidarr", cover = DeezerCover });
        var chosen = await ItemAsync(_albumId);
        var refused = await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative),
            new { provider = "deezer", id = "730000661", cover = "https://example.com/cover.jpg" });

        Assert.Equal(($"embedded://{Folder}/01.flac", "#112233", true), (kept.PosterUrl, kept.AccentColor, kept.ArtworkLocked));
        Assert.Equal((DeezerCover, true, "Wet Leg"), (chosen.PosterUrl, chosen.ArtworkLocked, chosen.Title));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
    }

    [Fact]
    public async Task ChoosingAnArtist_NamesIt_AndNeverFollowsOrAddsAnything()
    {
        var results = await Client().GetFromJsonAsync<List<MusicMatchDto>>(new Uri($"/api/v1/items/{_artistId}/music-match", UriKind.Relative));
        var response = await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_artistId}/music-match", UriKind.Relative), new { provider = "lidarr", id = "3", instance = "Lidarr" });

        Assert.Equal([("lidarr", "3", true, true), ("deezer", "134334152", false, false)], results!.Select(r => (r.Provider, r.Id, r.Current, r.Followed)));
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var artist = await ItemAsync(_artistId);
        Assert.Equal(("Wet Leg", "Wetleg", "mb-wetleg", true), (artist.Title, artist.OriginalTitle, artist.MusicBrainzId, artist.MatchLocked));
        Assert.DoesNotContain(_lidarr.Requests, r => !r.StartsWith("GET ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MatchAutomaticallyAgain_HandsTheAlbumBackToItsTags()
    {
        await Client().PostAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative), new { provider = "deezer", id = "730000661" });

        var response = await Client().DeleteAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var album = await ItemAsync(_albumId);
        Assert.Equal(("Moisturiser", false, null, MetadataRefresher.Tags, null), (album.Title, album.MatchLocked, album.DeezerId, album.MetadataSource, album.PosterUrl));
    }

    [Fact]
    public async Task AnAlbumEditedByHand_KeepsTheTagsSpelling_AndRefusesAnUnknownType()
    {
        var edit = await Client().PutAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/album-details", UriKind.Relative),
            new { title = "Moisturizer", year = 2025, genres = new[] { "Indie Rock" }, albumType = "album" });
        var refused = await Client().PutAsJsonAsync(new Uri($"/api/v1/items/{_albumId}/album-details", UriKind.Relative), new { title = "Moisturizer", albumType = "mixtape" });

        Assert.Equal(HttpStatusCode.NoContent, edit.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        var album = await ItemAsync(_albumId);
        Assert.Equal(("Moisturizer", "Moisturiser", 2025, AlbumKind.Album, true), (album.Title, album.OriginalTitle, album.Year, album.AlbumType, album.DetailsLocked));
    }

    [Fact]
    public async Task FixMatch_NeedsTheMainProfile()
    {
        var response = await _server.CreateClient().GetAsync(new Uri($"/api/v1/items/{_albumId}/music-match", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private HttpClient Client()
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<MediaItem> ItemAsync(Guid id)
    {
        using var scope = _server.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AsNoTracking().FirstAsync(i => i.Id == id);
    }
}
