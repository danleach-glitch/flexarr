using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

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
        Assert.Equal([show], samSearch.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("id").GetGuid()));
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
    public async Task ProfileKeptOutOfFilms_CantRequestFilms()
    {
        var (films, _, _, _) = await SeedAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Kid", hiddenLibraryIds = new[] { films } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        // Rejected before Seerr is consulted, so no real request is ever made.
        var response = await Client(token).PostAsJsonAsync(Api("/requests"), new { mediaType = "movie", tmdbId = 603 });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
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
