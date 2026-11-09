using System.Net;
using System.Net.Http.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
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

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithLidarr(FakeArr arr, FlexarrApiFactory factory) =>
        arr.Serve(factory).WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Arr:2:Name", "Lidarr");
            b.UseSetting("Flexarr:Arr:2:Kind", "Lidarr");
            b.UseSetting("Flexarr:Arr:2:BaseUrl", "http://lidarr.test");
            b.UseSetting("Flexarr:Arr:2:ApiKey", "key");
        });

    private static async Task<(MediaItem Artist, MediaItem Album)> AddMusicAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, bool managed)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Music {Guid.NewGuid():N}", Type = LibraryType.Music, Folders = [new LibraryFolder { Path = $"/Volumes/Media/data/music-{Guid.NewGuid():N}" }] };
        var artist = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Artist, Title = "Wet Leg", SortTitle = "wet leg", MetadataSource = managed ? "Lidarr" : null, ArrId = managed ? 3 : null };
        var album = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Album, Title = "moisturizer", SortTitle = "moisturizer", Parent = artist, MetadataSource = managed ? "Lidarr" : null, ArrId = managed ? 9 : null };
        db.AddRange(library, artist, album);
        await db.SaveChangesAsync();
        return (artist, album);
    }

    private static async Task<T> InScopeAsync<T>(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, Func<FlexarrDbContext, Task<T>> query)
    {
        using var scope = app.Services.CreateScope();
        return await query(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>());
    }

    private static HttpClient Admin(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    [Theory]
    [InlineData("new", true)]
    [InlineData("none", false)]
    public async Task AnAlbumLidarrManages_LosesItsFiles_AndStaysUnmonitored_AndAFollowedArtistStays(string monitorNewItems, bool artistStays)
    {
        var arr = new FakeArr();
        arr.Responses["PUT /api/v1/album/monitor"] = "[]";
        arr.Responses["GET /api/v1/trackfile?albumId=9"] = """[{"id":71,"artistId":3,"albumId":9},{"id":72,"artistId":3,"albumId":9}]""";
        arr.Responses["DELETE /api/v1/trackfile/bulk"] = "{}";
        arr.Responses["GET /api/v1/artist"] = $$"""[{"id":3,"artistName":"Wet Leg","monitored":true,"monitorNewItems":"{{monitorNewItems}}"}]""";
        using var app = WithLidarr(arr, factory);
        var (artist, album) = await AddMusicAsync(app, managed: true);

        var plan = await Admin(app).GetFromJsonAsync<DeletionPlan>(new Uri($"/api/v1/items/{album.Id}/deletion", UriKind.Relative));
        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{album.Id}", UriKind.Relative));

        Assert.Equal("Lidarr", plan!.ManagedBy);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("PUT /api/v1/album/monitor", arr.Requests);
        Assert.Contains("DELETE /api/v1/trackfile/bulk", arr.Requests);
        Assert.DoesNotContain(arr.Requests, r => r.StartsWith("DELETE /api/v1/album", StringComparison.Ordinal));
        using var check = app.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.False(await db.MediaItems.AnyAsync(i => i.Id == album.Id));
        var kept = await db.MediaItems.FirstOrDefaultAsync(i => i.Id == artist.Id);
        Assert.Equal(artistStays, kept is not null);
        Assert.Equal(artistStays, kept?.Placeholder ?? false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnArtistLidarrManages_IsDeletedThere_WithTheirFiles_OnlyWhenTheirFolderIsThisLibrarys(bool inLibrary)
    {
        var arr = new FakeArr();
        arr.Responses["DELETE /api/v1/artist/3?deleteFiles=true&addImportListExclusion=false"] = "{}";
        using var app = WithLidarr(arr, factory);
        var (artist, album) = await AddMusicAsync(app, managed: true);
        var folder = await InScopeAsync(app, db => db.LibraryFolders.Where(f => f.LibraryId == artist.LibraryId).Select(f => f.Path).SingleAsync());
        var lidarrPath = inLibrary ? folder.Replace("/Volumes/Media/data", "/data", StringComparison.Ordinal) + "/Wet Leg" : "/data/elsewhere/Wet Leg";
        arr.Responses["GET /api/v1/artist/3"] = $$"""{"id":3,"artistName":"Wet Leg","path":"{{lidarrPath}}"}""";
        if (!inLibrary)
        {
            var refused = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{artist.Id}", UriKind.Relative));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.DoesNotContain(arr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
            return;
        }

        await InScopeAsync(app, async db =>
        {
            db.MediaRequests.Add(new Core.Notifications.MediaRequest { ProfileId = "owner", Kind = MediaKind.Artist, Title = "Wet Leg" });
            db.MediaRequests.Add(new Core.Notifications.MediaRequest { ProfileId = "owner", Kind = MediaKind.Album, Title = "moisturizer", ArtistName = "Wet Leg", MusicBrainzId = "rg" });
            return await db.SaveChangesAsync();
        });
        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{artist.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v1/artist/3?deleteFiles=true&addImportListExclusion=false", arr.Requests);
        using var check = app.Services.CreateScope();
        var after = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.False(await after.MediaItems.AnyAsync(i => i.Id == artist.Id || i.Id == album.Id));
        // Nothing more of theirs is coming: their follow and album request are over.
        Assert.False(await after.MediaRequests.AnyAsync(r => (r.Title == "Wet Leg" || r.ArtistName == "Wet Leg") && r.FulfilledAt == null));
    }

    [Fact]
    public async Task AnArtistMatchedToAnotherOfTheirName_IsntDeletedThere()
    {
        var arr = new FakeArr();
        arr.Responses["DELETE /api/v1/artist/3?deleteFiles=true&addImportListExclusion=false"] = "{}";
        using var app = WithLidarr(arr, factory);
        var (artist, album) = await AddMusicAsync(app, managed: true);
        var folder = await InScopeAsync(app, db => db.LibraryFolders.Where(f => f.LibraryId == artist.LibraryId).Select(f => f.Path).SingleAsync());
        await InScopeAsync(app, async db =>
        {
            (await db.MediaItems.SingleAsync(i => i.Id == album.Id)).Path = folder + "/Nirvana (UK)/Local Anaesthetic";
            return await db.SaveChangesAsync();
        });
        // Lidarr's Nirvana is the other one, in a folder of their own in the same library.
        arr.Responses["GET /api/v1/artist/3"] = $$"""{"id":3,"artistName":"Nirvana","path":"{{folder.Replace("/Volumes/Media/data", "/data", StringComparison.Ordinal)}}/Nirvana"}""";

        var refused = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{artist.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.DoesNotContain(arr.Requests, r => r.StartsWith("DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnAlbumLidarrNeverMatched_UnderAnArtistItManages_IsFoundThereByTitle()
    {
        var arr = new FakeArr();
        arr.Responses["GET /api/v1/album?artistId=3"] = """[{"id":9,"artistId":3,"title":"Moisturizer","monitored":true}]""";
        arr.Responses["PUT /api/v1/album/monitor"] = "[]";
        arr.Responses["GET /api/v1/trackfile?albumId=9"] = "[]";
        arr.Responses["GET /api/v1/artist"] = """[{"id":3,"artistName":"Wet Leg","monitored":true,"monitorNewItems":"none"}]""";
        using var app = WithLidarr(arr, factory);
        var (_, album) = await AddMusicAsync(app, managed: true);
        await InScopeAsync(app, db => db.MediaItems.Where(i => i.Id == album.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.ArrId, (int?)null).SetProperty(i => i.MetadataSource, (string?)null)));

        var plan = await Admin(app).GetFromJsonAsync<DeletionPlan>(new Uri($"/api/v1/items/{album.Id}/deletion", UriKind.Relative));
        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{album.Id}", UriKind.Relative));

        Assert.Equal("Lidarr", plan!.ManagedBy);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("PUT /api/v1/album/monitor", arr.Requests);
    }

    [Fact]
    public async Task AnAlbumNoLidarrManages_LeavesTheDisk_AndTakesItsEmptiedArtist()
    {
        var arr = new FakeArr();
        using var app = WithLidarr(arr, factory);
        var (artist, album) = await AddMusicAsync(app, managed: false);

        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{album.Id}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Empty(arr.Requests);
        using var check = app.Services.CreateScope();
        Assert.False(await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.AnyAsync(i => i.Id == artist.Id));
    }

    private sealed record DeletionPlan(string? ManagedBy, string[] Files, DeletionCopy[]? Copies = null);

    private sealed record DeletionCopy(Guid ItemId, string Library, string? ManagedBy, string[] Files);

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
                Files = [new MediaFile { Path = best, Height = 1080, Size = 4, Bitrate = 2000 }, new MediaFile { Path = spare, Height = 1080, Size = 5, Bitrate = 1000 }],
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

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> WithSecondRadarr(FakeArr arr, FlexarrApiFactory factory) =>
        arr.Serve(factory).WithWebHostBuilder(b =>
        {
            b.UseSetting("Flexarr:Arr:2:Name", "Radarr 4K");
            b.UseSetting("Flexarr:Arr:2:Kind", "Radarr");
            b.UseSetting("Flexarr:Arr:2:BaseUrl", "http://radarr4k.test");
            b.UseSetting("Flexarr:Arr:2:ApiKey", "key");
        });

    /// <summary>A film in an HD and a 4K library. The 4K copy's record names the HD Radarr, as metadata's TMDB fallback can.</summary>
    private static async Task<(Guid Hd, Guid Uhd, Guid UhdLibrary, string HdFile, string UhdFile, string Root)> AddLinkedFilmAsync(
        Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, FakeArr arr)
    {
        var root = Directory.CreateTempSubdirectory("flexarr-copies-").FullName;
        var hdDir = Directory.CreateDirectory(Path.Combine(root, "hd", "Film")).FullName;
        var uhdDir = Directory.CreateDirectory(Path.Combine(root, "4k", "Film")).FullName;
        var hdFile = Path.Combine(hdDir, "Film 1080p.mkv");
        var uhdFile = Path.Combine(uhdDir, "Film 2160p.mkv");
        File.WriteAllText(hdFile, "hd");
        File.WriteAllText(uhdFile, "uhd");
        var tmdb = Random.Shared.Next(1_000_000, 9_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var hd = new Library { Name = $"HD {tmdb}", Type = LibraryType.Movies };
        var uhd = new Library { Name = $"4K {tmdb}", Type = LibraryType.Movies };
        var hdFilm = new MediaItem { LibraryId = hd.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", TmdbId = tmdb, MetadataSource = "Radarr", ArrId = 7, Files = [new MediaFile { Path = hdFile, Height = 1080 }] };
        var uhdFilm = new MediaItem { LibraryId = uhd.Id, Kind = MediaKind.Movie, Title = "Film", SortTitle = "film", TmdbId = tmdb, MetadataSource = "Radarr", ArrId = 7, Files = [new MediaFile { Path = uhdFile, Height = 2160 }] };
        db.AddRange(hd, uhd, hdFilm, uhdFilm);
        db.AddRange(new LibraryFolder { LibraryId = hd.Id, Path = Path.Combine(root, "hd") }, new LibraryFolder { LibraryId = uhd.Id, Path = Path.Combine(root, "4k") });
        await db.SaveChangesAsync();
        await TitleLinker.LinkAsync(db, default);
        arr.Responses["GET //radarr.test/api/v3/movie"] = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = 7, title = "Film", path = hdDir } });
        arr.Responses["GET //radarr4k.test/api/v3/movie"] = System.Text.Json.JsonSerializer.Serialize(new[] { new { id = 9, title = "Film", path = uhdDir } });
        arr.Responses["DELETE //radarr.test/api/v3/movie/7?deleteFiles=true&addImportExclusion=false"] = "{}";
        arr.Responses["DELETE //radarr4k.test/api/v3/movie/9?deleteFiles=true&addImportExclusion=false"] = "{}";
        return (hdFilm.Id, uhdFilm.Id, uhd.Id, hdFile, uhdFile, root);
    }

    [Fact]
    public async Task DeletingALinkedTitle_DeletesEveryCopy_ThroughTheAppHoldingIt_EvenInAHiddenLibrary()
    {
        var arr = new FakeArr();
        using var app = WithSecondRadarr(arr, factory);
        var (hd, uhd, uhdLibrary, _, _, root) = await AddLinkedFilmAsync(app, arr);
        await SetHiddenAsync(app, [uhdLibrary]);
        try
        {
            var plan = await Admin(app).GetFromJsonAsync<DeletionPlan>(new Uri($"/api/v1/items/{hd}/deletion", UriKind.Relative));
            Assert.Equal(2, plan!.Copies!.Length);
            Assert.Equal(["Radarr", "Radarr 4K"], plan.Copies.Select(c => c.ManagedBy!).Order());

            var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{hd}?allCopies=true", UriKind.Relative));

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            Assert.Contains("DELETE /api/v3/movie/7?deleteFiles=true&addImportExclusion=false", arr.Requests);
            Assert.Contains("DELETE /api/v3/movie/9?deleteFiles=true&addImportExclusion=false", arr.Requests);
            using var check = app.Services.CreateScope();
            var db = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.False(await db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.Id == hd || i.Id == uhd));
        }
        finally
        {
            await SetHiddenAsync(app, []);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task APlainDelete_TakesOnlyTheCopyNamed()
    {
        var arr = new FakeArr();
        using var app = WithSecondRadarr(arr, factory);
        var (hd, uhd, _, _, _, root) = await AddLinkedFilmAsync(app, arr);

        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{hd}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("DELETE /api/v3/movie/7?deleteFiles=true&addImportExclusion=false", arr.Requests);
        Assert.DoesNotContain("DELETE /api/v3/movie/9?deleteFiles=true&addImportExclusion=false", arr.Requests);
        using var check = app.Services.CreateScope();
        var left = await check.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaItems.SingleAsync(i => i.Id == uhd);
        Assert.Null(left.LinkGroupId);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ACopyThatWontDelete_IsReported_AndTheOneAskedAboutStays_SoTryingAgainWorks()
    {
        var arr = new FakeArr();
        using var app = WithSecondRadarr(arr, factory);
        var (hd, uhd, _, _, _, root) = await AddLinkedFilmAsync(app, arr);
        arr.Before = (request, _) => request.Method == HttpMethod.Delete && request.RequestUri!.Host == "radarr4k.test"
            ? throw new HttpRequestException("no", null, HttpStatusCode.InternalServerError)
            : Task.CompletedTask;

        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{hd}?allCopies=true", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("Deleted 0 of 2 copies", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using (var check = app.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.True(await db.MediaItems.AnyAsync(i => i.Id == hd));
            Assert.True(await db.MediaItems.AnyAsync(i => i.Id == uhd));
        }

        // Trying again, the same way, finishes the job.
        arr.Before = null;
        var again = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{hd}?allCopies=true", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        using (var check = app.Services.CreateScope())
        {
            var db = check.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            Assert.False(await db.MediaItems.AnyAsync(i => i.Id == hd || i.Id == uhd));
        }

        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task ACopyLeftOverAfterItsOtherWent_StillGoesToTheAppHoldingIt()
    {
        var arr = new FakeArr();
        using var app = WithSecondRadarr(arr, factory);
        var (hd, uhd, _, _, _, root) = await AddLinkedFilmAsync(app, arr);
        Assert.Equal(HttpStatusCode.NoContent, (await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{hd}", UriKind.Relative))).StatusCode);

        // Though its record names the other Radarr.
        var again = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{uhd}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Contains("DELETE /api/v3/movie/9?deleteFiles=true&addImportExclusion=false", arr.Requests);
        Directory.Delete(root, recursive: true);
    }

    [Fact]
    public async Task AManagedEpisode_LosesItsUntrackedFilesToo()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-episode-").FullName;
        var season = Directory.CreateDirectory(Path.Combine(root, "Show", "Season 1")).FullName;
        var tracked = Path.Combine(season, "Show S01E01 2160p.mkv");
        var extra = Path.Combine(season, "Show S01E01 720p.mkv");
        File.WriteAllText(extra, "extra");
        var arr = new FakeArr();
        arr.Responses["GET /api/v3/episode/5"] = """{"id":5,"seriesId":2,"seasonNumber":1,"episodeNumber":1,"episodeFileId":8}""";
        arr.Responses["GET /api/v3/episodefile/8"] = System.Text.Json.JsonSerializer.Serialize(new { id = 8, path = tracked });
        arr.Responses["PUT /api/v3/episode/monitor"] = "[]";
        arr.Responses["DELETE /api/v3/episodefile/8"] = "{}";
        using var app = arr.Serve(factory);
        Guid episodeId;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"TV {Guid.NewGuid():N}", Type = LibraryType.TvShows };
            var show = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Series, Title = "Show", SortTitle = "show", MetadataSource = "Sonarr", ArrId = 2 };
            var one = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, Parent = show };
            var episode = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Episode, Title = "Pilot", SortTitle = "1", SeasonNumber = 1, EpisodeNumber = 1, Parent = one, ArrId = 5, MetadataSource = "Sonarr",
                Files = [new MediaFile { Path = tracked, Height = 2160 }, new MediaFile { Path = extra, Height = 720 }],
            };
            db.AddRange(library, show, one, episode, new LibraryFolder { LibraryId = library.Id, Path = root });
            await db.SaveChangesAsync();
            episodeId = episode.Id;
        }

        var plan = await Admin(app).GetFromJsonAsync<DeletionPlan>(new Uri($"/api/v1/items/{episodeId}/deletion", UriKind.Relative));
        Assert.Equal([extra], plan!.Files);

        var response = await Admin(app).DeleteAsync(new Uri($"/api/v1/items/{episodeId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.False(File.Exists(extra));
        Directory.Delete(root, recursive: true);
    }

    private static async Task SetHiddenAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, List<Guid> hidden)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var main = await db.Profiles.SingleAsync(p => p.Id == PlaybackState.DefaultUser);
        main.HiddenLibraryIds = hidden;
        await db.SaveChangesAsync();
        Flexarr.Api.Profiles.LibraryVisibility.KidsSettingsChanged();
    }
}
