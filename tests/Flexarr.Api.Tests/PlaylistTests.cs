using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playlists;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A profile's music playlists: making them, filling them, ordering them, and keeping them to the profile.</summary>
public class PlaylistTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task APlaylist_TakesTracksAndWholeAlbums_InTheOrderAdded_AndCanBeReorderedAndTrimmed()
    {
        var music = await SeedAsync();
        var client = Client();

        var created = await (await client.PostAsJsonAsync(Api("/playlists"), new { name = "  Road trip  " })).Content.ReadFromJsonAsync<PlaylistSummaryDto>(Json);
        Assert.NotNull(created);
        Assert.Equal("Road trip", created.Name);
        Assert.Equal(0, created.TrackCount);

        // A track, then a whole album (its tracks in album order), then a track already in it (allowed: a playlist may repeat).
        var added = await (await client.PostAsJsonAsync(Api($"/playlists/{created.Id}/items"), new { itemIds = new[] { music.Tender, music.DifferentClass, music.Tender } }))
            .Content.ReadFromJsonAsync<PlaylistAddedDto>(Json);
        Assert.Equal(4, added!.Added);

        var detail = await client.GetFromJsonAsync<PlaylistDetailDto>(Api($"/playlists/{created.Id}"), Json);
        Assert.NotNull(detail);
        Assert.Equal(["Tender", "Common People", "Disco 2000", "Tender"], detail.Tracks.Select(t => t.Track.Title));
        Assert.All(detail.Tracks, t => Assert.Equal(MediaKind.Track, t.Track.Kind));
        Assert.Equal(4, detail.Summary.TrackCount);
        Assert.Equal(4 * 200, detail.Summary.DurationSeconds);
        // The mosaic: each album's cover once, first tracks first.
        Assert.Equal([$"/api/v1/images/items/{music.ThirteenAlbum}/poster?v=0", $"/api/v1/images/items/{music.DifferentClass}/poster?v=0"], detail.Summary.Covers);
        Assert.Null(detail.Summary.Cover);

        // Move the last Tender to the top, then drop the first Common People.
        var entries = detail.Tracks.Select(t => t.EntryId).ToList();
        (await client.PutAsJsonAsync(Api($"/playlists/{created.Id}/items/{entries[3]}/position"), new { position = 0 })).EnsureSuccessStatusCode();
        Assert.Equal(["Tender", "Tender", "Common People", "Disco 2000"], await Titles(client, created.Id));
        (await client.DeleteAsync(Api($"/playlists/{created.Id}/items/{entries[1]}"))).EnsureSuccessStatusCode();
        Assert.Equal(["Tender", "Tender", "Disco 2000"], await Titles(client, created.Id));

        // Past the end means last; positions are kept without gaps.
        (await client.PutAsJsonAsync(Api($"/playlists/{created.Id}/items/{entries[0]}/position"), new { position = 99 })).EnsureSuccessStatusCode();
        Assert.Equal(["Tender", "Disco 2000", "Tender"], await Titles(client, created.Id));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.Equal([0, 1, 2], db.PlaylistItems.Where(i => i.PlaylistId == created.Id).OrderBy(i => i.Position).Select(i => i.Position).ToList());
        }

        // Duplicates can be skipped; an artist brings every track they have.
        var skipped = await (await client.PostAsJsonAsync(Api($"/playlists/{created.Id}/items"), new { itemIds = new[] { music.Pulp }, skipDuplicates = true }))
            .Content.ReadFromJsonAsync<PlaylistAddedDto>(Json);
        Assert.Equal(2, skipped!.Added); // Common People (removed above) and Help the Aged; Disco 2000 is already there
        Assert.Equal(["Tender", "Disco 2000", "Tender", "Common People", "Help the Aged"], await Titles(client, created.Id));

        // Only music goes in.
        var film = await (await client.PostAsJsonAsync(Api($"/playlists/{created.Id}/items"), new { itemIds = new[] { music.Film } })).Content.ReadAsStringAsync();
        Assert.Contains("Only tracks, albums and artists", film);
    }

    [Fact]
    public async Task APlaylist_CanBeRenamed_GivenACover_AndDeleted()
    {
        var music = await SeedAsync();
        var client = Client();
        var created = await (await client.PostAsJsonAsync(Api("/playlists"), new { name = "Mix", itemIds = new[] { music.DifferentClass } })).Content.ReadFromJsonAsync<PlaylistSummaryDto>(Json);
        Assert.Equal(2, created!.TrackCount);

        var renamed = await (await client.PutAsJsonAsync(Api($"/playlists/{created.Id}"), new { name = "Britpop", coverItemId = music.ThirteenAlbum })).Content.ReadFromJsonAsync<PlaylistSummaryDto>(Json);
        Assert.Equal("Britpop", renamed!.Name);
        Assert.Equal($"/api/v1/images/items/{music.ThirteenAlbum}/poster?v=0", renamed.Cover);

        var cleared = await (await client.PutAsJsonAsync(Api($"/playlists/{created.Id}"), new { clearCover = true })).Content.ReadFromJsonAsync<PlaylistSummaryDto>(Json);
        Assert.Equal("Britpop", cleared!.Name);
        Assert.Null(cleared.Cover);

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(Api($"/playlists/{created.Id}"), new { name = "   " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync(Api("/playlists"), new { name = "" })).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Api($"/playlists/{created.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api($"/playlists/{created.Id}"))).StatusCode);
    }

    [Fact]
    public async Task Playlists_AreTheProfilesOwn_AndTheHomeRowShowsOnlyForAProfileThatHasAny()
    {
        var music = await SeedAsync();
        var mine = await (await Client().PostAsJsonAsync(Api("/playlists"), new { name = $"Mine {Guid.NewGuid():N}", itemIds = new[] { music.Tender } })).Content.ReadFromJsonAsync<PlaylistSummaryDto>(Json);
        var sam = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{sam.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var samsClient = Client(token);

        // Sam neither sees nor touches it.
        Assert.DoesNotContain(await samsClient.GetFromJsonAsync<List<PlaylistSummaryDto>>(Api("/playlists"), Json) ?? [], p => p.Id == mine!.Id);
        Assert.Equal(HttpStatusCode.NotFound, (await samsClient.GetAsync(Api($"/playlists/{mine!.Id}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await samsClient.PutAsJsonAsync(Api($"/playlists/{mine.Id}"), new { name = "Sam's now" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await samsClient.PostAsJsonAsync(Api($"/playlists/{mine.Id}/items"), new { itemIds = new[] { music.Tender } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await samsClient.DeleteAsync(Api($"/playlists/{mine.Id}"))).StatusCode);
        Assert.Contains(await Client().GetFromJsonAsync<List<PlaylistSummaryDto>>(Api("/playlists"), Json) ?? [], p => p.Id == mine.Id && p.Name == mine.Name);

        // The main profile's Home and the music library's Browse carry the row, linking to the playlists page; Sam's don't.
        var home = await Client().GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        var browse = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        var samsHome = await Client(token).GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        var layout = await Client().GetFromJsonAsync<LayoutDto>(Api("/settings/layout"), Json);

        var row = Assert.Single(home!.Rows, r => r.Id == "music-playlists");
        Assert.Equal(("Your playlists", "playlists", "/playlists"), (row.Title, row.Style, row.Link));
        Assert.Empty(row.Items);
        Assert.Contains(row.Playlists!, p => p.Id == mine.Id);
        Assert.Equal("music-playlists", browse!.Rows[0].Id);
        Assert.DoesNotContain(samsHome!.Rows, r => r.Id == "music-playlists");
        Assert.Contains(layout!.Home, r => r.Id == "music-playlists");
    }

    private HttpClient Client(string? profileToken = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private static async Task<string[]> Titles(HttpClient client, Guid id) =>
        (await client.GetFromJsonAsync<PlaylistDetailDto>(Api($"/playlists/{id}"), Json))!.Tracks.Select(t => t.Track.Title).ToArray();

    private sealed record Seeded(Guid Library, Guid Pulp, Guid DifferentClass, Guid ThirteenAlbum, Guid Tender, Guid Film);

    /// <summary>Pulp (Different Class: Common People, Disco 2000; This Is Hardcore: Help the Aged), Blur (13: Tender), all with covers and 200-second files, and a film.</summary>
    private async Task<Seeded> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        db.AddRange(library, films);

        MediaItem Item(MediaKind kind, string title, MediaItem? parent = null, int? year = null) =>
            new() { LibraryId = library.Id, ParentId = parent?.Id, Kind = kind, Title = title, SortTitle = title.ToLowerInvariant(), Year = year };

        var pulp = Item(MediaKind.Artist, "Pulp");
        var blur = Item(MediaKind.Artist, "Blur");
        db.AddRange(pulp, blur);

        MediaItem Album(MediaItem artist, string title, int year, params string[] tracks)
        {
            var album = Item(MediaKind.Album, title, artist, year);
            album.PosterUrl = $"https://covers.example/{title}.jpg";
            db.Add(album);
            foreach (var (t, i) in tracks.Select((t, i) => (t, i)))
            {
                var track = Item(MediaKind.Track, t, album);
                track.TrackNumber = i + 1;
                track.SortTitle = SortTitle.ForNumber(i + 1);
                track.Files.Add(new MediaFile { Path = $"/music/{library.Id}/{title}/{t}.flac", DurationSeconds = 200 });
                db.Add(track);
            }

            return album;
        }

        var differentClass = Album(pulp, "Different Class", 1995, "Common People", "Disco 2000");
        Album(pulp, "This Is Hardcore", 1998, "Help the Aged");
        var thirteen = Album(blur, "13", 1999, "Tender");
        var film = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "A Film", SortTitle = "a film" };
        db.Add(film);
        await db.SaveChangesAsync();
        var tender = db.MediaItems.Single(i => i.ParentId == thirteen.Id);
        return new Seeded(library.Id, pulp.Id, differentClass.Id, thirteen.Id, tender.Id, film.Id);
    }
}
