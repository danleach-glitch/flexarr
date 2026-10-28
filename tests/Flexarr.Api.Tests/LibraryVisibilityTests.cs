using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Flexarr.Api.Tests;

public class LibraryVisibilityTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private HttpClient Client(string? profileToken = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Fact]
    public async Task HiddenLibrary_DisappearsForThatProfileOnly()
    {
        var (films, shows, film, show) = await SeedAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Sam", hiddenLibraryIds = new[] { films } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var sam = Client(token);

        var samLibraries = await sam.GetFromJsonAsync<JsonElement>(Api("/libraries"));
        var mainLibraries = await Client().GetFromJsonAsync<JsonElement>(Api("/libraries"));
        var samSearch = await sam.GetFromJsonAsync<JsonElement>(Api("/search?q=Visibility"));
        var mainSearch = await Client().GetFromJsonAsync<JsonElement>(Api("/search?q=Visibility"));
        var home = await sam.GetFromJsonAsync<JsonElement>(Api("/home"));

        Assert.DoesNotContain(samLibraries.EnumerateArray(), l => l.GetProperty("id").GetGuid() == films);
        Assert.Contains(samLibraries.EnumerateArray(), l => l.GetProperty("id").GetGuid() == shows);
        Assert.Contains(mainLibraries.EnumerateArray(), l => l.GetProperty("id").GetGuid() == films);
        var samTitles = samSearch.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(show, samTitles);
        Assert.DoesNotContain(film, samTitles);
        Assert.Contains(mainSearch.GetProperty("titles").EnumerateArray(), t => t.GetProperty("id").GetGuid() == film);
        Assert.DoesNotContain(film.ToString(), home.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync(Api($"/items/{film}"))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await sam.GetAsync(Api($"/libraries/{films}"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync(Api($"/items/{film}"))).StatusCode);
    }

    [Fact]
    public async Task Settings_SeesEveryLibrary_EvenOnesTheMainProfileHides()
    {
        var (films, _, _, _) = await SeedAsync();
        await Client().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Main profile", hiddenLibraryIds = new[] { films } });

        var browsing = await Client().GetFromJsonAsync<JsonElement>(Api("/libraries"));
        var settings = await Client().GetFromJsonAsync<JsonElement>(Api("/libraries?all=true"));
        await Client().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Main profile", hiddenLibraryIds = Array.Empty<Guid>() });

        Assert.DoesNotContain(browsing.EnumerateArray(), l => l.GetProperty("id").GetGuid() == films);
        Assert.Contains(settings.EnumerateArray(), l => l.GetProperty("id").GetGuid() == films);
    }

    [Fact]
    public async Task AnAssistantOverMcp_DoesntFindTitlesInHiddenLibraries()
    {
        var (films, _, film, show) = await SeedAsync();
        await Client().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Main profile", hiddenLibraryIds = new[] { films } });
        try
        {
            var http = factory.CreateClient();
            var options = new HttpClientTransportOptions
            {
                Endpoint = new Uri(http.BaseAddress!, "/api/v1/mcp"),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = new Dictionary<string, string> { ["X-Api-Key"] = FlexarrApiFactory.ApiKey },
            };
            await using var mcp = await McpClient.CreateAsync(new HttpClientTransport(options, http, ownsHttpClient: true));

            var found = await mcp.CallToolAsync("search_library", new Dictionary<string, object?> { ["query"] = "Visibility" });
            Assert.NotEqual(true, found.IsError);
            var text = found.StructuredContent?.GetRawText() ?? string.Concat(found.Content.OfType<TextContentBlock>().Select(b => b.Text));

            Assert.Contains(show.ToString(), text, StringComparison.Ordinal);
            Assert.DoesNotContain(film.ToString(), text, StringComparison.Ordinal);
        }
        finally
        {
            await Client().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Main profile", hiddenLibraryIds = Array.Empty<Guid>() });
        }
    }

    [Fact]
    public async Task ProfileKeptOutOfEveryFilmLibrary_CantRequestFilms_ButHidingOneTvLibraryOfTwoStillAllowsShows()
    {
        await SeedAsync();
        await SeedAsync(); // two film and two TV libraries
        var libraries = await Client().GetFromJsonAsync<JsonElement>(Api("/libraries?all=true"));
        var allFilms = libraries.EnumerateArray().Where(l => l.GetProperty("type").GetString() == "Movies").Select(l => l.GetProperty("id").GetGuid()).ToArray();
        var oneTv = libraries.EnumerateArray().First(l => l.GetProperty("type").GetString() == "TvShows").GetProperty("id").GetGuid();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Kid", hiddenLibraryIds = allFilms.Append(oneTv).ToArray() }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        // Both are answered before Seerr is consulted (none is set up here), so no real request is ever made.
        var film = await Client(token).PostAsJsonAsync(Api("/requests"), new { mediaType = "movie", tmdbId = 603 });
        var show = await Client(token).PostAsJsonAsync(Api("/requests"), new { mediaType = "tv", tmdbId = 1396 });

        Assert.Equal(HttpStatusCode.Forbidden, film.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, show.StatusCode); // allowed; it only fails for want of a Seerr server
    }

    private async Task<(Guid Films, Guid Shows, Guid Film, Guid Show)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var shows = new Library { Name = $"Shows {Guid.NewGuid():N}", Type = LibraryType.TvShows };
        var film = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Visibility Film", SortTitle = "visibility film" };
        var show = new MediaItem { LibraryId = shows.Id, Kind = MediaKind.Series, Title = "Visibility Show", SortTitle = "visibility show" };
        db.AddRange(films, shows, film, show);
        await db.SaveChangesAsync();
        return (films.Id, shows.Id, film.Id, show.Id);
    }
}
