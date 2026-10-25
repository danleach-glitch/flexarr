using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Music rows on Home and a music library's Browse, artist radio, and albums and tracks in search.</summary>
public class MusicDiscoveryTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task AMusicLibrarysBrowse_ShowsRecentlyPlayedAlbums_MostPlayedTracks_AndUnplayedAlbumsByFavouriteArtists()
    {
        var music = await SeedListeningAsync();

        var home = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);

        Assert.NotNull(home);
        Assert.Empty(home.Featured);
        Assert.Equal(["music-recent", "music-top", "music-artists", "recent"], home.Rows.Select(r => r.Id));

        var recent = home.Rows[0];
        Assert.Equal("Recently played", recent.Title);
        Assert.Equal(["Different Class", "Parklife", "This Is Hardcore"], recent.Items.Select(i => i.Title));
        Assert.All(recent.Items, i => Assert.Equal(MediaKind.Album, i.Kind));

        var top = home.Rows[1];
        Assert.Equal("tracks", top.Style);
        Assert.Equal(["Common People", "Girls & Boys", "Disco 2000", "Help the Aged"], top.Items.Select(i => i.Title));
        Assert.Equal("Pulp", top.Items[0].SeriesTitle);
        Assert.Equal(5, top.Items[0].UserData.PlayCount);

        // Pulp (8 plays) and Blur (3) take turns; albums played in the last month are left out, as is Oasis, never played.
        var more = home.Rows[2];
        Assert.Equal("More from artists you play", more.Title);
        Assert.Equal(["This Is Hardcore", "13", "His 'n' Hers"], more.Items.Select(i => i.Title));

        Assert.Equal("New albums", home.Rows[3].Title);
        Assert.Equal(7, home.Rows[3].Items.Count);
    }

    [Fact]
    public async Task TheRowsAreTheProfilesOwn_AndHomeCarriesThem_WhenTheProfileHasAMusicLibrary()
    {
        var music = await SeedListeningAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var sam = await Client(token).GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        var mainHome = await Client().GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        var samHome = await Client(token).GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        var layout = await Client().GetFromJsonAsync<LayoutDto>(Api("/settings/layout"), Json);

        // Sam hasn't played anything: only the library's new albums.
        Assert.Equal(["recent"], sam!.Rows.Select(r => r.Id));
        Assert.Contains(mainHome!.Rows, r => r.Id == "music-top" && r.Items.Any(i => i.Title == "Common People"));
        Assert.Contains(mainHome.Rows, r => r.Id == "music-recent" && r.Items.Any(i => i.Title == "Different Class"));
        // New albums doesn't depend on listening, so it's the one music row Sam gets.
        Assert.DoesNotContain(samHome!.Rows, r => r.Id.StartsWith("music-", StringComparison.Ordinal) && r.Id != "music-new");
        Assert.Contains(samHome.Rows, r => r.Id == "music-new" && r.Items.Any(i => i.Title == "Different Class"));
        Assert.Contains(layout!.Home, r => r.Id == "music-top");
    }

    /// <summary>
    /// A track left half played is music's business (Recently played), not Continue Watching's: an album in the Home
    /// hero with a Resume button would be wrong, and on Apple TV it took Home over.
    /// </summary>
    [Fact]
    public async Task ContinueWatching_AndTheHomeHero_LeaveMusicAlone()
    {
        var music = await SeedListeningAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var track = await db.MediaItems.SingleAsync(i => i.LibraryId == music.Library && i.Title == "Babies");
            db.Add(new PlaybackState { MediaItemId = track.Id, PositionSeconds = 90, Watched = false, LastPlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var continueWatching = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(Api("/home/continue"), Json);
        var home = await Client().GetFromJsonAsync<HomeDto>(Api("/home"), Json);

        Assert.DoesNotContain(continueWatching!, i => i.Title == "Babies");
        Assert.DoesNotContain(home!.Featured, i => i.Kind is MediaKind.Album or MediaKind.Track or MediaKind.Artist);
        Assert.DoesNotContain(home.Rows.Where(r => r.Id == "continue").SelectMany(r => r.Items), i => i.Title == "Babies");
    }

    [Fact]
    public async Task ArtistRadio_PlaysTheArtistAndOthersSharingTheirGenres_TheSameWayForTheSameSeed()
    {
        var (radiohead, muse, mozart, album, track) = await SeedGenresAsync();
        var client = Client();

        var station = await client.GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/items/{radiohead}/radio?seed=7"), Json);
        var again = await client.GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/items/{radiohead}/radio?seed=7"), Json);
        var fromAlbum = await client.GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/items/{album}/radio?seed=7"), Json);

        Assert.NotNull(station);
        Assert.Equal(12, station.Count);
        Assert.Equal(station.Count, station.DistinctBy(t => t.Id).Count());
        Assert.All(station, t => Assert.Equal(MediaKind.Track, t.Kind));
        Assert.Equal("Radiohead", station[0].SeriesTitle);
        Assert.Contains(station, t => t.SeriesId == muse);
        Assert.DoesNotContain(station, t => t.SeriesId == mozart);
        Assert.Equal(station.Select(t => t.Id), again!.Select(t => t.Id));
        Assert.Equal(station.Select(t => t.Id), fromAlbum!.Select(t => t.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api($"/items/{track}/radio"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api($"/items/{Guid.NewGuid()}/radio"))).StatusCode);
    }

    [Fact]
    public async Task Search_FindsAlbumsAndTracks_NamingTheirArtistAndAlbum()
    {
        // A word of its own: the other tests here seed orchestras of their own into the same database.
        var word = $"Zylo{Guid.NewGuid():N}"[..12];
        await SeedListeningAsync(word);

        var results = await Client().GetFromJsonAsync<SearchResultsDto>(Api($"/search?q={word}"), Json);

        Assert.NotNull(results);
        var artist = Assert.Single(results.Titles);
        Assert.Equal(MediaKind.Artist, artist.Kind);
        Assert.Equal($"{word} Orchestra", artist.Title);

        var album = Assert.Single(results.Albums);
        Assert.Equal($"{word} Nights", album.Title);
        Assert.Equal($"{word} Orchestra", album.SeriesTitle);
        Assert.Equal(artist.Id, album.SeriesId);

        var track = Assert.Single(results.Tracks);
        Assert.Equal($"{word} Dream", track.Item.Title);
        Assert.Equal($"{word} Nights", track.Album);
        Assert.Equal($"{word} Orchestra", track.Item.SeriesTitle);
        Assert.Equal(album.Id, track.Item.ParentId);
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

    private static MediaItem Item(Guid library, MediaKind kind, string title, Guid? parent = null, int? year = null) =>
        new() { LibraryId = library, ParentId = parent, Kind = kind, Title = title, SortTitle = title.ToLowerInvariant(), Year = year };

    /// <summary>
    /// Britpop, with the main profile's listening: Pulp played most and lately, Blur a little, Oasis never; one Pulp
    /// album last played over a month ago. Plus an orchestra nobody searches for by accident.
    /// </summary>
    /// <param name="word">What the orchestra, its album and its track are called, for search to find.</param>
    private async Task<(Guid Library, Guid Pulp)> SeedListeningAsync(string word = "Zylophonic")
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        db.Add(library);

        var now = DateTime.UtcNow;
        var pulp = Item(library.Id, MediaKind.Artist, "Pulp");
        var blur = Item(library.Id, MediaKind.Artist, "Blur");
        var oasis = Item(library.Id, MediaKind.Artist, "Oasis");
        var orchestra = Item(library.Id, MediaKind.Artist, $"{word} Orchestra");
        db.AddRange(pulp, blur, oasis, orchestra);

        MediaItem Album(MediaItem artist, string title, int year, params string[] tracks)
        {
            var album = Item(library.Id, MediaKind.Album, title, artist.Id, year);
            db.Add(album);
            db.AddRange(tracks.Select((t, i) => Item(library.Id, MediaKind.Track, t, album.Id).Numbered(i + 1)));
            return album;
        }

        Album(pulp, "Different Class", 1995, "Common People", "Disco 2000");
        Album(pulp, "This Is Hardcore", 1998, "Help the Aged");
        Album(pulp, "His 'n' Hers", 1994, "Babies");
        Album(blur, "Parklife", 1994, "Girls & Boys", "Parklife");
        Album(blur, "13", 1999, "Tender");
        Album(oasis, "Definitely Maybe", 1994, "Supersonic");
        Album(orchestra, $"{word} Nights", 2001, $"{word} Dream");
        await db.SaveChangesAsync();

        void Played(string track, int count, TimeSpan ago) => db.Add(new PlaybackState
        {
            MediaItemId = db.MediaItems.Single(i => i.LibraryId == library.Id && i.Title == track).Id, PlayCount = count, Watched = true, LastPlayedAt = now - ago,
        });

        Played("Common People", 5, TimeSpan.FromDays(1));
        Played("Disco 2000", 2, TimeSpan.FromDays(2));
        Played("Girls & Boys", 3, TimeSpan.FromDays(3));
        Played("Help the Aged", 1, TimeSpan.FromDays(40));
        await db.SaveChangesAsync();
        return (library.Id, pulp.Id);
    }

    /// <summary>Radiohead (Alternative, Rock), Muse (Alternative, on the album only) and Mozart (Classical), six, six and three tracks.</summary>
    private async Task<(Guid Radiohead, Guid Muse, Guid Mozart, Guid Album, Guid Track)> SeedGenresAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        db.Add(library);

        // Genre names are unique across the database; these tests share one.
        var tag = Guid.NewGuid().ToString("N")[..6];
        var alternative = new Genre { Name = $"Alternative {tag}" };
        var rock = new Genre { Name = $"Rock {tag}" };
        var classical = new Genre { Name = $"Classical {tag}" };

        var radiohead = Item(library.Id, MediaKind.Artist, "Radiohead");
        radiohead.Genres.AddRange([alternative, rock]);
        var muse = Item(library.Id, MediaKind.Artist, "Muse");
        var mozart = Item(library.Id, MediaKind.Artist, "Mozart");
        mozart.Genres.Add(classical);
        db.AddRange(radiohead, muse, mozart);

        MediaItem Album(MediaItem artist, string title, int tracks, Genre? genre = null)
        {
            var album = Item(library.Id, MediaKind.Album, title, artist.Id, 2000);
            if (genre is not null)
            {
                album.Genres.Add(genre);
            }

            db.Add(album);
            db.AddRange(Enumerable.Range(1, tracks).Select(n => Item(library.Id, MediaKind.Track, $"{title} {n}", album.Id).Numbered(n)));
            return album;
        }

        var okComputer = Album(radiohead, "OK Computer", 6);
        Album(muse, "Absolution", 6, alternative);
        Album(mozart, "Requiem", 3);
        await db.SaveChangesAsync();
        var track = db.MediaItems.First(i => i.ParentId == okComputer.Id);
        return (radiohead.Id, muse.Id, mozart.Id, okComputer.Id, track.Id);
    }
}

file static class TrackNumbering
{
    /// <summary>Gives a track its number and a sort key that orders by it.</summary>
    public static MediaItem Numbered(this MediaItem track, int number)
    {
        track.TrackNumber = number;
        track.SortTitle = SortTitle.ForNumber(number);
        return track;
    }
}
