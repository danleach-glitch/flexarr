using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>GET /items/{id}/tracks: everything an artist has, in the order a record collection plays.</summary>
public class ArtistTracksTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task AnArtistsTracks_ComeAlbumByAlbum_OldestFirst_ThenByDiscAndTrack()
    {
        var (artist, albums) = await SeedAsync();

        var tracks = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(new Uri($"/api/v1/items/{artist}/tracks", UriKind.Relative), Json);

        Assert.NotNull(tracks);
        Assert.Equal(
            ["Once Upon a Time in the West", "News", "Tunnel of Love", "Romeo and Juliet", "Calling Elvis", "On Every Street", "Heavy Fuel", "Early Demo"],
            tracks.Select(t => t.Title));
        Assert.All(tracks, t => Assert.Equal(MediaKind.Track, t.Kind));
        Assert.Equal("Dire Straits", tracks[0].SeriesTitle);

        // One album's tracks alone, too.
        var oneAlbum = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(new Uri($"/api/v1/items/{albums["On Every Street"]}/tracks", UriKind.Relative), Json);
        Assert.Equal(["Calling Elvis", "On Every Street", "Heavy Fuel"], oneAlbum!.Select(t => t.Title));
    }

    [Fact]
    public async Task OnlyArtistsAndAlbums_HaveTracks()
    {
        var (_, albums) = await SeedAsync();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var track = db.MediaItems.First(i => i.ParentId == albums["Communiqué"]);

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{track.Id}/tracks", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/items/{Guid.NewGuid()}/tracks", UriKind.Relative))).StatusCode);
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>Dire Straits with three albums (one of them double-disc, one with no year) and a few tracks each.</summary>
    private async Task<(Guid Artist, Dictionary<string, Guid> Albums)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Dire Straits", SortTitle = "dire straits" };
        db.AddRange(library, artist);

        var albums = new Dictionary<string, Guid>();
        void Album(string title, int? year, params (int Disc, int Track, string Title)[] tracks)
        {
            var album = new MediaItem { LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = title, SortTitle = title.ToLowerInvariant(), Year = year };
            db.Add(album);
            albums[title] = album.Id;
            foreach (var (disc, number, name) in tracks)
            {
                db.Add(new MediaItem
                {
                    LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = name,
                    SortTitle = SortTitle.ForNumber((disc * 1000) + number), DiscNumber = disc, TrackNumber = number,
                });
            }
        }

        // Added out of order on purpose: the endpoint sorts, not the database.
        Album("On Every Street", 1991, (2, 1, "Heavy Fuel"), (1, 2, "On Every Street"), (1, 1, "Calling Elvis"));
        Album("Making Movies", 1980, (1, 2, "Romeo and Juliet"), (1, 1, "Tunnel of Love"));
        Album("Unreleased", null, (1, 1, "Early Demo"));
        Album("Communiqué", 1979, (1, 2, "News"), (1, 1, "Once Upon a Time in the West"));
        await db.SaveChangesAsync();
        return (artist.Id, albums);
    }
}
