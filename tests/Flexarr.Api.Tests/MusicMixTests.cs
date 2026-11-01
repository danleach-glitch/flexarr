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

/// <summary>"Made for you" mixes over the API, their row on Home and Browse, and the New albums row on Home.</summary>
public class MusicMixTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Mixes_ClusterTheArtistsYouPlayByGenre_NamedAfterTheGenreOrTheArtists_AndStayTheSameAllDay()
    {
        var (listener, tag, library) = await NewListenerAsync();
        await SeedAsync(library, listener, tag, plays: [("Pulp", 10), ("Blur", 5), ("Oasis", 3), ("Mozart", 4), ("Bach", 2), ("Miles Davis", 1)]);
        var client = Client(listener.Token);

        var mixes = await client.GetFromJsonAsync<List<MixSummaryDto>>(Api("/music/mixes"), Json);

        Assert.NotNull(mixes);
        // Britpop (18 plays) leads; Rock has two of Britpop's three artists, so it's skipped; Classical follows; Miles
        // Davis has no genre, so he gets a mix of his own.
        Assert.Equal([$"Britpop {tag} Mix", $"Classical {tag} Mix", "Miles Davis Mix"], mixes.Select(m => m.Name));
        Assert.Equal(["Pulp, Blur & Oasis", "Mozart & Bach", "Made from the artists you play"], mixes.Select(m => m.Description));
        Assert.Equal([24, 16, 10], mixes.Select(m => m.TrackCount));
        // Pulp's two albums and Blur's have covers; Oasis's doesn't.
        Assert.Equal(3, mixes[0].Covers.Count);
        Assert.All(mixes[0].Covers, c => Assert.StartsWith("/api/v1/images/items/", c, StringComparison.Ordinal));

        var britpop = await client.GetFromJsonAsync<MixDetailDto>(Api($"/music/mixes/{mixes[0].Id}"), Json);
        var again = await client.GetFromJsonAsync<MixDetailDto>(Api($"/music/mixes/{mixes[0].Id}"), Json);

        Assert.NotNull(britpop);
        Assert.Equal(mixes[0].Id, britpop.Summary.Id);
        Assert.Equal(mixes[0].Name, britpop.Summary.Name);
        Assert.Equal(24, britpop.Tracks.Count);
        Assert.Equal(24, britpop.Tracks.DistinctBy(t => t.Id).Count());
        Assert.All(britpop.Tracks, t => Assert.Equal(MediaKind.Track, t.Kind));
        Assert.All(britpop.Tracks, t => Assert.Contains(t.SeriesTitle, new[] { "Pulp", "Blur", "Oasis" }));
        Assert.Equal(britpop.Tracks.Select(t => t.Id), again!.Tracks.Select(t => t.Id));
        Assert.Equal(10, britpop.Tracks.First(t => t.Title == $"Pulp {tag} 1 1").UserData.PlayCount);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Api("/music/mixes/nothing"))).StatusCode);
    }

    [Fact]
    public async Task TooLittleListening_MeansNoMixesAndNoRow_UntilTheListeningIsThere()
    {
        var (listener, tag, library) = await NewListenerAsync();
        await SeedAsync(library, listener, tag, plays: [("Pulp", 3), ("Blur", 2)]);
        var client = Client(listener.Token);

        Assert.Empty((await client.GetFromJsonAsync<List<MixSummaryDto>>(Api("/music/mixes"), Json))!);
        var home = await client.GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        Assert.DoesNotContain(home!.Rows, r => r.Id == "music-mixes");

        // More plays today: the empty answer wasn't kept, so the mixes arrive today rather than tomorrow.
        await PlayMoreAsync(library, listener.Id, "Pulp", 20);
        var mixes = await client.GetFromJsonAsync<List<MixSummaryDto>>(Api("/music/mixes"), Json);
        Assert.Equal([$"Britpop {tag} Mix"], mixes!.Select(m => m.Name));
    }

    [Fact]
    public async Task HomeAndBrowse_CarryTheMadeForYouRow_AndHomeOffersNewAlbums_NewestFirst()
    {
        var (listener, tag, library) = await NewListenerAsync();
        await SeedAsync(library, listener, tag, plays: [("Pulp", 10), ("Blur", 5), ("Oasis", 3), ("Mozart", 4), ("Bach", 2), ("Miles Davis", 1)]);
        var client = Client(listener.Token);

        var home = await client.GetFromJsonAsync<HomeDto>(Api("/home"), Json);
        var browse = await client.GetFromJsonAsync<HomeDto>(Api($"/libraries/{library}/browse"), Json);
        var layout = await Client().GetFromJsonAsync<LayoutDto>(Api("/settings/layout"), Json);

        var row = Assert.Single(home!.Rows, r => r.Id == "music-mixes");
        Assert.Equal("Made for you", row.Title);
        Assert.Equal("mixes", row.Style);
        Assert.Empty(row.Items);
        Assert.Equal([$"Britpop {tag} Mix", $"Classical {tag} Mix", "Miles Davis Mix"], row.Mixes!.Select(m => m.Name));
        Assert.Contains(browse!.Rows, r => r.Id == "music-mixes" && r.Mixes!.Count == 3);

        // New albums: on Browse as before, and on Home across music libraries, newest added first.
        var browseNew = Assert.Single(browse.Rows, r => r.Id == "recent");
        Assert.Equal("New albums", browseNew.Title);
        Assert.Equal([$"Miles {tag}", $"Bach {tag}", $"Mozart {tag}", $"Oasis {tag}", $"Blur {tag}", $"Pulp {tag} 2", $"Pulp {tag} 1"], browseNew.Items.Select(i => i.Title));
        var homeNew = Assert.Single(home.Rows, r => r.Id == "music-new");
        Assert.Equal("New albums", homeNew.Title);
        Assert.Equal("poster", homeNew.Style);
        Assert.Equal($"Miles {tag}", homeNew.Items[0].Title);
        Assert.All(homeNew.Items, i => Assert.Equal(MediaKind.Album, i.Kind));

        Assert.Contains(layout!.Home, r => r.Id == "music-mixes" && r.Enabled);
        Assert.Contains(layout.Home, r => r.Id == "music-new" && r.Enabled);
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

    /// <summary>A profile of its own for each test: a day's mixes are kept per profile, so tests mustn't share one.</summary>
    private async Task<((string Id, string Token) Listener, string Tag, Guid Library)> NewListenerAsync()
    {
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString()!;
        await factory.ShowEveryHomeRowAsync(id);
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString()!;

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        db.Add(library);
        await db.SaveChangesAsync();
        // Genre names are unique across the database, and each test seeds its own.
        return ((id, token), Guid.NewGuid().ToString("N")[..6], library.Id);
    }

    /// <summary>
    /// Britpop: Pulp (Britpop and Rock, two albums of six with covers), Blur (Britpop, six, with a cover) and Oasis
    /// (Britpop and Rock, six, no cover); Classical: Mozart and Bach (eight each); Miles Davis (ten, no genre). Albums
    /// were added a day apart, Pulp's first and Miles's last. The listener's plays all land on each artist's first track.
    /// </summary>
    private async Task SeedAsync(Guid library, (string Id, string Token) listener, string tag, (string Artist, int Plays)[] plays)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var britpop = new Genre { Name = $"Britpop {tag}" };
        var rock = new Genre { Name = $"Rock {tag}" };
        var classical = new Genre { Name = $"Classical {tag}" };
        var now = DateTime.UtcNow;
        var added = 0;

        MediaItem Artist(string name, params Genre[] genres)
        {
            var artist = new MediaItem { LibraryId = library, Kind = MediaKind.Artist, Title = name, SortTitle = name.ToLowerInvariant() };
            artist.Genres.AddRange(genres);
            db.Add(artist);
            return artist;
        }

        void Album(MediaItem artist, string title, int tracks, bool cover)
        {
            var album = new MediaItem
            {
                LibraryId = library, ParentId = artist.Id, Kind = MediaKind.Album, Title = title, SortTitle = title.ToLowerInvariant(), Year = 2000,
                PosterUrl = cover ? "https://example.test/cover.jpg" : null, AddedAt = now.AddDays(-7 + added++),
            };
            db.Add(album);
            db.AddRange(Enumerable.Range(1, tracks).Select(n => new MediaItem
            {
                LibraryId = library, ParentId = album.Id, Kind = MediaKind.Track, Title = $"{title} {n}", SortTitle = SortTitle.ForNumber(n), TrackNumber = n,
            }));
        }

        var pulp = Artist("Pulp", britpop, rock);
        Album(pulp, $"Pulp {tag} 1", 6, cover: true);
        Album(pulp, $"Pulp {tag} 2", 6, cover: true);
        Album(Artist("Blur", britpop), $"Blur {tag}", 6, cover: true);
        Album(Artist("Oasis", britpop, rock), $"Oasis {tag}", 6, cover: false);
        Album(Artist("Mozart", classical), $"Mozart {tag}", 8, cover: false);
        Album(Artist("Bach", classical), $"Bach {tag}", 8, cover: false);
        Album(Artist("Miles Davis"), $"Miles {tag}", 10, cover: false);
        await db.SaveChangesAsync();

        foreach (var (artist, count) in plays)
        {
            await PlayMoreAsync(library, listener.Id, artist, count, db);
        }
    }

    /// <summary>Plays an artist's first track <paramref name="count"/> more times, as the listener.</summary>
    private async Task PlayMoreAsync(Guid library, string listenerId, string artist, int count, FlexarrDbContext? db = null)
    {
        using var scope = db is null ? factory.Services.CreateScope() : null;
        db ??= scope!.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var track = db.MediaItems.Where(t => t.LibraryId == library && t.Kind == MediaKind.Track && t.Parent!.Parent!.Title == artist)
            .OrderBy(t => t.Parent!.AddedAt).ThenBy(t => t.TrackNumber).First();
        var state = db.PlaybackStates.SingleOrDefault(p => p.UserId == listenerId && p.MediaItemId == track.Id);
        if (state is null)
        {
            db.Add(new PlaybackState { UserId = listenerId, MediaItemId = track.Id, PlayCount = count, Watched = true, LastPlayedAt = DateTime.UtcNow });
        }
        else
        {
            state.PlayCount += count;
        }

        await db.SaveChangesAsync();
    }
}
