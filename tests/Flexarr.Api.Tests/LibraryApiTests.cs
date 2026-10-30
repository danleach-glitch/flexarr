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
