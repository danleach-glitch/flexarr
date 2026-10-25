using System.Text.Json;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

/// <summary>
/// Against Lidarr's answers as it gives them (trimmed): /api/v1/artist, /api/v1/album?artistId=1 and
/// /api/v1/trackfile?artistId=1 for a Dire Straits collection, with Lidarr in Docker seeing /data where Flexarr sees
/// /Volumes/Media/data.
/// </summary>
public sealed class LidarrMatcherTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly IReadOnlyList<PathMapping> Mappings = [new PathMapping { From = "/data", To = "/Volumes/Media/data" }];

    private static readonly IReadOnlyList<LidarrArtist> Artists = Load<LidarrArtist>("lidarr-artist.json");
    private static readonly IReadOnlyList<LidarrAlbum> Albums = Load<LidarrAlbum>("lidarr-album.json");
    private static readonly IReadOnlyList<LidarrTrackFile> Files = Load<LidarrTrackFile>("lidarr-trackfile.json");

    private const string Root = "/Volumes/Media/data/music";

    [Fact]
    public void Artist_ByMusicBrainzId_BeforeAnythingElse()
    {
        var artist = Artist("Someone Else", mbid: "6c4fb5a6-cc8a-4dd3-9a0a-1a9e7a1f2c33");

        var hit = LidarrMatcher.MatchArtist(artist, [$"{Root}/Dire Straits/Communiqué (1979)"], Artists, Mappings);

        Assert.Equal("Mark Knopfler", hit?.ArtistName);
    }

    [Fact]
    public void Artist_ByTheFolderItsAlbumsAreIn_WhateverTheTagsCallIt()
    {
        var artist = Artist("dire straits ");

        var hit = LidarrMatcher.MatchArtist(artist, [$"{Root}/Dire Straits/Communiqué (1979)", null], Artists, Mappings);

        Assert.Equal(1, hit?.Id);
    }

    [Fact]
    public void Artist_FolderMatch_IsWholeSegments_NotAPrefix()
    {
        // "Dire Wolves" isn't inside "Dire Straits"' folder, and "Dire Straits Tribute" isn't "Dire Straits".
        var artist = Artist("Unknown artist");

        var hit = LidarrMatcher.MatchArtist(artist, [$"{Root}/Dire Straits Tribute/Live (2001)"], Artists, Mappings);

        Assert.Null(hit);
    }

    [Fact]
    public void Artist_ByName_WhenNoFolderMatches()
    {
        var artist = Artist("MARK KNOPFLER");

        var hit = LidarrMatcher.MatchArtist(artist, ["/somewhere/else/Golden Heart"], Artists, Mappings);

        Assert.Equal(3, hit?.Id);
        Assert.Null(LidarrMatcher.MatchArtist(Artist("Mark Knopfler & Friends"), [], Artists, Mappings));
    }

    [Fact]
    public void Album_ByTheFolderLidarrsFilesAreIn_CorrectsTitleAndYearFromTags()
    {
        // The tags say "Communiqué CD" and "Making movies" (1996, a remaster): the folders say which albums they are.
        var communique = Album("Communiqué CD", 1979, $"{Root}/Dire Straits/Communiqué (1979)");
        var makingMovies = Album("Making movies", 1996, $"{Root}/Dire Straits/Making Movies (1980)");

        Assert.Equal("Communiqué", LidarrMatcher.MatchAlbum(communique, Albums, Files, Mappings)?.Title);
        var hit = LidarrMatcher.MatchAlbum(makingMovies, Albums, Files, Mappings);
        Assert.Equal("Making Movies", hit?.Title);
        Assert.Equal(1980, hit?.ReleaseDate?.Year);
    }

    [Fact]
    public void Album_FilesInDiscFolders_StillPlaceTheAlbum()
    {
        var album = Album("On Every Street", null, $"{Root}/Dire Straits/On Every Street (1991)");

        Assert.Equal(1, LidarrMatcher.MatchAlbum(album, Albums, Files, Mappings)?.Id);
    }

    [Fact]
    public void Album_ByMusicBrainzReleaseId_OrReleaseGroupId()
    {
        // Tags carry the release id (one pressing); Lidarr's foreignAlbumId is the release group's.
        var byRelease = Album("Whatever", null, "/elsewhere/a", mbid: "3a40201d-e7ba-3121-98cb-e2d57fa71736");
        var byGroup = Album("Whatever", null, "/elsewhere/b", mbid: "935DDE7E-8390-39FA-8E0D-1350DB96BC3F");

        Assert.Equal("Communiqué", LidarrMatcher.MatchAlbum(byRelease, Albums, Files, Mappings)?.Title);
        Assert.Equal("Love Over Gold", LidarrMatcher.MatchAlbum(byGroup, Albums, Files, Mappings)?.Title);
    }

    [Fact]
    public void Album_ByTitle_NearestYearWins_AndNothingElseIsGuessed()
    {
        // Two "Brothers in Arms" (the studio album and a live one): the year picks.
        var studio = Album("brothers in arms", 1985, "/elsewhere/Brothers in Arms");
        var live = Album("Brothers In Arms", 2005, "/elsewhere/Brothers in Arms Live");
        var unknownYear = Album("Brothers in Arms", null, "/elsewhere/bia");
        var loose = Album("Love over gold!", 1982, "/elsewhere/log");

        Assert.Equal(6, LidarrMatcher.MatchAlbum(studio, Albums, Files, Mappings)?.Id);
        Assert.Equal(7, LidarrMatcher.MatchAlbum(live, Albums, Files, Mappings)?.Id);
        Assert.Contains(LidarrMatcher.MatchAlbum(unknownYear, Albums, Files, Mappings)?.Id, new int?[] { 6, 7 });
        Assert.Equal(4, LidarrMatcher.MatchAlbum(loose, Albums, Files, Mappings)?.Id);
        Assert.Null(LidarrMatcher.MatchAlbum(Album("Alchemy", 1984, "/elsewhere/Alchemy"), Albums, Files, Mappings));
    }

    [Fact]
    public void Fixtures_DeserialiseAsTheClientWould()
    {
        Assert.Equal(3, Artists.Count);
        Assert.Equal("/data/music/Dire Straits", Artists[0].Path);
        Assert.Equal(["banner", "poster", "fanart", "clearlogo"], Artists[0].Images.Select(i => i.CoverType));
        Assert.Equal(7, Albums.Count);
        Assert.Equal(2, Albums[1].Releases.Count);
        Assert.Equal(3617625, Albums[0].Duration);
        Assert.Equal(5, Files.Count);
    }

    private static MediaItem Artist(string title, string? mbid = null) =>
        new() { Kind = MediaKind.Artist, Title = title, SortTitle = title, MusicBrainzId = mbid };

    private static MediaItem Album(string title, int? year, string path, string? mbid = null) =>
        new() { Kind = MediaKind.Album, Title = title, SortTitle = title, Year = year, Path = path, MusicBrainzId = mbid };

    private static IReadOnlyList<T> Load<T>(string file) =>
        JsonSerializer.Deserialize<IReadOnlyList<T>>(File.ReadAllText(Path.Combine("Fixtures", file)), Json)!;
}
