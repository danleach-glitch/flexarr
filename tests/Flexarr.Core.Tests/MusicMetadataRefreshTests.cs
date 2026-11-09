using System.Net;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>A music library refreshed from Lidarr: the same Dire Straits collection as the matcher's fixtures.</summary>
public sealed class MusicMetadataRefreshTests : IAsyncLifetime
{
    private const string Root = "/Volumes/Media/data/music";
    private const string MakingMoviesFolder = $"{Root}/Dire Straits/Making Movies (1980)";

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-music-").FullName;
    private readonly FakeLidarr _lidarr = new();
    private FlexarrDbContext _db = null!;
    private Library _library = null!;
    private MediaItem _artist = null!;

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>()
            .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
        _library = new Library { Name = "Music", Type = LibraryType.Music, Folders = [new LibraryFolder { Path = Root }] };
        _db.Libraries.Add(_library);

        // As the scanner leaves them: tag spellings, a remaster's year, and the one embedded cover doing for the artist too.
        var embedded = $"embedded://{Root}/Dire Straits/Love Over Gold (1982)/01 - Telegraph Road.flac";
        _artist = new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = "Dire straits", SortTitle = "dire straits", PosterUrl = embedded };
        _db.MediaItems.AddRange(_artist,
            Album("Communiqué CD", 1979, "Communiqué (1979)", null),
            Album("Making movies", 1996, "Making Movies (1980)", null),
            Album("Love Over Gold", 1982, "Love Over Gold (1982)", embedded),
            Album("Alchemy", 1984, "Alchemy (1984)", null)); // not in Lidarr
        await _db.SaveChangesAsync();

        foreach (var file in new[] { "artist", "album", "trackfile" })
        {
            _lidarr.Responses[$"/api/v1/{file}" + (file == "artist" ? "" : "?artistId=1")] = File.ReadAllText(Path.Combine("Fixtures", $"lidarr-{file}.json"));
        }
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        _lidarr.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task Lidarr_NamesDatesAndIllustrates_TheArtistAndItsAlbums()
    {
        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(new MetadataRefreshResult(1, 0, 0), result);
        Assert.Equal(["/api/v1/artist", "/api/v1/album?artistId=1", "/api/v1/trackfile?artistId=1"], _lidarr.Paths.Where(p => !p.Contains("mediacover", StringComparison.Ordinal)));

        var artist = await _db.MediaItems.Include(i => i.Genres).SingleAsync(i => i.Kind == MediaKind.Artist);
        Assert.Equal("Dire Straits", artist.Title);
        Assert.Equal("Dire straits", artist.OriginalTitle); // the tags' spelling, so the scanner still knows the files
        Assert.Equal("614e3804-7d34-41ba-857f-811bad7c2b7a", artist.MusicBrainzId);
        Assert.Equal("arr://Lidarr/mediacover/artist/1/poster.jpg?lastWrite=639285234250000000", artist.PosterUrl);
        Assert.Equal("arr://Lidarr/mediacover/artist/1/fanart.jpg?lastWrite=639285234250000000", artist.BackdropUrl);
        Assert.Equal("arr://Lidarr/mediacover/artist/1/clearlogo.png?lastWrite=639285234240000000", artist.LogoUrl);
        Assert.Equal(1, artist.ArrId);
        Assert.Equal("Lidarr", artist.MetadataSource);
        Assert.Equal(9.4, artist.CommunityRating);
        Assert.Contains(artist.Genres, g => g.Name == "Soft Rock");

        var albums = await _db.MediaItems.Where(i => i.Kind == MediaKind.Album).OrderBy(i => i.Year).ToListAsync();
        Assert.Equal(["Communiqué", "Making Movies", "Love Over Gold", "Alchemy"], albums.Select(a => a.Title));
        Assert.Equal([1979, 1980, 1982, 1984], albums.Select(a => a.Year));
        Assert.Equal(new DateOnly(1980, 10, 17), albums[1].ReleaseDate);
        Assert.Equal("Making movies", albums[1].OriginalTitle);
        Assert.Equal("5b0a15ce-8aa9-323b-8fb6-02b657756ba9", albums[1].MusicBrainzId);
        Assert.Equal(38, albums[1].RuntimeMinutes);
        Assert.Equal("arr://Lidarr/mediacover/album/2/cover.jpg?lastWrite=639285234210000000", albums[0].PosterUrl);
        Assert.Equal("arr://Lidarr/mediacover/album/3/cover.jpg?lastWrite=639285234200000000", albums[1].PosterUrl);
        Assert.Equal("arr://Lidarr/mediacover/album/4/cover.jpg?lastWrite=639285234220000000", albums[2].PosterUrl); // over the embedded art
        Assert.Equal("Lidarr", albums[2].MetadataSource);

        // Alchemy isn't in Lidarr: its tags stand, and it's marked as such.
        Assert.Null(albums[3].PosterUrl);
        Assert.Null(albums[3].OriginalTitle);
        Assert.Equal(MetadataRefresher.Tags, albums[3].MetadataSource);
        Assert.All(albums, a => Assert.NotNull(a.MetadataUpdatedAt));
    }

    [Fact]
    public async Task TheKindOfRelease_ComesFromLidarrsTypes()
    {
        // A live album in a folder Lidarr has no files for, so it's matched by its MusicBrainz id.
        var live = Album("Brothers in Arms (Live)", 2005, "Brothers in Arms (2005)", null);
        live.MusicBrainzId = "e0e0e0e0-0000-4000-8000-000000000007";
        _db.MediaItems.Add(live);
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(_library, force: false, default);

        var albums = await _db.MediaItems.Where(i => i.Kind == MediaKind.Album).OrderBy(i => i.Year).ToListAsync();
        Assert.Equal(["Communiqué", "Making Movies", "Love Over Gold", "Alchemy", "Brothers in Arms"], albums.Select(a => a.Title));
        // Alchemy isn't in Lidarr: nobody says what it is, and the page takes it for an album.
        Assert.Equal([AlbumKind.Album, AlbumKind.Album, AlbumKind.Album, null, AlbumKind.Live], albums.Select(a => a.AlbumType));
    }

    [Fact]
    public async Task TheArtwork_IsFetchedFromLidarrWithItsKey_AndNeverHandedOut()
    {
        await Refresher().RefreshLibraryAsync(_library, force: false, default);

        var fetched = _lidarr.Requests.Where(r => r.RequestUri!.PathAndQuery.Contains("mediacover", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(fetched);
        Assert.All(fetched, r =>
        {
            Assert.Equal("lidarr.test", r.RequestUri!.Host);
            Assert.Equal("key", r.Headers.GetValues("X-Api-Key").Single());
        });
        Assert.Contains(fetched, r => r.RequestUri!.PathAndQuery == "/api/v1/mediacover/artist/1/poster.jpg?lastWrite=639285234250000000");
        // What's stored (and so what the image endpoints work from) names the instance, never its address or key.
        var stored = (await _db.MediaItems.Select(i => new { i.PosterUrl, i.BackdropUrl, i.LogoUrl }).ToListAsync())
            .SelectMany(i => new[] { i.PosterUrl, i.BackdropUrl, i.LogoUrl }).OfType<string>().ToList();
        Assert.All(stored, url => Assert.True(ArtworkUrl.IsArr(url) || ArtworkUrl.IsLocal(url), url));
        Assert.All(stored, url => Assert.DoesNotContain("lidarr.test", url, StringComparison.Ordinal));
        Assert.All(stored, url => Assert.DoesNotContain("key", url, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AFollowedArtistsPlaceholder_StaysWhileLidarrFollowsThem_AndGoesWhenItDoesnt()
    {
        var artists = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine("Fixtures", "lidarr-artist.json")))!.AsArray();
        artists.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":2,"artistName":"Mark Knopfler","foreignArtistId":"mb-knopfler","monitored":true,"monitorNewItems":"new","path":"/data/music/Mark Knopfler"}"""));
        artists.Add(System.Text.Json.Nodes.JsonNode.Parse("""{"id":3,"artistName":"Wet Leg","foreignArtistId":"mb-wetleg","monitored":true,"monitorNewItems":"none","path":"/data/music/Wet Leg"}"""));
        _lidarr.Responses["/api/v1/artist"] = artists.ToJsonString();
        foreach (var id in new[] { 2, 3 })
        {
            _lidarr.Responses[$"/api/v1/album?artistId={id}"] = "[]";
            _lidarr.Responses[$"/api/v1/trackfile?artistId={id}"] = "[]";
        }

        _db.MediaItems.AddRange(
            new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = "Mark Knopfler", SortTitle = "mark knopfler", MusicBrainzId = "mb-knopfler", Placeholder = true },
            new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = "Wet Leg", SortTitle = "wet leg", MusicBrainzId = "mb-wetleg", Placeholder = true },
            new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = "Gone", SortTitle = "gone", MusicBrainzId = "mb-gone", Placeholder = true });
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        var left = await _db.MediaItems.Where(i => i.Kind == MediaKind.Artist && i.Placeholder).Select(i => i.Title).ToListAsync();
        Assert.Equal(["Mark Knopfler"], left);
    }

    [Fact]
    public async Task ARenamedArtist_GoneFromLidarr_GetsItsTagsBack()
    {
        await Refresher().RefreshLibraryAsync(_library, force: false, default);
        _lidarr.Responses["/api/v1/artist"] = "[]";

        var result = await Refresher().RefreshLibraryAsync(_library, force: true, default);

        Assert.Equal(new MetadataRefreshResult(0, 0, 1), result);
        var artist = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Artist);
        Assert.Equal("Dire straits", artist.Title);
        Assert.Null(artist.OriginalTitle);
        Assert.Equal(MetadataRefresher.Tags, artist.MetadataSource);
        Assert.False(ArtworkUrl.IsArr(artist.PosterUrl));
        var makingMovies = await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder);
        Assert.Equal("Making movies", makingMovies.Title);
        Assert.Null(makingMovies.PosterUrl);
    }

    [Fact]
    public async Task NothingDue_AsksLidarrNothing_AndAScannedAlbumFolderMakesItsArtistDue()
    {
        await Refresher().RefreshLibraryAsync(_library, force: false, default);
        _lidarr.Paths.Clear();

        Assert.Equal(new MetadataRefreshResult(0, 0, 0), await Refresher().RefreshLibraryAsync(_library, force: false, default));
        Assert.Empty(_lidarr.Paths);

        var again = await Refresher().RefreshLibraryAsync(_library, force: false, [$"{Root}/Dire Straits/Alchemy (1984)"], default);
        Assert.Equal(1, again.FromArr);
        Assert.Contains("/api/v1/artist", _lidarr.Paths);
    }

    [Fact]
    public async Task ScansOfOneFolder_ShareLidarrsArtistList_UntilItDoesntKnowAnArtist()
    {
        var list = new LidarrArtistList(TimeProvider.System);

        await Refresher(list).RefreshScannedAsync(_library, false, [MakingMoviesFolder], null, null, default);
        await Refresher(list).RefreshScannedAsync(_library, false, [MakingMoviesFolder], null, null, default);
        Assert.Single(_lidarr.Paths, p => p == "/api/v1/artist");

        // Someone new, added to Lidarr since: the import that started this scan.
        _db.MediaItems.Add(new MediaItem { LibraryId = _library.Id, Kind = MediaKind.Artist, Title = "New Name", SortTitle = "new name" });
        await _db.SaveChangesAsync();
        await Refresher(list).RefreshScannedAsync(_library, false, [MakingMoviesFolder], null, null, default);
        Assert.Equal(2, _lidarr.Paths.Count(p => p == "/api/v1/artist"));

        // A refresh asked for, or of the whole library, asks afresh.
        await Refresher(list).RefreshScannedAsync(_library, true, [MakingMoviesFolder], null, null, default);
        Assert.Equal(3, _lidarr.Paths.Count(p => p == "/api/v1/artist"));
    }

    [Fact]
    public async Task WithoutLidarr_TagsStand_AndOnlyColoursAreAdded()
    {
        var result = await Refresher(lidarr: false).RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(0, result.FromArr);
        Assert.DoesNotContain(_lidarr.Paths, p => p.StartsWith("/api", StringComparison.Ordinal));
        Assert.Equal("Making movies", (await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder)).Title);
    }

    [Fact]
    public async Task WithoutLidarr_ACoverWithNoColour_IsOnlyTriedOnce_AndKeepsItsTime()
    {
        var first = await Refresher(lidarr: false).RefreshLibraryAsync(_library, force: false, default);
        Assert.Equal(2, first.FromLookup); // the artist's and Love Over Gold's: the only artwork here, and no colour to find

        var times = await _db.MediaItems.AsNoTracking().Where(i => i.PosterUrl != null).Select(i => i.MetadataUpdatedAt).ToListAsync();
        Assert.All(times, t => Assert.NotNull(t));

        var again = await Refresher(lidarr: false).RefreshLibraryAsync(_library, force: false, default);
        Assert.Equal(0, again.FromLookup);

        // Asked to, it looks again, and the time still says nothing changed (it's in the artwork's address).
        var forced = await Refresher(lidarr: false).RefreshLibraryAsync(_library, force: true, default);
        Assert.Equal(2, forced.FromLookup);
        Assert.Equal(times, await _db.MediaItems.AsNoTracking().Where(i => i.PosterUrl != null).Select(i => i.MetadataUpdatedAt).ToListAsync());
    }

    [Fact]
    public async Task WithoutLidarr_AScanOfOneFolder_OnlyLooksAtColoursInThatFolder()
    {
        var other = await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder);
        other.PosterUrl = $"file://{MakingMoviesFolder}/cover.jpg";
        await _db.SaveChangesAsync();

        var result = await Refresher(lidarr: false).RefreshScannedAsync(_library, force: false, [MakingMoviesFolder], null, null, default);

        Assert.Equal(1, result.FromLookup);
        Assert.Equal(MetadataRefresher.Tags, (await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Path == MakingMoviesFolder)).MetadataSource);
        Assert.Null((await _db.MediaItems.AsNoTracking().SingleAsync(i => i.Path == $"{Root}/Dire Straits/Love Over Gold (1982)")).MetadataSource);
    }

    [Fact]
    public async Task AlbumsThatCouldntBeChecked_KeepWhatTheyHad()
    {
        await Refresher().RefreshLibraryAsync(_library, force: false, default);
        _lidarr.Responses.Remove("/api/v1/album?artistId=1"); // a 404 now: Lidarr mid-restart, say

        var result = await Refresher().RefreshLibraryAsync(_library, force: true, default);

        Assert.Equal(1, result.FromArr);
        var makingMovies = await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder);
        Assert.Equal("Making Movies", makingMovies.Title);
        Assert.Equal("arr://Lidarr/mediacover/album/3/cover.jpg?lastWrite=639285234200000000", makingMovies.PosterUrl);
    }

    [Fact]
    public async Task LidarrDown_IsNotTheArtistsFault()
    {
        _lidarr.TimeOut = true;

        var result = await Refresher().RefreshLibraryAsync(_library, force: false, default);

        Assert.Equal(new MetadataRefreshResult(0, 1, 0), result);
        Assert.Equal("Dire straits", (await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Artist)).Title);
    }

    [Fact]
    public async Task AnAlbumMatchedByHand_IsTheLidarrAlbumChosen_WhateverItsFolderSays()
    {
        // Lidarr has Making Movies' folder, but it was fixed to be the debut; Alchemy, which Lidarr lacks, to On Every Street.
        var makingMovies = await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder);
        var alchemy = await _db.MediaItems.SingleAsync(i => i.Title == "Alchemy");
        foreach (var (album, id) in new[] { (makingMovies, 5), (alchemy, 1) })
        {
            (album.MatchLocked, album.ArrId, album.MetadataSource) = (true, id, "Lidarr");
        }

        await _db.SaveChangesAsync();
        _lidarr.Responses["/api/v1/album/1"] = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine("Fixtures", "lidarr-album.json")))![0]!.ToJsonString();

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        _db.ChangeTracker.Clear();
        var titles = await _db.MediaItems.Where(i => i.Id == makingMovies.Id || i.Id == alchemy.Id).ToDictionaryAsync(i => i.Id);
        Assert.Equal(("Dire Straits", 5, "Making movies"), (titles[makingMovies.Id].Title, titles[makingMovies.Id].ArrId, titles[makingMovies.Id].OriginalTitle));
        Assert.Equal(("On Every Street", 1, "Alchemy", "Lidarr"), (titles[alchemy.Id].Title, titles[alchemy.Id].ArrId, titles[alchemy.Id].OriginalTitle, titles[alchemy.Id].MetadataSource));
    }

    [Fact]
    public async Task AnAlbumMatchedOnDeezer_KeepsWhatItWasGiven_WithOrWithoutLidarr()
    {
        var alchemy = await _db.MediaItems.SingleAsync(i => i.Title == "Alchemy");
        await Refresher().ApplyChosenAlbumAsync(alchemy, new DeezerAlbum(302127, "Alchemy (Live)", CoverXl: "https://cdn-images.dzcdn.net/images/cover/abc/1000x1000-000000-80-0-0.jpg",
            ReleaseDate: "1984-03-16", RecordType: "album", Genres: new DeezerList<DeezerGenre>([new DeezerGenre(152, "Rock")])), default);
        alchemy.MatchLocked = true;
        await _db.SaveChangesAsync();

        foreach (var lidarr in new[] { true, false })
        {
            await Refresher(lidarr).RefreshLibraryAsync(_library, force: true, default);
            _db.ChangeTracker.Clear();
            var album = await _db.MediaItems.Include(i => i.Genres).SingleAsync(i => i.Id == alchemy.Id);
            Assert.Equal(("Alchemy (Live)", "Alchemy", 1984, AlbumKind.Album, "302127", MetadataRefresher.Deezer),
                (album.Title, album.OriginalTitle, album.Year, album.AlbumType, album.DeezerId, album.MetadataSource));
            Assert.Equal("https://cdn-images.dzcdn.net/images/cover/abc/1000x1000-000000-80-0-0.jpg", album.PosterUrl);
            Assert.Equal(["Rock"], album.Genres.Select(g => g.Name));
        }
    }

    [Fact]
    public async Task AnAlbumEditedByHand_KeepsItsDetailsAndChosenCover_AndStillGetsTheRest()
    {
        var makingMovies = await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder);
        (makingMovies.Title, makingMovies.OriginalTitle, makingMovies.Year, makingMovies.DetailsLocked) = ("Making Movies (Remastered)", "Making movies", 1996, true);
        (makingMovies.PosterUrl, makingMovies.ArtworkLocked) = ($"file://{MakingMoviesFolder}/front.jpg", true);
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        _db.ChangeTracker.Clear();
        var album = await _db.MediaItems.SingleAsync(i => i.Id == makingMovies.Id);
        Assert.Equal(("Making Movies (Remastered)", "Making movies", 1996), (album.Title, album.OriginalTitle, album.Year));
        Assert.Equal($"file://{MakingMoviesFolder}/front.jpg", album.PosterUrl);
        Assert.Equal(("5b0a15ce-8aa9-323b-8fb6-02b657756ba9", 3, 38), (album.MusicBrainzId, album.ArrId, album.RuntimeMinutes));
    }

    [Fact]
    public async Task AnArtistMatchedOnDeezer_IsntPairedWithLidarrsArtist()
    {
        (_artist.MatchLocked, _artist.DeezerId, _artist.MetadataSource) = (true, "176", MetadataRefresher.Deezer);
        await _db.SaveChangesAsync();

        await Refresher().RefreshLibraryAsync(_library, force: true, default);

        _db.ChangeTracker.Clear();
        var artist = await _db.MediaItems.SingleAsync(i => i.Kind == MediaKind.Artist);
        Assert.Equal(("Dire straits", null, MetadataRefresher.Deezer), (artist.Title, artist.ArrId, artist.MetadataSource));
        Assert.Equal("Making movies", (await _db.MediaItems.SingleAsync(i => i.Path == MakingMoviesFolder)).Title);
    }

    [Fact]
    public void MatchAutomaticallyAgain_HandsAnAlbumBackToItsTags()
    {
        var album = new MediaItem
        {
            Kind = MediaKind.Album, Title = "Alchemy (Live)", SortTitle = "alchemy live", OriginalTitle = "Alchemy", MatchLocked = true, ArtworkLocked = true,
            DeezerId = "302127", MetadataSource = MetadataRefresher.Deezer, PosterUrl = "https://cdn-images.dzcdn.net/images/cover/abc/1000x1000.jpg",
        };

        MetadataRefresher.ForgetMusicMatch(album, "file:///music/Alchemy/cover.jpg");

        Assert.Equal(("Alchemy", null, false, false, null, MetadataRefresher.Tags, "file:///music/Alchemy/cover.jpg"),
            (album.Title, album.OriginalTitle, album.MatchLocked, album.ArtworkLocked, album.DeezerId, album.MetadataSource, album.PosterUrl));
    }

    private MediaItem Album(string title, int year, string folder, string? poster) => new()
    {
        LibraryId = _library.Id,
        ParentId = _artist.Id,
        Kind = MediaKind.Album,
        Title = title,
        SortTitle = title.ToLowerInvariant(),
        Year = year,
        Path = $"{Root}/Dire Straits/{folder}",
        PosterUrl = poster,
    };

    private MetadataRefresher Refresher(bool lidarr = true) => Refresher(null, lidarr);

    private MetadataRefresher Refresher(LidarrArtistList? list, bool lidarr = true)
    {
        var settings = new Settings(_dir, lidarr);
        var http = new HttpClient(_lidarr);
        return new MetadataRefresher(_db, new ArrClient(http), new TvMazeClient(http),
            new ImageCache(http, settings, NullLogger<ImageCache>.Instance), settings, NullLogger<MetadataRefresher>.Instance, artistList: list);
    }

    private sealed class Settings(string cache, bool lidarr) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = lidarr
            ? [new ArrInstance { Name = "Lidarr", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr.test"), ApiKey = "key" }]
            : [];

        public IReadOnlyList<PathMapping> PathMappings { get; } = [new PathMapping { From = "/data", To = "/Volumes/Media/data" }];
        public string CacheDirectory => cache;
        public string FfmpegPath => "false"; // no resizing: artwork answers are not images here anyway
    }

    /// <summary>Lidarr with the fixtures in it, answering by path and query; artwork requests get a 404.</summary>
    private sealed class FakeLidarr : HttpMessageHandler
    {
        public bool TimeOut;
        public Dictionary<string, string> Responses { get; } = [];
        public List<string> Paths { get; } = [];
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Paths)
            {
                Paths.Add(request.RequestUri!.PathAndQuery);
                Requests.Add(request);
            }

            if (TimeOut)
            {
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 100 seconds elapsing.");
            }

            return Task.FromResult(Responses.TryGetValue(request.RequestUri!.PathAndQuery, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
