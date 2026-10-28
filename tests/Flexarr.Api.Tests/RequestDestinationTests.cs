using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class RequestDestinationTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly PathMapping[] Docker = [new() { From = "/data", To = "/Volumes/Media/data" }];

    private static Library Library(string name, LibraryType type, params string[] folders) =>
        new() { Name = name, Type = type, Folders = folders.Select(f => new LibraryFolder { Path = f }).ToList() };

    private static SeerrServer Sonarr(int id, bool isDefault, string active, params string[] roots) =>
        new(MediaKind.Series, new SeerrService(id, "sonarr", false, isDefault, active), roots.Select(r => new SeerrRootFolder(r)).ToList());

    [Fact]
    public void TwoTvLibraries_InOneSonarrsTwoRootFolders_AreBothDestinations_AndTheActiveOneIsTheDefault()
    {
        var dans = Library("TV Shows", LibraryType.TvShows, "/Volumes/Media/data/tv");
        var claires = Library("Claire’s Shows", LibraryType.TvShows, "/Volumes/Media/data/claire-shows");
        var films = Library("Movies", LibraryType.Movies, "/Volumes/Media/data/movies");
        var radarr = new SeerrServer(MediaKind.Movie, new SeerrService(0, "radarr", false, true, "/data/movies"), [new SeerrRootFolder("/data/movies")]);

        var destinations = RequestDestinations.Match([Sonarr(0, true, "/data/tv", "/data/tv/", "/data/claire-shows"), radarr], Docker, [dans, claires, films]);

        Assert.Collection(destinations,
            d => Assert.Equal((dans.Id, MediaKind.Series, true, "/data/tv/"), (d.LibraryId, d.Kind, d.IsDefault, d.To.RootFolder)),
            d => Assert.Equal((claires.Id, MediaKind.Series, false, "/data/claire-shows"), (d.LibraryId, d.Kind, d.IsDefault, d.To.RootFolder)),
            d => Assert.Equal((films.Id, MediaKind.Movie, true), (d.LibraryId, d.Kind, d.IsDefault)));
    }

    [Fact]
    public void LibrariesSeerrCantAddTo_AndRecordings_AreLeftOut()
    {
        var elsewhere = Library("Old TV", LibraryType.TvShows, "/Volumes/Other/tv");
        var recordings = Library(RecordingsLibrary.Name, LibraryType.TvShows, "/Volumes/Media/data/tv/DVR");
        var films = Library("Movies", LibraryType.Movies, "/Volumes/Media/data/tv"); // a film library can't take Sonarr's folder

        Assert.Empty(RequestDestinations.Match([Sonarr(0, true, "/data/tv", "/data/tv", "/data/tv/DVR")], Docker, [elsewhere, recordings, films]));
    }

    [Fact]
    public void AFolderTwoSonarrsShare_GoesToTheDefaultOne()
    {
        var shows = Library("TV Shows", LibraryType.TvShows, "/Volumes/Media/data/tv");

        var destination = Assert.Single(RequestDestinations.Match([Sonarr(3, false, "/data/tv", "/data/tv"), Sonarr(1, true, "/data/anime", "/data/tv")], Docker, [shows]));

        Assert.Equal(1, destination.To.ServerId);
        Assert.False(destination.IsDefault); // that server adds to /data/anime unless told otherwise
    }

    [Fact]
    public async Task AProfilesRequestLibraries_AreSavedAndShown_AndMustBeTheRightKind()
    {
        var shows = await AddLibraryAsync("Requests TV", LibraryType.TvShows);
        var films = await AddLibraryAsync("Requests Films", LibraryType.Movies);

        var wrong = await Client().PutAsJsonAsync(Api("/profiles/owner/request-libraries"), new { showsLibraryId = films, filmsLibraryId = shows });
        var saved = await Client().PutAsJsonAsync(Api("/profiles/owner/request-libraries"), new { showsLibraryId = shows, filmsLibraryId = (Guid?)null });
        var owner = (await Client().GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().First(p => p.GetProperty("id").GetString() == "owner");
        await Client().PutAsJsonAsync(Api("/profiles/owner/request-libraries"), new { showsLibraryId = (Guid?)null, filmsLibraryId = (Guid?)null });

        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal(shows, owner.GetProperty("requestShowsLibraryId").GetGuid());
        Assert.Equal(JsonValueKind.Null, owner.GetProperty("requestFilmsLibraryId").ValueKind);
    }

    [Fact]
    public async Task WithoutSeerr_ThereAreNoDestinations()
    {
        var destinations = await Client().GetFromJsonAsync<JsonElement>(Api("/requests/destinations"));

        Assert.Equal(0, destinations.GetArrayLength());
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private async Task<Guid> AddLibraryAsync(string name, LibraryType type)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"{name} {Guid.NewGuid():N}", Type = type };
        db.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }
}
