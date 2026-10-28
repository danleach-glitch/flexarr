using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class LibraryScannerTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-scan-").FullName;
    private readonly FakeProbe _probe = new();
    private FlexarrDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Movies_FolderPerMovie_WithMultipleVersionsAndExtras()
    {
        var root = Touch(
            "movies/The Nice Guys (2016)/The Nice Guys (2016) [Bluray-1080p].mkv",
            "movies/Forrest Gump (1994)/Forrest Gump (1994) [Bluray-1080p].mkv",
            "movies/Forrest Gump (1994)/Forrest Gump (1994) [Remux-2160p].mkv",
            "movies/Forrest Gump (1994)/Featurettes/Making Of.mkv",
            "movies/All of You (2025) {imdb-tt30852384}/All of You (2025) - [WEBDL-1080p].mkv",
            "movies/The Nice Guys (2016)/notes.nfo");
        var library = await AddLibrary(LibraryType.Movies, Path.Combine(root, "movies"));

        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(4, result.Added);
        var movies = await _db.MediaItems.Include(i => i.Files).OrderBy(i => i.SortTitle).ToListAsync();
        Assert.Equal(["All of You", "Forrest Gump", "The Nice Guys"], movies.Select(m => m.Title));
        Assert.Equal(2, movies[1].Files.Count);
        Assert.Equal("tt30852384", movies[0].ImdbId);
        Assert.Equal("nice guys", movies[2].SortTitle);
        Assert.Equal("hevc", movies[2].Files[0].VideoCodec);
    }

    [Fact]
    public async Task TvShows_BuildSeriesSeasonEpisodeTree()
    {
        var root = Touch(
            "tv/30 Rock/Season 01/30 Rock (2006) - S01E01 - Pilot [HDTV-720p].mkv",
            "tv/30 Rock/Season 01/30 Rock (2006) - S01E02 - The Aftermath [HDTV-720p].mkv",
            "tv/30 Rock/Season 06/30 Rock (2006) - S06E06-E07 - Hey Baby Whats Wrong [HDTV-720p].mkv",
            "tv/Gogglebox (2019) {tvdb-365180}/Gogglebox (2019) - S07E01 - Episode 1 [WEBDL-1080p].mkv",
            "tv/Only Fools and Horses/Specials/Only Fools And Horses 03 Thicker Than Water 1983 DVDRip.mp4",
            "tv/Eastbound/Season 01/Season 1 EXTRAS/eastbound.s01.extras.outtakes.avi",
            "tv/Mystery/random video.mkv");
        var library = await AddLibrary(LibraryType.TvShows, Path.Combine(root, "tv"));

        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(5, result.Added);
        Assert.Equal(1, result.Unmatched); // "random video.mkv" has no episode number
        var series = await _db.MediaItems.Where(i => i.Kind == MediaKind.Series).OrderBy(i => i.SortTitle).Select(i => i.Title).ToListAsync();
        Assert.Equal(["30 Rock", "Gogglebox", "Only Fools and Horses"], series);

        var multi = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Episode && i.SeasonNumber == 6);
        Assert.Equal((6, 7, "Hey Baby Whats Wrong"), (multi.EpisodeNumber!.Value, multi.EpisodeNumberEnd!.Value, multi.Title));

        var special = await _db.MediaItems.Include(i => i.Parent).SingleAsync(i => i.Title == "Thicker Than Water");
        Assert.Equal("Specials", special.Parent!.Title);
        Assert.Equal("365180", (await _db.MediaItems.SingleAsync(i => i.Title == "Gogglebox")).TvdbId);
    }

    [Fact]
    public async Task Rescan_SkipsUnchanged_ProbesChanged_HidesMissing_ThenPrunesAfterGracePeriod()
    {
        var root = Touch(
            "tv/Show/Season 01/Show - S01E01.mkv",
            "tv/Show/Season 01/Show - S01E02.mkv",
            "tv/Other/Season 01/Other - S01E01.mkv");
        var library = await AddLibrary(LibraryType.TvShows, Path.Combine(root, "tv"));
        await Scanner().ScanAsync(library, null, default);
        _probe.Calls = 0;

        File.AppendAllText(Path.Combine(root, "tv/Show/Season 01/Show - S01E01.mkv"), "more bytes");
        Directory.Delete(Path.Combine(root, "tv/Other"), recursive: true);
        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal((0, 1, 0, 1, 1), (result.Added, result.Updated, result.Removed, result.Unchanged, result.Missing));
        Assert.Equal(1, _probe.Calls);
        Assert.False(await _db.MediaItems.AnyAsync(i => i.Title == "Other"), "a series whose files are gone should be hidden");
        Assert.Equal(2, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Episode));
        Assert.Equal(3, await _db.MediaItems.IgnoreQueryFilters().CountAsync(i => i.Kind == MediaKind.Episode));

        // Still gone after the grace period: removed for good.
        await _db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow - LibraryScanner.MissingGracePeriod - TimeSpan.FromMinutes(1)));
        var later = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(1, later.Removed);
        Assert.False(await _db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.Title == "Other"), "empty series should be pruned");
        Assert.Equal(2, await _db.MediaItems.IgnoreQueryFilters().CountAsync(i => i.Kind == MediaKind.Episode));
    }

    [Fact]
    public async Task MissingFile_ThatComesBack_KeepsItsItemAndWatchState()
    {
        var root = Touch("movies/Dune (2021)/Dune (2021).mkv", "movies/Heat (1995)/Heat (1995).mkv");
        var library = await AddLibrary(LibraryType.Movies, Path.Combine(root, "movies"));
        await Scanner().ScanAsync(library, null, default);
        var dune = await _db.MediaItems.SingleAsync(i => i.Title == "Dune");
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = dune.Id, Watched = true, PlayCount = 1 });
        await _db.SaveChangesAsync();

        // Mid-upgrade: the old file is deleted before the new one has finished copying in.
        var file = Path.Combine(root, "movies/Dune (2021)/Dune (2021).mkv");
        File.Move(file, file + ".partial");
        var during = await Scanner().ScanAsync(library, Path.Combine(root, "movies/Dune (2021)"), default);
        Assert.Equal((0, 1), (during.Removed, during.Missing));
        Assert.False(await _db.MediaItems.AnyAsync(i => i.Id == dune.Id), "a film with no files should be hidden");
        Assert.True(await _db.MediaItems.AnyAsync(i => i.Title == "Heat"));

        File.Move(file + ".partial", Path.Combine(root, "movies/Dune (2021)/Dune (2021) [Remux-2160p].mkv"));
        await Scanner().ScanAsync(library, Path.Combine(root, "movies/Dune (2021)"), default);

        _db.ChangeTracker.Clear(); // The scanner shared this context; read back what was saved.
        var back = await _db.MediaItems.Include(i => i.Files).SingleAsync(i => i.Title == "Dune");
        Assert.Equal(dune.Id, back.Id);
        Assert.EndsWith("[Remux-2160p].mkv", Assert.Single(back.Files).Path, StringComparison.Ordinal);
        Assert.True((await _db.PlaybackStates.SingleAsync(p => p.MediaItemId == dune.Id)).Watched);
    }

    [Fact]
    public async Task EmptyRoot_HidesAndRemovesNothing()
    {
        var root = Touch("tv/A/Season 01/A - S01E01.mkv", "tv/B/Season 01/B - S01E01.mkv");
        var tv = Path.Combine(root, "tv");
        var library = await AddLibrary(LibraryType.TvShows, tv);
        await Scanner().ScanAsync(library, null, default);
        await _db.MediaFiles.Where(f => f.Path.Contains("/B/")).ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddDays(-30)));

        // An unmounted share leaves its (empty) mount point behind.
        Directory.Delete(Path.Combine(tv, "A"), recursive: true);
        Directory.Delete(Path.Combine(tv, "B"), recursive: true);
        var full = await Scanner().ScanAsync(library, null, default);
        var scoped = await Scanner().ScanAsync(library, Path.Combine(tv, "A"), default);

        Assert.Equal((0, 0, 0, 0), (full.Removed, full.Missing, scoped.Removed, scoped.Missing));
        Assert.Equal(1, await _db.MediaFiles.CountAsync());
        Assert.Equal(2, await _db.MediaFiles.IgnoreQueryFilters().CountAsync()); // Not even long-missing files go.
    }

    [Fact]
    public async Task MostOfALibraryVanishing_HidesNothing()
    {
        var root = Touch(Enumerable.Range(1, 12).Select(n => $"movies/Film {n} (2000)/Film {n} (2000).mkv").ToArray());
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);

        for (var n = 1; n <= 7; n++)
        {
            Directory.Delete(Path.Combine(movies, $"Film {n} (2000)"), recursive: true);
        }

        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(0, result.Missing);
        Assert.Equal(12, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Movie));
    }

    [Fact]
    public async Task MostOfALibraryVanishing_GoesThrough_OnceScansHaveFoundTheSameForADay()
    {
        var root = Touch(Enumerable.Range(1, 12).Select(n => $"movies/Film {n} (2000)/Film {n} (2000).mkv").ToArray());
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        for (var n = 1; n <= 7; n++)
        {
            Directory.Delete(Path.Combine(movies, $"Film {n} (2000)"), recursive: true);
        }

        var first = await Scanner().ScanAsync(library, null, default);
        var second = await Scanner().ScanAsync(library, null, default);
        var folder = await _db.LibraryFolders.SingleAsync();
        Assert.Equal((0, 0, FolderHold.MostFilesGone, 7, 12, 2), (first.Missing, second.Missing, folder.Hold, folder.HeldMissing, folder.HeldKnown, folder.HeldScans));

        // A third scan with the same result, a day after the first: a clear-out, not a hiccup.
        folder.HeldSince = DateTime.UtcNow - LibraryScanner.SettledAfter - TimeSpan.FromMinutes(1);
        await _db.SaveChangesAsync();
        var third = await Scanner().ScanAsync(library, null, default);
        var fourth = await Scanner().ScanAsync(library, null, default);

        Assert.Equal((7, 0), (third.Missing, fourth.Missing));
        Assert.Equal(5, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Movie));
        Assert.Null((await _db.LibraryFolders.AsNoTracking().SingleAsync()).Hold);
    }

    [Fact]
    public async Task ARemovalConfirmedWhileAScanFindsNewNumbers_DoesNotLetThemThrough()
    {
        var root = Touch(Enumerable.Range(1, 12).Select(n => $"movies/Film {n} (2000)/Film {n} (2000).mkv").ToArray());
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        for (var n = 1; n <= 7; n++)
        {
            Directory.Delete(Path.Combine(movies, $"Film {n} (2000)"), recursive: true);
        }

        await Scanner().ScanAsync(library, null, default); // Held: 7 of 12 gone.

        // Someone confirms the 7 just as a scan, which has the folder loaded already, finds an 8th gone.
        await using (var other = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options))
        {
            await other.LibraryFolders.ExecuteUpdateAsync(u => u.SetProperty(f => f.RemovalConfirmed, true));
        }

        Directory.Delete(Path.Combine(movies, "Film 8 (2000)"), recursive: true);
        var during = await Scanner().ScanAsync(library, null, default);
        var queued = await Scanner().ScanAsync(library, null, default); // The scan the confirmation queued.

        var folder = await _db.LibraryFolders.AsNoTracking().SingleAsync();
        Assert.Equal((0, 0), (during.Missing, queued.Missing));
        Assert.Equal((FolderHold.MostFilesGone, 8, false), (folder.Hold, folder.HeldMissing, folder.RemovalConfirmed));
    }

    [Fact]
    public async Task EmptiedFolder_StaysHeld_UntilTheRemovalIsConfirmed_ThenGoesAfterTheGracePeriod()
    {
        var root = Touch("movies/A (2001)/A (2001).mkv", "movies/B (2002)/B (2002).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        Directory.Delete(Path.Combine(movies, "A (2001)"), recursive: true);
        Directory.Delete(Path.Combine(movies, "B (2002)"), recursive: true);

        // However long it's been: an empty folder may be a mount point whose drive is away.
        await Scanner().ScanAsync(library, null, default);
        var folder = await _db.LibraryFolders.SingleAsync();
        folder.HeldSince = DateTime.UtcNow.AddDays(-5);
        folder.HeldScans = 20;
        await _db.SaveChangesAsync();
        var held = await Scanner().ScanAsync(library, null, default);
        Assert.Equal((0, FolderHold.FolderEmpty, 2), (held.Missing, folder.Hold, folder.HeldMissing));

        folder.RemovalConfirmed = true; // Library health's "Confirm removal".
        await _db.SaveChangesAsync();
        var confirmed = await Scanner().ScanAsync(library, null, default);
        Assert.Equal(2, confirmed.Missing);
        Assert.False(await _db.MediaItems.AnyAsync());

        // Still empty, but already let through: not held again, so the files go once their grace period is up.
        await _db.MediaFiles.IgnoreQueryFilters().ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddDays(-2)));
        var later = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(2, later.Removed);
        Assert.Null((await _db.LibraryFolders.AsNoTracking().SingleAsync()).Hold);
    }

    [Fact]
    public async Task FolderEmptiedByRadarr_IsHidden_ButNotByAPlainScan()
    {
        var root = Touch("movies/Only (2001)/Only (2001).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        Directory.Delete(Path.Combine(movies, "Only (2001)"), recursive: true);

        var plain = await Scanner().ScanAsync(library, Path.Combine(movies, "Only (2001)"), default);
        var deleted = await Scanner().ScanAsync(library, Path.Combine(movies, "Only (2001)"), retryFailedProbes: false, deleted: true, default);
        var full = await Scanner().ScanAsync(library, null, default);

        Assert.Equal((0, 1), (plain.Missing, deleted.Missing));
        Assert.False(await _db.MediaItems.AnyAsync());
        Assert.Null((await _db.LibraryFolders.AsNoTracking().SingleAsync()).Hold); // The full scan after doesn't hold it either.
        Assert.Equal(0, full.Missing);
    }

    [Fact]
    public async Task OneTitleDeletedByRadarr_WhileTheFolderLooksUnmounted_LetsOnlyThatTitleGo()
    {
        var root = Touch("movies/A (2001)/A (2001).mkv", "movies/B (2002)/B (2002).mkv", "movies/C (2003)/C (2003).mkv", "movies/Old (1990)/Old (1990).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        Directory.Delete(Path.Combine(movies, "Old (1990)"), recursive: true);
        await Scanner().ScanAsync(library, null, default);
        await _db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddHours(-23)));

        // The share drops; Radarr, which still sees it, deletes A.
        foreach (var film in new[] { "A (2001)", "B (2002)", "C (2003)" })
        {
            Directory.Delete(Path.Combine(movies, film), recursive: true);
        }

        await Scanner().ScanAsync(library, null, default);
        await _db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddDays(-2)));
        var deleted = await Scanner().ScanAsync(library, Path.Combine(movies, "A (2001)"), retryFailedProbes: false, deleted: true, default);
        var folder = await _db.LibraryFolders.AsNoTracking().SingleAsync();
        var full = await Scanner().ScanAsync(library, null, default);

        Assert.Equal((1, 0), (deleted.Missing, deleted.Removed)); // Nothing purged while the folder looks unmounted.
        Assert.Equal((FolderHold.FolderEmpty, (DateTime?)null), (folder.Hold, folder.ReleasedAt));
        Assert.Equal(0, full.Missing);
        Assert.Equal(["B", "C"], await _db.MediaItems.OrderBy(i => i.Title).Select(i => i.Title).ToListAsync());
    }

    [Fact]
    public async Task DeletedFolder_SentInAnotherUnicodeSpelling_IsHidden()
    {
        var decomposed = "Léon".Normalize(System.Text.NormalizationForm.FormD);
        var root = Touch($"tv/{decomposed}/Season 01/{decomposed} - S01E01.mkv", "tv/Other/Season 01/Other - S01E01.mkv");
        var tv = Path.Combine(root, "tv");
        var library = await AddLibrary(LibraryType.TvShows, tv);
        await Scanner().ScanAsync(library, null, default);

        Directory.Delete(Path.Combine(tv, decomposed), recursive: true);
        var result = await Scanner().ScanAsync(library, Path.Combine(tv, "Léon".Normalize(System.Text.NormalizationForm.FormC)), default);

        Assert.Equal(1, result.Missing);
        Assert.Equal(["Other"], await _db.MediaItems.Where(i => i.Kind == MediaKind.Series).Select(i => i.Title).ToListAsync());
    }

    [Fact]
    public async Task FilmBackUnderANewFolder_KeepsItsWatchStateAndCollections()
    {
        var root = Touch("movies/Dune (2021) {tmdb-438631}/Dune (2021).mkv", "movies/Heat (1995)/Heat (1995).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        var dune = await _db.MediaItems.SingleAsync(i => i.Title == "Dune");
        var collection = new Collection { Name = "Sci-fi" };
        _db.Collections.Add(collection);
        _db.CollectionItems.Add(new CollectionItem { CollectionId = collection.Id, MediaItemId = dune.Id, Position = 3 });
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = dune.Id, Watched = true, PlayCount = 2 });
        await _db.SaveChangesAsync();

        // Radarr renames the folder.
        Directory.Move(Path.Combine(movies, "Dune (2021) {tmdb-438631}"), Path.Combine(movies, "Dune Part One (2021) {tmdb-438631}"));
        await Scanner().ScanAsync(library, null, default);

        _db.ChangeTracker.Clear();
        var renamed = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Movie && i.TmdbId == "438631");
        Assert.NotEqual(dune.Id, renamed.Id);
        Assert.True((await _db.PlaybackStates.SingleAsync(p => p.MediaItemId == renamed.Id)).Watched);
        Assert.Equal(3, (await _db.CollectionItems.SingleAsync(c => c.MediaItemId == renamed.Id)).Position);
    }

    [Fact]
    public async Task FilmBackUnderANewFolder_IsHandedOverOnce()
    {
        var root = Touch("movies/Dune (2021) {tmdb-438631}/Dune (2021).mkv", "movies/Heat (1995)/Heat (1995).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        var dune = await _db.MediaItems.SingleAsync(i => i.Title == "Dune");
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = dune.Id, Watched = true, PlayCount = 2 });
        await _db.SaveChangesAsync();
        Directory.Move(Path.Combine(movies, "Dune (2021) {tmdb-438631}"), Path.Combine(movies, "Dune Part One (2021) {tmdb-438631}"));
        await Scanner().ScanAsync(library, null, default);

        // Watched again from the start under its new folder, then a scan while the old item waits out its grace period.
        _db.ChangeTracker.Clear();
        var renamed = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Movie && i.TmdbId == "438631");
        await _db.PlaybackStates.Where(p => p.MediaItemId == renamed.Id).ExecuteDeleteAsync();
        await Scanner().ScanAsync(library, null, default);

        Assert.False(await _db.PlaybackStates.AnyAsync(p => p.MediaItemId == renamed.Id));
        Assert.NotNull((await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().SingleAsync(i => i.Id == dune.Id)).HandedOverAt);
    }

    [Fact]
    public async Task ShowBackUnderANewFolder_KeepsTheHistoryOfEpisodesThatArriveLater()
    {
        var root = Touch("tv/Show {tvdb-81189}/Season 01/Show - S01E01.mkv", "tv/Show {tvdb-81189}/Season 01/Show - S01E02.mkv", "tv/Other/Season 01/Other - S01E01.mkv");
        var tv = Path.Combine(root, "tv");
        var library = await AddLibrary(LibraryType.TvShows, tv);
        await Scanner().ScanAsync(library, null, default);
        var episodes = await _db.MediaItems.Where(i => i.Kind == MediaKind.Episode && i.Parent!.Parent!.TvdbId == "81189").ToListAsync();
        _db.PlaybackStates.AddRange(episodes.Select(e => new PlaybackState { MediaItemId = e.Id, Watched = true, PlayCount = e.EpisodeNumber!.Value }));
        await _db.SaveChangesAsync();

        // Sonarr moves the show to a new folder, the second episode a little after the first.
        var moved = Path.Combine(tv, "Show (2008) {tvdb-81189}", "Season 01");
        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(tv, "Show {tvdb-81189}", "Season 01", "Show - S01E01.mkv"), Path.Combine(moved, "Show - S01E01.mkv"));
        File.Move(Path.Combine(tv, "Show {tvdb-81189}", "Season 01", "Show - S01E02.mkv"), Path.Combine(root, "E02.mkv"));
        Directory.Delete(Path.Combine(tv, "Show {tvdb-81189}"), recursive: true);
        await Scanner().ScanAsync(library, null, default);
        File.Move(Path.Combine(root, "E02.mkv"), Path.Combine(moved, "Show - S01E02.mkv"));
        await Scanner().ScanAsync(library, null, default);

        _db.ChangeTracker.Clear();
        var counts = await _db.PlaybackStates.Where(p => p.MediaItem!.MissingSince == null && p.MediaItem.Parent!.Parent!.Path!.EndsWith("Show (2008) {tvdb-81189}"))
            .OrderBy(p => p.MediaItem!.EpisodeNumber).Select(p => p.PlayCount).ToListAsync();
        Assert.Equal([1, 2], counts);
        Assert.Equal(0, await _db.WatchTombstones.CountAsync());
    }

    [Fact]
    public async Task HandedOverTitles_AreNotTombstonedAgain_WhenTheOldItemsArePurged()
    {
        var root = Touch("tv/Show {tvdb-81189}/Season 01/Show - S01E01.mkv", "tv/Show {tvdb-81189}/Season 01/Show - S01E02.mkv", "tv/Other/Season 01/Other - S01E01.mkv",
            "movies/Dune (2021) {tmdb-438631}/Dune (2021).mkv", "movies/Heat (1995)/Heat (1995).mkv");
        var tv = Path.Combine(root, "tv");
        var movies = Path.Combine(root, "movies");
        var shows = await AddLibrary(LibraryType.TvShows, tv);
        var films = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(shows, null, default);
        await Scanner().ScanAsync(films, null, default);
        var watched = await _db.MediaItems.Where(i => (i.Kind == MediaKind.Episode && i.Parent!.Parent!.TvdbId == "81189") || i.Title == "Dune").ToListAsync();
        _db.PlaybackStates.AddRange(watched.Select(e => new PlaybackState { MediaItemId = e.Id, Watched = true }));
        await _db.SaveChangesAsync();

        // Sonarr moves the show but only the first episode comes with it; Radarr renames the film's folder.
        var moved = Path.Combine(tv, "Show (2008) {tvdb-81189}", "Season 01");
        Directory.CreateDirectory(moved);
        File.Move(Path.Combine(tv, "Show {tvdb-81189}", "Season 01", "Show - S01E01.mkv"), Path.Combine(moved, "Show - S01E01.mkv"));
        Directory.Delete(Path.Combine(tv, "Show {tvdb-81189}"), recursive: true);
        Directory.Move(Path.Combine(movies, "Dune (2021) {tmdb-438631}"), Path.Combine(movies, "Dune Part One (2021) {tmdb-438631}"));
        await Scanner().ScanAsync(shows, null, default);
        await Scanner().ScanAsync(films, null, default);
        Assert.Equal(1, await _db.WatchTombstones.CountAsync()); // the second episode, for when it's back

        // The old show and film wait out their grace period, then go.
        await _db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddDays(-2)));
        await Scanner().ScanAsync(shows, null, default);
        await Scanner().ScanAsync(films, null, default);

        Assert.False(await _db.MediaItems.IgnoreQueryFilters().AnyAsync(i => watched.Select(w => w.Id).Contains(i.Id)));
        var tombstones = await _db.WatchTombstones.ToListAsync();
        Assert.Equal((MediaKind.Episode, 2), (Assert.Single(tombstones).Kind, tombstones[0].EpisodeNumber!.Value));
    }

    [Fact]
    public async Task FilmBackAfterItWasRemoved_GetsItsWatchStateBackFromTheTombstone()
    {
        var root = Touch("movies/Dune (2021) {tmdb-438631}/Dune (2021).mkv", "movies/Heat (1995)/Heat (1995).mkv", "movies/Up (2009)/Up (2009).mkv");
        var movies = Path.Combine(root, "movies");
        var library = await AddLibrary(LibraryType.Movies, movies);
        await Scanner().ScanAsync(library, null, default);
        var dune = await _db.MediaItems.SingleAsync(i => i.Title == "Dune");
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = dune.Id, PositionSeconds = 600 });
        await _db.SaveChangesAsync();

        var away = Path.Combine(root, "away");
        Directory.Move(Path.Combine(movies, "Dune (2021) {tmdb-438631}"), away);
        await Scanner().ScanAsync(library, null, default);
        await _db.MediaFiles.IgnoreQueryFilters().Where(f => f.MissingSince != null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.MissingSince, DateTime.UtcNow.AddDays(-2)));
        await Scanner().ScanAsync(library, null, default);
        Assert.False(await _db.MediaItems.IgnoreQueryFilters().AnyAsync(i => i.Id == dune.Id));
        Assert.Equal(1, await _db.WatchTombstones.CountAsync());

        Directory.Move(away, Path.Combine(movies, "Dune (2021) {tmdb-438631}"));
        await Scanner().ScanAsync(library, null, default);

        _db.ChangeTracker.Clear();
        var back = await _db.MediaItems.SingleAsync(i => i.Title == "Dune");
        Assert.Equal(600, (await _db.PlaybackStates.SingleAsync(p => p.MediaItemId == back.Id)).PositionSeconds);
        Assert.Equal(0, await _db.WatchTombstones.CountAsync());
    }

    [Fact]
    public async Task ScopedScan_WithOtherUnicodeSpelling_MatchesTheExistingSeries()
    {
        var decomposed = "Léon";
        var composed = decomposed.Normalize(System.Text.NormalizationForm.FormC);
        var root = Touch($"tv/{decomposed}/Season 01/{decomposed} - S01E01.mkv");
        var tv = Path.Combine(root, "tv");
        var library = await AddLibrary(LibraryType.TvShows, tv);
        await Scanner().ScanAsync(library, null, default);

        var result = await Scanner().ScanAsync(library, Path.Combine(tv, composed), default);

        Assert.Equal((0, 1, 0), (result.Added, result.Unchanged, result.Missing));
        Assert.Equal(1, await _db.MediaItems.IgnoreQueryFilters().CountAsync(i => i.Kind == MediaKind.Series));
        Assert.Equal(1, await _db.MediaFiles.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Scope_EscapingTheRoot_IsIgnored()
    {
        var root = Touch("tv/A/Season 01/A - S01E01.mkv", "elsewhere/B/Season 01/B - S01E01.mkv");
        var library = await AddLibrary(LibraryType.TvShows, Path.Combine(root, "tv"));

        var result = await Scanner().ScanAsync(library, Path.Combine(root, "tv", "..", "elsewhere", "B"), default);

        Assert.Equal(0, result.Added);
        Assert.False(await _db.MediaFiles.AnyAsync());
    }

    [Fact]
    public async Task ScopedScan_OnlyTouchesThatFolder()
    {
        var root = Touch("tv/A/Season 01/A - S01E01.mkv", "tv/B/Season 01/B - S01E01.mkv");
        var library = await AddLibrary(LibraryType.TvShows, Path.Combine(root, "tv"));

        var result = await Scanner().ScanAsync(library, Path.Combine(root, "tv", "A"), default);

        Assert.Equal(1, result.Added);
        Assert.Equal(["A"], await _db.MediaItems.Where(i => i.Kind == MediaKind.Series).Select(i => i.Title).ToListAsync());
    }

    [Fact]
    public async Task MissingRoot_DoesNotDeleteLibrary()
    {
        var root = Touch("tv/A/Season 01/A - S01E01.mkv");
        var tv = Path.Combine(root, "tv");
        var library = await AddLibrary(LibraryType.TvShows, tv);
        await Scanner().ScanAsync(library, null, default);

        Directory.Move(tv, tv + "-unplugged");
        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(0, result.Removed);
        Assert.Equal(1, await _db.MediaFiles.CountAsync());
    }

    [Fact]
    public async Task ProbeFailure_StillAddsFile_AndRetriesWhenChangedOrAsked()
    {
        var root = Touch("movies/Broken (2020)/Broken (2020).mkv");
        var library = await AddLibrary(LibraryType.Movies, Path.Combine(root, "movies"));
        _probe.Fail = true;

        var first = await Scanner().ScanAsync(library, null, default);
        var unchanged = await Scanner().ScanAsync(library, null, default);
        _probe.Fail = false;
        var asked = await Scanner().ScanAsync(library, null, retryFailedProbes: true, default);

        Assert.Equal((1, 1), (first.Added, first.ProbeFailures));
        Assert.Equal((1, 0), (unchanged.Unchanged, unchanged.ProbeFailures));
        Assert.Equal(2, _probe.Calls);
        Assert.Equal(1, asked.Updated);
        Assert.Null((await _db.MediaFiles.SingleAsync()).ProbeError);
    }

    [Fact]
    public async Task Music_BuildsArtistAlbumTrackTree_FromTagsAndFolders()
    {
        var root = Touch(
            "music/Radiohead/OK Computer (1997)/01 - Airbag.flac",
            "music/Radiohead/OK Computer (1997)/02 - Paranoid Android.flac",
            "music/Radiohead/OK Computer (1997)/cover.jpg",
            "music/Various/Drive OST/03 - Nightcall.mp3",
            "music/Untagged Artist/Some Album/07 Song Name.mp3");
        _probe.Tags["01 - Airbag.flac"] = new() { ["artist"] = "Radiohead", ["album"] = "OK Computer", ["title"] = "Airbag", ["track"] = "1/12", ["date"] = "1997-05-21" };
        _probe.Tags["02 - Paranoid Android.flac"] = new() { ["ARTIST"] = "Radiohead", ["ALBUM"] = "OK Computer", ["TITLE"] = "Paranoid Android", ["TRACK"] = "2" };
        // Compilation: the album artist groups it, not the track artist.
        _probe.Tags["03 - Nightcall.mp3"] = new() { ["artist"] = "Kavinsky", ["album_artist"] = "Various Artists", ["album"] = "Drive (Original Motion Picture Soundtrack)", ["title"] = "Nightcall", ["track"] = "3" };
        var library = await AddLibrary(LibraryType.Music, Path.Combine(root, "music"));

        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(4, result.Added);
        var artists = await _db.MediaItems.Where(i => i.Kind == MediaKind.Artist).OrderBy(i => i.SortTitle).Select(i => i.Title).ToListAsync();
        Assert.Equal(["Radiohead", "Untagged Artist", "Various Artists"], artists);

        var okComputer = await _db.MediaItems.Include(i => i.Children).SingleAsync(i => i.Title == "OK Computer");
        Assert.Equal(1997, okComputer.Year);
        Assert.StartsWith("file://", okComputer.PosterUrl, StringComparison.Ordinal);
        Assert.Equal(["Airbag", "Paranoid Android"], okComputer.Children.OrderBy(t => t.SortTitle).Select(t => t.Title));
        Assert.Equal(okComputer.PosterUrl, (await _db.MediaItems.SingleAsync(i => i.Title == "Radiohead")).PosterUrl);

        var untagged = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Track && i.Title == "Song Name");
        Assert.Equal(7, untagged.TrackNumber);
        Assert.Equal("Some Album", (await _db.MediaItems.SingleAsync(i => i.Id == untagged.ParentId)).Title);

        // A compilation's track keeps its own artist; a track by the album's artist doesn't repeat it.
        Assert.Equal("Kavinsky", (await _db.MediaItems.SingleAsync(i => i.Title == "Nightcall")).TrackArtist);
        Assert.Null((await _db.MediaItems.SingleAsync(i => i.Title == "Airbag")).TrackArtist);
    }

    [Fact]
    public async Task Music_ACompilationFlaggedWithoutAnAlbumArtist_IsOneAlbumUnderVariousArtists()
    {
        var root = Touch("music/Compilations/Now That's What I Call Music/01 - Blue Monday.mp3", "music/Compilations/Now That's What I Call Music/02 - Relax.mp3");
        _probe.Tags["01 - Blue Monday.mp3"] = new() { ["artist"] = "New Order", ["album"] = "Now That's What I Call Music", ["title"] = "Blue Monday", ["track"] = "1", ["compilation"] = "1" };
        _probe.Tags["02 - Relax.mp3"] = new() { ["artist"] = "Frankie Goes to Hollywood", ["album"] = "Now That's What I Call Music", ["title"] = "Relax", ["track"] = "2", ["compilation"] = "1" };
        var library = await AddLibrary(LibraryType.Music, Path.Combine(root, "music"));

        await Scanner().ScanAsync(library, null, default);

        Assert.Equal(["Various Artists"], await _db.MediaItems.Where(i => i.Kind == MediaKind.Artist).Select(i => i.Title).ToListAsync());
        var album = await _db.MediaItems.Include(i => i.Children).SingleAsync(i => i.Kind == MediaKind.Album);
        Assert.Equal(["New Order", "Frankie Goes to Hollywood"], album.Children.OrderBy(t => t.SortTitle).Select(t => t.TrackArtist));
    }

    [Fact]
    public async Task Music_AnAlbumFolderIsOneAlbum_UnderTheNameLidarrGaveIt()
    {
        var root = Touch("music/Dire Straits/Making Movies (1980)/01 - Tunnel of Love.flac");
        _probe.Tags["01 - Tunnel of Love.flac"] = new() { ["artist"] = "Dire straits", ["album"] = "Making movies", ["title"] = "Tunnel of Love", ["track"] = "1", ["date"] = "1996" };
        _probe.Tags["02 - Romeo and Juliet.flac"] = new() { ["artist"] = "Dire straits", ["album"] = "Making movies", ["title"] = "Romeo and Juliet", ["track"] = "2", ["date"] = "1996" };
        _probe.Tags["03 - Skateaway.flac"] = new() { ["artist"] = "Mark Knopfler", ["album"] = "Making Movies (Remaster)", ["title"] = "Skateaway", ["track"] = "3" };
        var library = await AddLibrary(LibraryType.Music, Path.Combine(root, "music"));
        await Scanner().ScanAsync(library, null, default);

        // The metadata refresh gives them Lidarr's names, keeping the tags' as the original title.
        var artist = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Artist);
        var album = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Album);
        (artist.OriginalTitle, artist.Title) = (artist.Title, "Dire Straits");
        (album.OriginalTitle, album.Title, album.Year) = (album.Title, "Making Movies", 1980);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // Another track lands in the folder, and one tagged differently: the same album and artist, not new ones.
        Touch("music/Dire Straits/Making Movies (1980)/02 - Romeo and Juliet.flac", "music/Dire Straits/Making Movies (1980)/03 - Skateaway.flac");
        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(2, result.Added);
        Assert.Equal(1, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Artist));
        var albums = await _db.MediaItems.Include(i => i.Children).Where(i => i.Kind == MediaKind.Album).ToListAsync();
        Assert.Single(albums);
        Assert.Equal("Making Movies", albums[0].Title);
        Assert.Equal(1980, albums[0].Year);
        Assert.Equal(["Tunnel of Love", "Romeo and Juliet", "Skateaway"], albums[0].Children.OrderBy(t => t.SortTitle).Select(t => t.Title));

        // "Dire straits" is the artist under the tags' spelling, so it isn't a track artist; Knopfler alone is.
        var trackArtists = albums[0].Children.ToDictionary(t => t.Title, t => t.TrackArtist);
        Assert.Null(trackArtists["Romeo and Juliet"]);
        Assert.Equal("Mark Knopfler", trackArtists["Skateaway"]);
    }

    [Fact]
    public async Task HomeVideos_FoldersAreShows_TheirFoldersSeasons_InNameOrder()
    {
        var root = Touch(
            "home/Family Holidays/2021 Lakes/Walk.mp4",
            "home/Family Holidays/2019 Cornwall/Boat trip.mov",
            "home/Family Holidays/2019 Cornwall/Beach day.mp4",
            "home/Family Holidays/2019 Cornwall/Day 2/Part 10.mp4",
            "home/Family Holidays/2019 Cornwall/Day 2/Part 2.mp4",
            "home/Family Holidays/Trailer.mp4",
            "home/Kids' plays/Nativity 2022.mp4",
            "home/Loose clip.mkv");
        var library = await AddLibrary(LibraryType.HomeVideos, Path.Combine(root, "home"));

        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(8, result.Added);
        var items = await _db.MediaItems.ToListAsync();
        Assert.Equal(["Family Holidays", "home", "Kids' plays"], items.Where(i => i.Kind == MediaKind.Series).Select(i => i.Title).Order(StringComparer.OrdinalIgnoreCase));
        var holidays = items.Single(i => i.Kind == MediaKind.Series && i.Title == "Family Holidays");
        // Videos straight in the show's folder first, as a "season" named after the show; then its folders by name.
        Assert.Equal([(1, "Family Holidays"), (2, "2019 Cornwall"), (3, "2021 Lakes")],
            items.Where(i => i.ParentId == holidays.Id).OrderBy(i => i.SeasonNumber).Select(i => (i.SeasonNumber!.Value, i.Title)));
        var cornwall = items.Single(i => i.Title == "2019 Cornwall");
        // Deeper folders join their season, and numbers in names count as numbers.
        Assert.Equal(["Beach day", "Boat trip", "Part 2", "Part 10"],
            items.Where(i => i.ParentId == cornwall.Id).OrderBy(i => i.EpisodeNumber).Select(i => i.Title));
        Assert.All(items.Where(i => i.ParentId == cornwall.Id), e => Assert.Equal(2, e.SeasonNumber));
    }

    [Fact]
    public async Task HomeVideos_ANewFolder_KeepsTheOthersAndTheirVideos()
    {
        var root = Touch("home/Holidays/2021/Walk.mp4", "home/Holidays/2023/Swim.mp4");
        var library = await AddLibrary(LibraryType.HomeVideos, Path.Combine(root, "home"));
        await Scanner().ScanAsync(library, null, default);
        var walk = await _db.MediaItems.SingleAsync(i => i.Title == "Walk");

        Touch("home/Holidays/2022/Picnic.mp4");
        var result = await Scanner().ScanAsync(library, null, default);

        Assert.Equal(1, result.Added);
        var seasons = await _db.MediaItems.Where(i => i.Kind == MediaKind.Season).OrderBy(i => i.SeasonNumber).Select(i => i.Title).ToListAsync();
        Assert.Equal(["2021", "2022", "2023"], seasons);
        Assert.Equal(walk.Id, (await _db.MediaItems.SingleAsync(i => i.Title == "Walk")).Id);
        Assert.Equal(3, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Episode));
    }

    private LibraryScanner Scanner() => new(_db, _probe, new ScanProgress(), NullLogger<LibraryScanner>.Instance);

    private async Task<Library> AddLibrary(LibraryType type, string folder)
    {
        var library = new Library { Name = type.ToString(), Type = type, Folders = [new LibraryFolder { Path = folder }] };
        _db.Libraries.Add(library);
        await _db.SaveChangesAsync();
        return library;
    }

    private string Touch(params string[] files)
    {
        foreach (var file in files)
        {
            var path = Path.Combine(_dir, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "x");
        }

        return _dir;
    }

    private sealed class FakeProbe : IMediaProbe
    {
        public int Calls;
        public bool Fail;

        /// <summary>Tags to report, by file name.</summary>
        public Dictionary<string, Dictionary<string, string>> Tags { get; } = [];

        public Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            if (Fail)
            {
                throw new MediaProbeException("Invalid data found when processing input");
            }

            var tags = Tags.TryGetValue(Path.GetFileName(path), out var t)
                ? new Dictionary<string, string>(t, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(new ProbeResult("mkv", 1800, 5_000_000, VideoRange.Sdr,
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "hevc", Width = 1920, Height = 1080 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "eac3", Channels = 6, IsDefault = true },
            ]) { Tags = tags });
        }
    }
}
