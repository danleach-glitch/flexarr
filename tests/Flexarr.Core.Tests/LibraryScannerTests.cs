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
    public async Task Rescan_SkipsUnchanged_ProbesChanged_RemovesMissingAndPrunes()
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

        Assert.Equal((0, 1, 1, 1), (result.Added, result.Updated, result.Removed, result.Unchanged));
        Assert.Equal(1, _probe.Calls);
        Assert.False(await _db.MediaItems.AnyAsync(i => i.Title == "Other"), "empty series should be pruned");
        Assert.Equal(2, await _db.MediaItems.CountAsync(i => i.Kind == MediaKind.Episode));
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
    public async Task ProbeFailure_StillAddsFile_AndRetriesNextScan()
    {
        var root = Touch("movies/Broken (2020)/Broken (2020).mkv");
        var library = await AddLibrary(LibraryType.Movies, Path.Combine(root, "movies"));
        _probe.Fail = true;

        var first = await Scanner().ScanAsync(library, null, default);
        _probe.Fail = false;
        var second = await Scanner().ScanAsync(library, null, default);

        Assert.Equal((1, 1), (first.Added, first.ProbeFailures));
        Assert.Equal(1, second.Updated);
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
