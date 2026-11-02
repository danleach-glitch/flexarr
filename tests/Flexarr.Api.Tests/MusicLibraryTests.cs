using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>A music library's page as films and shows have it: the hero on Browse, every album, genres by album and a genre's tracks.</summary>
public class MusicLibraryTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task TheHero_LeadsWithTheAlbumPlayedLast_ResumingAtTheNextTrack_ThenArtistsWithFanart_ThenNewAlbums()
    {
        var music = await SeedAsync();

        var browse = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);

        Assert.NotNull(browse);
        // Portishead's Dummy was played last (its first track, heard through), so it carries on at the second. Massive
        // Attack (played most) comes next, then Tricky, never played but with fanart; Burial has none, so no slide.
        // Portishead are on the album's slide already. Then the two newest albums with a picture: not Untrue, as Burial
        // has no fanart.
        Assert.Equal(["Dummy", "Massive Attack", "Tricky", "Maxinquaye", "Portishead"], browse.Featured.Select(i => i.Title));
        Assert.Equal(MediaKind.Album, browse.Featured[0].Kind);
        Assert.Equal("Portishead", browse.Featured[0].SeriesTitle);
        var resume = Assert.Single(browse.Resumes!);
        Assert.Equal(browse.Featured[0].Id, resume.Key);
        Assert.Equal("Sour Times", resume.Value.Title);
        Assert.Equal("Bristol's finest.", browse.Overviews[browse.Featured[1].Id]);
    }

    [Fact]
    public async Task TheHero_ResumesInsideATrackLeftHalfWay_AndOffersNoResumeOnceTheAlbumIsDone()
    {
        var music = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            db.Add(new PlaybackState { MediaItemId = music.Tracks["Teardrop"], PositionSeconds = 80, LastPlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var halfWay = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        Assert.Equal("Mezzanine", halfWay!.Featured[0].Title);
        Assert.Equal("Teardrop", halfWay.Resumes![halfWay.Featured[0].Id].Title);
        Assert.Equal(80, halfWay.Resumes[halfWay.Featured[0].Id].UserData.PositionSeconds);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var state = db.PlaybackStates.Single(p => p.MediaItemId == music.Tracks["Teardrop"]);
            state.PositionSeconds = 0;
            state.Watched = true;
            await db.SaveChangesAsync();
        }

        // Teardrop is Mezzanine's last track here: the album is done, so its slide plays it from the start.
        var done = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        Assert.Equal("Mezzanine", done!.Featured[0].Title);
        Assert.Empty(done.Resumes!);
    }

    [Fact]
    public async Task TheHero_ResumesATrackStoppedTooEarlyForAPlace_AndStartsTheNextTrackFromTheTop()
    {
        var music = await SeedAsync();
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            // Sour Times was left half way weeks ago; Mysterons (before it) was heard through yesterday.
            db.Add(new PlaybackState { MediaItemId = music.Tracks["Sour Times"], PositionSeconds = 95, LastPlayedAt = DateTime.UtcNow.AddDays(-30) });
            await db.SaveChangesAsync();
        }

        var next = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        // The next track, from the top: its old place isn't where this listen got to.
        Assert.Equal("Sour Times", next!.Resumes![next.Featured[0].Id].Title);
        Assert.Equal(0, next.Resumes[next.Featured[0].Id].UserData.PositionSeconds);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            // Teardrop started and stopped 20 seconds in: too early for a place, not heard through.
            db.Add(new PlaybackState { MediaItemId = music.Tracks["Teardrop"], LastPlayedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var early = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);
        Assert.Equal("Mezzanine", early!.Featured[0].Title);
        Assert.Equal("Teardrop", early.Resumes![early.Featured[0].Id].Title);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AlbumsGenresAndTracks_KeepToWhatTheProfileMaySee(bool hidden)
    {
        var music = await SeedAsync();
        // Hidden from the profile, or behind its age limit (music has no rating, so it's hidden unless allowed).
        object settings = hidden
            ? new { name = $"Hidden {Guid.NewGuid():N}", hiddenLibraryIds = new[] { music.Library } }
            : new { name = $"Teen {Guid.NewGuid():N}", maxRatingAge = 15 };
        var profile = await (await Client().PostAsJsonAsync(Api("/profiles"), settings)).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{profile.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var genre = Uri.EscapeDataString(music.TripHop);

        var albums = await Client(token).GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums"), Json);
        var genres = await Client(token).GetFromJsonAsync<List<LibraryGenreDto>>(Api($"/libraries/{music.Library}/album-genres"), Json);
        var tracks = await Client(token).GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/libraries/{music.Library}/tracks?genre={genre}"), Json);

        Assert.Equal(0, albums!.Total);
        Assert.Empty(genres!);
        Assert.Empty(tracks!);
    }

    [Fact]
    public async Task ALibraryWithNoArtwork_HasNoHero()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Tagged Only", SortTitle = "tagged only" };
        var album = new MediaItem { LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = "From Tags", SortTitle = "from tags" };
        var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = "One", SortTitle = "001", TrackNumber = 1 };
        db.AddRange(library, artist, album, track);
        await db.SaveChangesAsync();
        db.Add(new PlaybackState { MediaItemId = track.Id, PlayCount = 1, Watched = true, LastPlayedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();

        var browse = await Client().GetFromJsonAsync<HomeDto>(Api($"/libraries/{library.Id}/browse"), Json);

        Assert.Empty(browse!.Featured);
        Assert.NotEmpty(browse.Rows);
    }

    [Fact]
    public async Task AProfileThatHasPlayedNothing_GetsArtistsWithFanart_AndNewAlbums_WithoutResume()
    {
        var music = await SeedAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"Sam {Guid.NewGuid():N}" })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var browse = await Client(token).GetFromJsonAsync<HomeDto>(Api($"/libraries/{music.Library}/browse"), Json);

        // Best-rated first, those with a logo ahead.
        Assert.Equal(["Massive Attack", "Portishead", "Tricky", "Maxinquaye", "Portishead"], browse!.Featured.Select(i => i.Title));
        Assert.Empty(browse.Resumes!);
    }

    [Fact]
    public async Task Albums_ListEveryAlbumInTheLibrary_SortedAndPaged_AndByGenreOnTheAlbumOrItsArtist()
    {
        var music = await SeedAsync();
        var client = Client();

        var byName = await client.GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums"), Json);
        var added = await client.GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums?sort=added&take=2"), Json);
        var year = await client.GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums?sort=year&skip=1&take=2"), Json);
        var trip = await client.GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums?genre={Uri.EscapeDataString(music.TripHop)}"), Json);

        Assert.Equal(6, byName!.Total);
        Assert.Equal(["Dummy", "Heligoland", "Maxinquaye", "Mezzanine", "Portishead", "Untrue"], byName.Items.Select(i => i.Title));
        Assert.All(byName.Items, i => Assert.Equal(MediaKind.Album, i.Kind));
        Assert.Equal("Portishead", byName.Items[0].SeriesTitle);
        Assert.Equal(6, added!.Total);
        Assert.Equal(["Untrue", "Maxinquaye"], added.Items.Select(i => i.Title));
        Assert.Equal(["Untrue", "Mezzanine"], year!.Items.Select(i => i.Title));
        // Massive Attack is tagged trip hop on the artist, Dummy on the album; Portishead (the album) has neither, and
        // Heligoland has a genre of its own.
        Assert.Equal(["Dummy", "Mezzanine"], trip!.Items.Select(i => i.Title));

        // By the album's title or its artist's name.
        var found = await client.GetFromJsonAsync<PagedDto<ItemSummaryDto>>(Api($"/libraries/{music.Library}/albums?search=porti"), Json);
        Assert.Equal(["Dummy", "Portishead"], found!.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task AlbumGenres_CountAlbums_ByTheirOwnGenres_OrTheirArtistsWhenTheyHaveNone_WithAnArtistsFanart()
    {
        var music = await SeedAsync();

        var genres = await Client().GetFromJsonAsync<List<LibraryGenreDto>>(Api($"/libraries/{music.Library}/album-genres"), Json);

        // Dubstep: Untrue (Burial's tag) and Heligoland (its own). Trip hop: Dummy (its own) and Mezzanine (Massive
        // Attack's), but not Heligoland, tagged itself.
        Assert.Equal([(music.Dubstep, 2), (music.TripHop, 2)], genres!.Select(g => (g.Name, g.Count)));
        Assert.All(genres!, g => Assert.NotNull(g.Backdrop));
    }

    [Fact]
    public async Task AGenresTracks_PlayInAlbumOrder_OrShuffled()
    {
        var music = await SeedAsync();
        var genre = Uri.EscapeDataString(music.TripHop);

        var inOrder = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/libraries/{music.Library}/tracks?genre={genre}"), Json);
        var shuffled = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/libraries/{music.Library}/tracks?genre={genre}&shuffle=true"), Json);
        var two = await Client().GetFromJsonAsync<List<ItemSummaryDto>>(Api($"/libraries/{music.Library}/tracks?genre={genre}&take=2"), Json);

        Assert.Equal(["Angel", "Teardrop", "Mysterons", "Sour Times"], inOrder!.Select(t => t.Title));
        Assert.Equal(inOrder!.Select(t => t.Title).Order(), shuffled!.Select(t => t.Title).Order());
        Assert.Equal(2, two!.Count);
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

    /// <summary>
    /// Bristol and beyond: Massive Attack (fanart, a logo, the best rated, played most), Portishead (fanart, played
    /// last), Tricky (fanart, never played) and Burial (no fanart). Trip hop is tagged on Massive Attack and on the
    /// album Dummy; dubstep on Burial. Untrue was added last, then Maxinquaye.
    /// </summary>
    private async Task<(Guid Library, string TripHop, string Dubstep, Dictionary<string, Guid> Tracks)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music };
        db.Add(library);
        var tag = Guid.NewGuid().ToString("N")[..6];
        var tripHop = new Genre { Name = $"Trip hop {tag}" };
        var dubstep = new Genre { Name = $"Dubstep {tag}" };

        var now = DateTime.UtcNow;
        MediaItem Artist(string name, bool fanart, double rating, bool logo = false) => new()
        {
            LibraryId = library.Id, Kind = MediaKind.Artist, Title = name, SortTitle = name.ToLowerInvariant(), CommunityRating = rating,
            BackdropUrl = fanart ? "https://example.com/fanart.jpg" : null, LogoUrl = logo ? "https://example.com/logo.png" : null,
            AddedAt = now.AddDays(-30),
        };

        var massive = Artist("Massive Attack", true, 9, logo: true);
        massive.Overview = "Bristol's finest.";
        massive.Genres.Add(tripHop);
        var portishead = Artist("Portishead", true, 8);
        var tricky = Artist("Tricky", true, 7);
        var burial = Artist("Burial", false, 9.5);
        burial.Genres.Add(dubstep);
        db.AddRange(massive, portishead, tricky, burial);

        var tracks = new Dictionary<string, Guid>();
        MediaItem Album(MediaItem artist, string title, int year, int addedDaysAgo, params string[] names)
        {
            var album = new MediaItem
            {
                LibraryId = library.Id, ParentId = artist.Id, Kind = MediaKind.Album, Title = title, SortTitle = title.ToLowerInvariant(), Year = year,
                AddedAt = now.AddDays(-addedDaysAgo),
            };
            db.Add(album);
            for (var n = 0; n < names.Length; n++)
            {
                var track = new MediaItem { LibraryId = library.Id, ParentId = album.Id, Kind = MediaKind.Track, Title = names[n], SortTitle = $"{n + 1:D3}", TrackNumber = n + 1 };
                db.Add(track);
                tracks[names[n]] = track.Id;
            }

            return album;
        }

        Album(massive, "Mezzanine", 1998, 20, "Angel", "Teardrop");
        // Tagged itself, so it isn't trip hop for being Massive Attack's.
        Album(massive, "Heligoland", 2010, 25, "Pray for Rain").Genres.Add(dubstep);
        var dummy = Album(portishead, "Dummy", 1994, 15, "Mysterons", "Sour Times");
        dummy.Genres.Add(tripHop);
        Album(portishead, "Portishead", 1997, 10, "Cowboys");
        Album(tricky, "Maxinquaye", 1995, 5, "Overcome");
        Album(burial, "Untrue", 2007, 1, "Archangel");
        await db.SaveChangesAsync();

        db.AddRange(
            new PlaybackState { MediaItemId = tracks["Angel"], PlayCount = 6, Watched = true, LastPlayedAt = now.AddDays(-3) },
            new PlaybackState { MediaItemId = tracks["Mysterons"], PlayCount = 2, Watched = true, LastPlayedAt = now.AddHours(-1) });
        await db.SaveChangesAsync();
        return (library.Id, tripHop.Name, dubstep.Name, tracks);
    }
}
