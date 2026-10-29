using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class LibraryOrderTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public void LibrariesFollowTheOrder_AndOnesItDoesntMentionComeAfter_InTheirOwnOrder()
    {
        var a = new Library { Name = "A", Type = LibraryType.Movies };
        var b = new Library { Name = "B", Type = LibraryType.TvShows };
        var c = new Library { Name = "C", Type = LibraryType.Music };
        var d = new Library { Name = "D", Type = LibraryType.Movies };

        var ordered = LibraryEndpoints.InOrder([a, b, c, d], [c.Id.ToString(), "live-tv", Guid.NewGuid().ToString(), a.Id.ToString().ToUpperInvariant()]);

        Assert.Equal(["C", "A", "B", "D"], ordered.Select(l => l.Name));
    }

    [Fact]
    public async Task AProfilesOrder_IsSaved_AndTheLibraryListFollowsIt()
    {
        var first = await AddLibraryAsync("Order A", LibraryType.Movies);
        var second = await AddLibraryAsync("Order B", LibraryType.TvShows);

        var saved = await Client().PutAsJsonAsync(Api("/profiles/owner/library-order"), new { order = new[] { second.ToString(), "live-tv", first.ToString() } });
        var owner = (await Client().GetFromJsonAsync<JsonElement>(Api("/profiles"))).EnumerateArray().First(p => p.GetProperty("id").GetString() == "owner");
        var ids = (await Client().GetFromJsonAsync<JsonElement>(Api("/libraries"))).EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();
        await Client().PutAsJsonAsync(Api("/profiles/owner/library-order"), new { order = Array.Empty<string>() });
        var byName = (await Client().GetFromJsonAsync<JsonElement>(Api("/libraries"))).EnumerateArray().Select(l => l.GetProperty("id").GetGuid()).ToList();

        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Equal([second.ToString(), "live-tv", first.ToString()], owner.GetProperty("libraryOrder").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal([second, first], ids.Take(2));
        Assert.True(byName.IndexOf(first) < byName.IndexOf(second));
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
        var library = new Library { Name = name, Type = type };
        db.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    }
}
