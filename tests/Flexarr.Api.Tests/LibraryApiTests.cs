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

public class LibraryApiTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Fact]
    public async Task CreateLibrary_PersistsAndQueuesScan()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "movies-" + Guid.NewGuid())).FullName;

        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Movies", type = "Movies", folders = new[] { folder + "/" } });

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var created = await response.Content.ReadFromJsonAsync<LibraryDto>(Json);
        Assert.Equal([folder], created!.Folders);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == folder);

        var all = await Client().GetFromJsonAsync<List<LibraryDto>>(new Uri("/api/v1/libraries", UriKind.Relative), Json);
        Assert.Contains(all!, l => l.Id == created.Id);
    }

    [Fact]
    public async Task UpdateLibrary_RenamesWithoutRescanning_AndScansOnlyAddedFolders()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "films-" + Guid.NewGuid())).FullName;
        var extra = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "films-" + Guid.NewGuid())).FullName;
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Films", type = "Movies", folders = new[] { folder } })).Content.ReadFromJsonAsync<LibraryDto>(Json);
        var before = factory.ScanQueue.Requests.Count;

        var renamed = await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{created!.Id}", UriKind.Relative),
            new { name = "  Kids' films ", type = "Movies", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
        Assert.Equal("Kids' films", (await renamed.Content.ReadFromJsonAsync<LibraryDto>(Json))!.Name);
        Assert.Equal(before, factory.ScanQueue.Requests.Count);

        await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{created.Id}", UriKind.Relative),
            new { name = "Kids' films", type = "Movies", folders = new[] { folder, extra } });

        Assert.Equal([extra], factory.ScanQueue.Requests.Skip(before).Select(r => r.Path));
    }

    [Fact]
    public async Task RemovingAFolder_HidesItsTitles_WhichWaitOutTheGracePeriod()
    {
        var gone = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "music-" + Guid.NewGuid())).FullName;
        var kept = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "music-" + Guid.NewGuid())).FullName;
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = $"Music {Guid.NewGuid():N}", type = "Music", folders = new[] { gone, kept } })).Content.ReadFromJsonAsync<LibraryDto>(Json);
        var id = created!.Id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            MediaItem Track(string folder, string artistName, bool missing)
            {
                var artist = new MediaItem { LibraryId = id, Kind = MediaKind.Artist, Title = artistName, SortTitle = artistName };
                var album = new MediaItem { LibraryId = id, Kind = MediaKind.Album, Title = "Album", SortTitle = "album", ParentId = artist.Id, Path = $"{folder}/{artistName}/Album" };
                var track = new MediaItem { LibraryId = id, Kind = MediaKind.Track, Title = "Song", SortTitle = "song", ParentId = album.Id, Path = $"{folder}/{artistName}/Album/01.flac" };
                track.Files.Add(new MediaFile { Path = track.Path, MissingSince = missing ? DateTime.UtcNow : null });
                db.AddRange(artist, album, track);
                return artist;
            }

            Track(gone, "Leaving", missing: false);
            Track(gone, "Already missing", missing: true);
            Track(kept, "Staying", missing: false);
            await db.SaveChangesAsync();
        }

        var response = await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{id}", UriKind.Relative),
            new { name = created.Name, type = "Music", folders = new[] { kept } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            // Nothing deleted yet: put back within the grace period, they'd be as they were.
            var showing = await db.MediaItems.IgnoreQueryFilters().Where(i => i.LibraryId == id && i.MissingSince == null).Select(i => i.Title).ToListAsync();
            Assert.Equal(["Album", "Song", "Staying"], showing.Order(StringComparer.Ordinal));
            Assert.Equal(9, await db.MediaItems.IgnoreQueryFilters().CountAsync(i => i.LibraryId == id));
            Assert.False(await db.MediaFiles.IgnoreQueryFilters().AnyAsync(f => f.Path.StartsWith(gone + "/") && f.MissingSince == null));
        }
    }

    [Fact]
    public async Task SwappingAFolderForAnotherWithTheSameFiles_KeepsEverythingAboutThem()
    {
        var old = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "music-" + Guid.NewGuid())).FullName;
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = $"Music {Guid.NewGuid():N}", type = "Music", folders = new[] { old } })).Content.ReadFromJsonAsync<LibraryDto>(Json);
        var id = created!.Id;
        // The same music, mounted somewhere new; one track didn't come across.
        var moved = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "music-" + Guid.NewGuid())).FullName;
        Directory.CreateDirectory(Path.Combine(moved, "Artist", "Album"));
        await File.WriteAllTextAsync(Path.Combine(moved, "Artist", "Album", "01.flac"), "sound");
        await File.WriteAllTextAsync(Path.Combine(moved, "Artist", "Album", "cover.jpg"), "cover");
        Guid track, lost;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var artist = new MediaItem { LibraryId = id, Kind = MediaKind.Artist, Title = "Artist", SortTitle = "artist" };
            var album = new MediaItem
            {
                LibraryId = id, Kind = MediaKind.Album, Title = "Album", SortTitle = "album", ParentId = artist.Id, Path = $"{old}/Artist/Album",
                PosterUrl = $"file://{old}/Artist/Album/cover.jpg", DetailsLocked = true,
            };
            var one = new MediaItem { LibraryId = id, Kind = MediaKind.Track, Title = "One", SortTitle = "1", ParentId = album.Id, Path = $"{old}/Artist/Album/01.flac" };
            one.Files.Add(new MediaFile { Path = one.Path });
            var two = new MediaItem { LibraryId = id, Kind = MediaKind.Track, Title = "Two", SortTitle = "2", ParentId = album.Id, Path = $"{old}/Artist/Album/02.flac" };
            two.Files.Add(new MediaFile { Path = two.Path });
            db.AddRange(artist, album, one, two);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = one.Id, PlayCount = 4 });
            await db.SaveChangesAsync();
            (track, lost) = (one.Id, two.Id);
        }

        var response = await Client().PutAsJsonAsync(new Uri($"/api/v1/libraries/{id}", UriKind.Relative),
            new { name = created.Name, type = "Music", folders = new[] { moved } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var file = await db.MediaFiles.IgnoreQueryFilters().SingleAsync(f => f.MediaItemId == track);
            Assert.Equal(Path.Combine(moved, "Artist", "Album", "01.flac"), file.Path);
            Assert.Null(file.MissingSince);
            Assert.Equal(4, (await db.PlaybackStates.SingleAsync(p => p.MediaItemId == track)).PlayCount);
            var album = await db.MediaItems.IgnoreQueryFilters().SingleAsync(i => i.LibraryId == id && i.Kind == MediaKind.Album);
            Assert.Equal(Path.Combine(moved, "Artist", "Album"), album.Path);
            Assert.Equal($"file://{moved}/Artist/Album/cover.jpg", album.PosterUrl);
            Assert.True(album.DetailsLocked);
            // The one that didn't come across is missing, not gone.
            Assert.NotNull((await db.MediaItems.IgnoreQueryFilters().SingleAsync(i => i.Id == lost)).MissingSince);
        }
    }

    [Fact]
    public async Task DeletingALibrary_KeepsItsFilmsHistory_ForWhenTheyreAddedAgain()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "films-" + Guid.NewGuid())).FullName;
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = $"Films {Guid.NewGuid():N}", type = "Movies", folders = new[] { folder } })).Content.ReadFromJsonAsync<LibraryDto>(Json);
        var tmdb = Random.Shared.Next(1_000_000, 9_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var film = new MediaItem { LibraryId = created!.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", TmdbId = tmdb, Path = $"{folder}/Film" };
            film.Files.Add(new MediaFile { Path = $"{folder}/Film/Film.mkv" });
            db.MediaItems.Add(film);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = film.Id, Watched = true, PlayCount = 1 });
            await db.SaveChangesAsync();
        }

        var response = await Client().DeleteAsync(new Uri($"/api/v1/libraries/{created!.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var check = factory.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.True(await after.WatchTombstones.AnyAsync(t => t.TmdbId == tmdb && t.Kind == MediaKind.Movie));
    }

    [Fact]
    public async Task CreateLibrary_RejectsMissingAndOverlappingFolders()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "tv-" + Guid.NewGuid())).FullName;
        await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative), new { name = "TV", type = "TvShows", folders = new[] { folder } });

        var missing = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Nope", type = "Movies", folders = new[] { "/definitely/not/here" } });
        var overlapping = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Dupe", type = "TvShows", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, overlapping.StatusCode);
        Assert.Contains("overlaps", await overlapping.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateLibrary_RejectsTheRecordingsName()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "recordings-" + Guid.NewGuid())).FullName;

        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/libraries", UriKind.Relative),
            new { name = "Recordings", type = "TvShows", folders = new[] { folder } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnknownLibrary_Items_ReturnsEmptyPage()
    {
        var page = await Client().GetFromJsonAsync<PagedDto<ItemSummaryDto>>(new Uri($"/api/v1/libraries/{Guid.NewGuid()}/items", UriKind.Relative), Json);

        Assert.Equal(0, page!.Total);
    }

    [Fact]
    public async Task LibraryGenres_CountThisLibrarysTitles_EachWithItsOwnBackdropWhereThereIsOne()
    {
        Guid films, heat, alien;
        string crime, scifi, drama;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var other = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            db.AddRange(library, other);
            // Genre names are unique across the database, which the tests share.
            var tag = Guid.NewGuid().ToString("N")[..6];
            var (crimeGenre, scifiGenre, dramaGenre) = (new Genre { Name = $"Crime {tag}" }, new Genre { Name = $"Science Fiction {tag}" }, new Genre { Name = $"Drama {tag}" });
            MediaItem Film(Library at, string title, double rating, bool backdrop, params Genre[] genres)
            {
                var film = new MediaItem
                {
                    LibraryId = at.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant(), CommunityRating = rating,
                    BackdropUrl = backdrop ? "https://image.example/backdrop.jpg" : null,
                };
                film.Genres.AddRange(genres);
                db.Add(film);
                return film;
            }

            var heatFilm = Film(library, "Heat", 8.3, true, crimeGenre, dramaGenre);
            Film(library, "Collateral", 7.5, true, crimeGenre);
            var alienFilm = Film(library, "Alien", 8.5, true, scifiGenre);
            Film(library, "Moon", 7.8, false, scifiGenre);
            Film(other, "Thief", 7.4, true, crimeGenre, dramaGenre);
            await db.SaveChangesAsync();
            (films, heat, alien) = (library.Id, heatFilm.Id, alienFilm.Id);
            (crime, scifi, drama) = (crimeGenre.Name, scifiGenre.Name, dramaGenre.Name);
        }

        var genres = await Client().GetFromJsonAsync<List<LibraryGenreDto>>(new Uri($"/api/v1/libraries/{films}/genres", UriKind.Relative), Json);
        Assert.NotNull(genres);

        // The other library's Thief doesn't count; biggest first.
        Assert.Equal([(crime, 2), (scifi, 2), (drama, 1)], genres.Select(g => (g.Name, g.Count)).OrderByDescending(g => g.Count).ThenBy(g => g.Name));
        Assert.Contains(alien.ToString(), genres.Single(g => g.Name == scifi).Backdrop, StringComparison.Ordinal);
        // Heat is the best-rated crime film here, so crime has it, and drama (Heat's only title) has it as well rather than nothing.
        Assert.Contains(heat.ToString(), genres.Single(g => g.Name == crime).Backdrop, StringComparison.Ordinal);
        Assert.Contains(heat.ToString(), genres.Single(g => g.Name == drama).Backdrop, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenreItems_FindAGenreWithASlashInItsName()
    {
        // From a hand-written .nfo: "Action/Adventure" is one genre, which the apps send as one encoded path segment.
        var name = $"Action/Adventure {Guid.NewGuid().ToString("N")[..6]}";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            db.Add(library);
            var film = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Heat", SortTitle = "heat", CommunityRating = 8.3 };
            film.Genres.Add(new Genre { Name = name });
            db.Add(film);
            await db.SaveChangesAsync();
        }

        var page = await Client().GetFromJsonAsync<PagedDto<ItemSummaryDto>>(
            new Uri($"/api/v1/genres/{Uri.EscapeDataString(name)}/items", UriKind.Relative), Json);

        Assert.Equal(["Heat"], page!.Items.Select(i => i.Title));
    }

    [Fact]
    public async Task Filesystem_ListsDirectories()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, "browse-" + Guid.NewGuid(), "Child")).Parent!.FullName;

        var listing = await Client().GetFromJsonAsync<DirectoryListingDto>(new Uri($"/api/v1/filesystem?path={Uri.EscapeDataString(folder)}", UriKind.Relative), Json);

        Assert.Equal(["Child"], listing!.Directories.Select(d => d.Name));
        Assert.False(listing.ChooseOnMac);
    }

    [Fact]
    public async Task ChooseFolder_OutsideTheMacApp_IsNotFound()
    {
        var response = await Client().PostAsync(new Uri("/api/v1/filesystem/choose", UriKind.Relative), null, CancellationToken.None);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MacApp_OutsideTheMacApp_IsNotFound()
    {
        var response = await Client().GetAsync(new Uri("/api/v1/mac-app", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
