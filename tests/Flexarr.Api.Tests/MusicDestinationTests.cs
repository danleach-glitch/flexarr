using Flexarr.Api.Endpoints.MusicRequests;
using Flexarr.Core.Libraries;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Api.Tests;

public class MusicDestinationTests
{
    private static readonly PathMapping[] Docker = [new() { From = "/data", To = "/Volumes/Media/data" }];

    private static readonly ArrInstance Lidarr = new() { Name = "Lidarr", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr.test"), ApiKey = "k" };
    private static readonly ArrInstance Second = new() { Name = "Lidarr 2", Kind = ArrKind.Lidarr, BaseUrl = new Uri("http://lidarr2.test"), ApiKey = "k" };

    private static Library Library(string name, LibraryType type, params string[] folders) =>
        new() { Name = name, Type = type, Folders = folders.Select(f => new LibraryFolder { Path = f }).ToList() };

    [Fact]
    public void MusicLibraries_MatchLidarrsRootFolders_ThroughThePathMappings()
    {
        var music = Library("Music", LibraryType.Music, "/Volumes/Media/data/music");
        var kids = Library("Kids music", LibraryType.Music, "/Volumes/Media/data/kids-music");
        var films = Library("Movies", LibraryType.Movies, "/Volumes/Media/data/music");
        LidarrRoots[] roots = [new(Lidarr, [new LidarrRootFolder(1, "/data/music/", 1, 1)]), new(Second, [new LidarrRootFolder(4, "/data/kids-music", 2, 3)])];

        var destinations = MusicDestinations.Match(roots, Docker, [music, kids, films]);

        Assert.Collection(destinations,
            d => Assert.Equal((music.Id, "Lidarr", 1, true), (d.LibraryId, d.Lidarr.Name, d.Root.Id, d.IsDefault)),
            d => Assert.Equal((kids.Id, "Lidarr 2", 4, false), (d.LibraryId, d.Lidarr.Name, d.Root.Id, d.IsDefault)));
    }

    [Fact]
    public void AMusicLibraryOutsideLidarr_IsNoDestination()
    {
        var elsewhere = Library("Vinyl rips", LibraryType.Music, "/Volumes/Other/music");

        Assert.Empty(MusicDestinations.Match([new(Lidarr, [new LidarrRootFolder(1, "/data/music", 1, 1)])], Docker, [elsewhere]));
    }
}
