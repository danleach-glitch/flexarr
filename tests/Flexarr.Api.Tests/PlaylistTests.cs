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

    [Fact]
    public async Task APlaylistsSummary_CountsAndTimesEveryEntry_AndShowsTheFirstFourDistinctCovers()
    {
        Guid a1, a2, a5, ownCover;
        Guid full, empty;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var music = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
            db.Add(music);
            var artist = new MediaItem { LibraryId = music.Id, Kind = MediaKind.Artist, Title = "Artist", SortTitle = "artist" };
            db.Add(artist);
            MediaItem Album(string title, bool poster)
            {
                var album = new MediaItem { LibraryId = music.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = title, SortTitle = title, PosterUrl = poster ? $"https://covers.example/{title}.jpg" : null };
                db.Add(album);
                return album;
            }

            MediaItem Track(MediaItem album, string title, params double?[] durations)
            {
                var track = new MediaItem { LibraryId = music.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = title, SortTitle = title };
                foreach (var (d, i) in durations.Select((d, i) => (d, i)))
                {
                    track.Files.Add(new MediaFile { Path = $"/music/{music.Id}/{title}/{i}.flac", DurationSeconds = d });
                }

                db.Add(track);
                return track;
            }

            var (album1, album2, album3, album4, album5, album6) = (Album("A1", true), Album("A2", true), Album("A3", false), Album("A4", true), Album("A5", true), Album("A6", true));
            var t1 = Track(album1, "t1", 200);
            var t2 = Track(album2, "t2", 100, 300); // Two copies: the longer counts. It has a cover of its own.
            t2.PosterUrl = "https://covers.example/t2.jpg";
            var t3 = Track(album2, "t3", 200);
            var t4 = Track(album3, "t4", 50); // No cover anywhere.
            var t5 = Track(album4, "t5", [null]); // Length unknown.
            var t6 = Track(album5, "t6", 200);
            var t7 = Track(album6, "t7", 200);
            var t8 = Track(album1, "t8", 200);
            (a1, a2, a5, ownCover) = (album1.Id, album2.Id, album5.Id, t2.Id);

            var playlist = new Playlist { ProfileId = "owner", Name = $"Summary {Guid.NewGuid():N}", UpdatedAt = DateTime.UtcNow.AddYears(1) };
            var nothing = new Playlist { ProfileId = "owner", Name = $"Empty {Guid.NewGuid():N}", UpdatedAt = DateTime.UtcNow.AddYears(1).AddMinutes(-1) };
            db.AddRange(playlist, nothing);
            (full, empty) = (playlist.Id, nothing.Id);
            // Added out of order: the positions are what count.
            foreach (var (track, position) in new[] { (t7, 7), (t1, 4), (t8, 0), (t6, 5), (t3, 1), (t5, 6), (t4, 2), (t2, 3) })
            {
                db.PlaylistItems.Add(new PlaylistItem { PlaylistId = playlist.Id, MediaItemId = track.Id, Position = position });
            }

            await db.SaveChangesAsync();
        }

        var all = await Client().GetFromJsonAsync<List<PlaylistSummaryDto>>(Api("/playlists"), Json);
        var summary = Assert.Single(all!, p => p.Id == full);
        Assert.Equal(8, summary.TrackCount);
        Assert.Equal(200 + 200 + 50 + 300 + 200 + 200 + 0 + 200, summary.DurationSeconds);
        Assert.Equal([a1, a2, ownCover, a5], summary.Covers.Select(c => Guid.Parse(c.Split('/')[5])));
        var nothingIn = Assert.Single(all!, p => p.Id == empty);
        Assert.Equal((0, 0d), (nothingIn.TrackCount, nothingIn.DurationSeconds));
        Assert.Empty(nothingIn.Covers);
        // Most recently changed first.
        Assert.True(all!.FindIndex(p => p.Id == full) < all.FindIndex(p => p.Id == empty));
        Assert.Equal(summary, (await Client().GetFromJsonAsync<PlaylistDetailDto>(Api($"/playlists/{full}"), Json))!.Summary with { Covers = summary.Covers });
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
