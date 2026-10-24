using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class CollectionTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

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

    [Fact]
    public async Task AManualCollection_KeepsItsOrder_AndCanBeReordered()
    {
        var (library, films) = await SeedAsync(("Alpha", 2001, 6.0), ("Bravo", 2002, 7.0), ("Charlie", 2003, 8.0));
        var created = await (await Client().PostAsJsonAsync(Api("/collections"), new { name = "Favourites", personal = false })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        foreach (var film in films.Reverse())
        {
            (await Client().PostAsJsonAsync(Api($"/collections/{id}/items"), new { itemId = film })).EnsureSuccessStatusCode();
        }

        var added = await Titles(id);
        await Client().PutAsJsonAsync(Api($"/collections/{id}/order"), new { itemIds = films });
        var reordered = await Titles(id);
        await Client().DeleteAsync(Api($"/collections/{id}/items/{films[1]}"));
        var afterRemove = await Titles(id);

        Assert.Equal(["Charlie", "Bravo", "Alpha"], added);
        Assert.Equal(["Alpha", "Bravo", "Charlie"], reordered);
        Assert.Equal(["Alpha", "Charlie"], afterRemove);
        Assert.Contains("Favourites", (await Client().GetFromJsonAsync<JsonElement>(Api($"/items/{films[0]}/collections"))).EnumerateArray()
            .Where(c => c.GetProperty("contains").GetBoolean()).Select(c => c.GetProperty("name").GetString()));
        _ = library;
    }

    [Fact]
    public async Task ASmartCollection_AppliesItsRules()
    {
        var (library, _) = await SeedAsync(("Old Classic", 1985, 9.0), ("Good New", 2015, 8.1), ("Great New", 2018, 8.7), ("Weak New", 2016, 5.0));
        var created = await (await Client().PostAsJsonAsync(Api("/collections"), new
        {
            name = "Best since 2000",
            rules = new { libraryIds = new[] { library }, type = "movie", yearFrom = 2000, minRating = 7.0, limit = 5 },
        })).Content.ReadFromJsonAsync<JsonElement>();

        var titles = await Titles(created.GetProperty("id").GetString());

        Assert.Equal("Smart", created.GetProperty("kind").GetString());
        Assert.Equal(["Great New", "Good New"], titles); // best first; 1985 and the weak one left out
    }

    [Fact]
    public async Task TitlesInAHiddenLibrary_DropOutOfCollections_ForThatProfile()
    {
        var (library, films) = await SeedAsync(("Hidden Film", 2010, 7.0));
        var created = await (await Client().PostAsJsonAsync(Api("/collections"), new { name = $"Shared {Guid.NewGuid():N}", personal = false })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        await Client().PostAsJsonAsync(Api($"/collections/{id}/items"), new { itemId = films[0] });
        var profile = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Kid", hiddenLibraryIds = new[] { library } })).Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await Client().PostAsJsonAsync(Api($"/profiles/{profile.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();

        var forKid = await Client(token).GetFromJsonAsync<JsonElement>(Api($"/collections/{id}"));

        Assert.Empty(forKid.GetProperty("items").EnumerateArray());
        Assert.Single((await Client().GetFromJsonAsync<JsonElement>(Api($"/collections/{id}"))).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task TheCollectionList_CountsAndPostersMatchTheCollectionItself()
    {
        var (library, films) = await SeedAsync(("Pa", 2001, 6.0), ("Pb", 2002, 6.5), ("Pc", 2003, 7.0), ("Pd", 2004, 7.5), ("Pe", 2005, 8.0), ("Pf", 2006, 8.5));
        Guid show;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var items = db.MediaItems.Where(i => films.Contains(i.Id)).ToList();
            foreach (var item in items.Where(i => i.Title != "Pb"))
            {
                item.PosterUrl = $"https://image.tmdb.org/t/p/original/{item.Title}.jpg";
            }

            // A show counts as watched once every episode is.
            var series = new MediaItem { LibraryId = library, Kind = MediaKind.Series, Title = "Px Show", SortTitle = "px show", CommunityRating = 9.0, PosterUrl = "https://image.tmdb.org/t/p/original/px.jpg" };
            var season = new MediaItem { LibraryId = library, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "0001", SeasonNumber = 1, Parent = series };
            var episode = new MediaItem { LibraryId = library, Kind = MediaKind.Episode, Title = "One", SortTitle = "0001", Parent = season };
            db.AddRange(series, season, episode);
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = episode.Id, Watched = true, PlayCount = 1 });
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = films[0], Watched = true, PlayCount = 1 });
            db.PlaybackStates.Add(new PlaybackState { UserId = PlaybackState.DefaultUser, MediaItemId = films[2], Watched = false, PositionSeconds = 60 });
            await db.SaveChangesAsync();
            show = series.Id;
        }

        var manual = await (await Client().PostAsJsonAsync(Api("/collections"), new { name = $"Mixed {Guid.NewGuid():N}", personal = false })).Content.ReadFromJsonAsync<JsonElement>();
        var manualId = manual.GetProperty("id").GetString();
        foreach (var id in films.Append(show))
        {
            (await Client().PostAsJsonAsync(Api($"/collections/{manualId}/items"), new { itemId = id })).EnsureSuccessStatusCode();
        }

        var smart = await (await Client().PostAsJsonAsync(Api("/collections"), new
        {
            name = $"Smart {Guid.NewGuid():N}",
            rules = new { libraryIds = new[] { library }, minRating = 6.2, limit = 4 },
        })).Content.ReadFromJsonAsync<JsonElement>();

        var list = (await Client().GetFromJsonAsync<JsonElement>(Api("/collections"))).EnumerateArray().ToList();
        foreach (var id in new[] { manualId, smart.GetProperty("id").GetString() })
        {
            var listed = list.Single(c => c.GetProperty("id").GetString() == id);
            var own = (await Client().GetFromJsonAsync<JsonElement>(Api($"/collections/{id}"))).GetProperty("summary");
            Assert.Equal(own.GetProperty("itemCount").GetInt32(), listed.GetProperty("itemCount").GetInt32());
            Assert.Equal(own.GetProperty("watchedCount").GetInt32(), listed.GetProperty("watchedCount").GetInt32());
            Assert.Equal(own.GetProperty("posters").EnumerateArray().Select(p => p.GetString()), listed.GetProperty("posters").EnumerateArray().Select(p => p.GetString()));
        }

        var mixed = list.Single(c => c.GetProperty("id").GetString() == manualId);
        Assert.Equal((7, 2, 4), (mixed.GetProperty("itemCount").GetInt32(), mixed.GetProperty("watchedCount").GetInt32(), mixed.GetProperty("posters").GetArrayLength()));
        Assert.DoesNotContain(mixed.GetProperty("posters").EnumerateArray(), p => p.GetString()!.Contains(films[1].ToString(), StringComparison.Ordinal));
    }

    private async Task<string[]> Titles(string? id) =>
        (await Client().GetFromJsonAsync<JsonElement>(Api($"/collections/{id}"))).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!).ToArray();

    private async Task<(Guid Library, Guid[] Films)> SeedAsync(params (string Title, int Year, double Rating)[] films)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        var items = films.Select(f => new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = f.Title, SortTitle = f.Title.ToLowerInvariant(), Year = f.Year, CommunityRating = f.Rating }).ToArray();
        db.Add(library);
        db.AddRange(items);
        await db.SaveChangesAsync();
        return (library.Id, items.Select(i => i.Id).ToArray());
    }
}
