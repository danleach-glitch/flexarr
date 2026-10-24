using System.Net;
using System.Net.Http.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class DeleteTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task AFilmSonarrAndRadarrDontManage_IsDeletedFromDisk_WithTheFolderItLeavesEmpty()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-delete-").FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Home Video (2020)")).FullName;
        var video = Path.Combine(folder, "Home Video (2020).mkv");
        File.WriteAllText(video, "video");
        File.WriteAllText(Path.Combine(folder, ".DS_Store"), "finder");
        var film = await AddFilmAsync(root, video);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var plan = await client.GetFromJsonAsync<DeletionPlan>(new Uri($"/api/v1/items/{film}/deletion", UriKind.Relative));
        var response = await client.DeleteAsync(new Uri($"/api/v1/items/{film}", UriKind.Relative));

        Assert.Null(plan!.ManagedBy);
        Assert.Equal([video], plan.Files);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(root)); // the library's own folder stays, empty or not
        using var check = factory.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AnyAsync(i => i.Id == film));
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AFileOutsideTheLibrarysFolders_IsNeverDeleted()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-delete-").FullName;
        var elsewhere = Directory.CreateTempSubdirectory("flexarr-elsewhere-").FullName;
        var video = Path.Combine(elsewhere, "Home Video.mkv");
        File.WriteAllText(video, "video");
        var film = await AddFilmAsync(root, video);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.DeleteAsync(new Uri($"/api/v1/items/{film}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(video));
        using var check = factory.Services.CreateScope();
        Assert.True(await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AnyAsync(i => i.Id == film));
        Directory.Delete(root, recursive: true);
        Directory.Delete(elsewhere, recursive: true);
    }

    private sealed record DeletionPlan(string? ManagedBy, string[] Files);

    /// <summary>A film no Radarr manages, in a films library whose folder is <paramref name="root"/>.</summary>
    private async Task<Guid> AddFilmAsync(string root, string video)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies, Folders = [new LibraryFolder { Path = root }] };
        var movie = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Home Video", SortTitle = "home video",
            Files = [new MediaFile { Path = video, Size = 5 }],
        };
        db.AddRange(library, movie);
        await db.SaveChangesAsync();
        return movie.Id;
    }

    [Fact]
    public async Task DeletingTheOnlyVisibleEpisode_KeepsTheSeason_WhileASiblingIsBrieflyMissing()
    {
        var arr = new FakeArr();
        arr.Responses["GET /api/v3/episode/11"] = """{"id":11,"seriesId":3,"seasonNumber":1,"episodeNumber":1,"episodeFileId":5}""";
        arr.Responses["PUT /api/v3/episode/monitor"] = "{}";
        arr.Responses["DELETE /api/v3/episodefile/5"] = "{}";
        using var app = arr.Serve(factory);
        Guid seasonId, sibling;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
            var series = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show", MetadataSource = "Sonarr", ArrId = 3 };
            var season = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "0001", Parent = series, SeasonNumber = 1 };
            var one = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "One", SortTitle = "0001", Parent = season, ArrId = 11 };
            var two = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Two", SortTitle = "0002", Parent = season, MissingSince = DateTime.UtcNow };
            db.AddRange(library, series, season, one, two);
            db.PlaybackStates.Add(new PlaybackState { MediaItemId = two.Id, Watched = true, PlayCount = 1 });
            await db.SaveChangesAsync();
            (seasonId, sibling) = (season.Id, two.Id);
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            var response = await client.DeleteAsync(new Uri($"/api/v1/items/{one.Id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using var check = app.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.True(await after.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.Id == seasonId));
        Assert.True(await after.PlaybackStates.AnyAsync(p => p.MediaItemId == sibling && p.Watched));
    }

    [Fact]
    public async Task AHeldFolder_ShowsInLibraryHealth_AndItsRemovalCanBeConfirmed()
    {
        var path = Path.Combine(factory.MediaDirectory, $"4k-{Guid.NewGuid():N}");
        int folderId, unmounted;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library
            {
                Name = $"4K {Guid.NewGuid():N}", Type = LibraryType.Movies,
                Folders =
                [
                    new LibraryFolder { Path = path, Hold = FolderHold.FolderEmpty, HeldSince = DateTime.UtcNow.AddDays(-2), HeldScans = 4, HeldMissing = 9, HeldKnown = 9 },
                    new LibraryFolder { Path = path + "-usb", Hold = FolderHold.FolderMissing, HeldSince = DateTime.UtcNow, HeldScans = 1 },
                ],
            };
            db.Add(library);
            await db.SaveChangesAsync();
            (folderId, unmounted) = (library.Folders[0].Id, library.Folders[1].Id);
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var health = await client.GetFromJsonAsync<System.Text.Json.JsonElement>(new Uri("/api/v1/library-health", UriKind.Relative));
        var held = health.GetProperty("held").EnumerateArray().Single(h => h.GetProperty("folderId").GetInt32() == folderId);
        var shown = new { since = held.GetProperty("since").GetString(), missing = held.GetProperty("missing").GetInt32(), known = held.GetProperty("known").GetInt32() };
        var stale = await client.PostAsJsonAsync(new Uri($"/api/v1/library-health/held/{folderId}/confirm", UriKind.Relative), shown with { missing = 6 });
        var confirm = await client.PostAsJsonAsync(new Uri($"/api/v1/library-health/held/{folderId}/confirm", UriKind.Relative), shown);
        var refused = await client.PostAsJsonAsync(new Uri($"/api/v1/library-health/held/{unmounted}/confirm", UriKind.Relative), shown);

        Assert.Equal(("FolderEmpty", 9, true, "Movies"),
            (held.GetProperty("reason").GetString(), held.GetProperty("missing").GetInt32(), held.GetProperty("canConfirm").GetBoolean(), held.GetProperty("libraryType").GetString()));
        Assert.Equal((HttpStatusCode.Conflict, HttpStatusCode.Accepted, HttpStatusCode.Conflict), (stale.StatusCode, confirm.StatusCode, refused.StatusCode));
        Assert.Contains("found something different", await stale.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == path);
        using var check = factory.Services.CreateScope();
        Assert.True((await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().LibraryFolders.SingleAsync(f => f.Id == folderId)).RemovalConfirmed);
    }

    [Fact]
    public async Task SpareCopiesOfAnUnmanagedTitle_StayOnDisk()
    {
        var dir = Directory.CreateTempSubdirectory("flexarr-dupes-").FullName;
        var best = Path.Combine(dir, "Film (2020) [1080p].mkv");
        var spare = Path.Combine(dir, "Film (2020).avi");
        File.WriteAllText(best, "best");
        File.WriteAllText(spare, "spare");
        Guid film;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
            var movie = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film",
                Files = [new MediaFile { Path = best, Height = 1080, Size = 4 }, new MediaFile { Path = spare, Height = 480, Size = 5 }],
            };
            db.AddRange(library, movie);
            await db.SaveChangesAsync();
            film = movie.Id;
        }

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.PostAsync(new Uri($"/api/v1/items/{film}/remove-duplicates", UriKind.Relative), null);

        // Without Sonarr or Radarr to say which copy they use, nothing is deleted.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(spare));
        Directory.Delete(dir, recursive: true);
    }
}
