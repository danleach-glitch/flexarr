using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Home is kept a minute per profile, and dropped by any write: what comes back is always what working it out afresh gives.</summary>
public sealed class HomeCacheTests : IDisposable
{
    private readonly FlexarrApiFactory factory = new();

    public void Dispose() => factory.Dispose();

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private MemoryCache Cache => (MemoryCache)factory.Services.GetRequiredService<IMemoryCache>();

    private List<string> HomeKeys() => [.. Cache.Keys.OfType<string>().Where(k => k.StartsWith("home|", StringComparison.Ordinal))];

    private async Task<string> HomeAsync() => await Client().GetStringAsync(Api("/home"));

    /// <summary>Home worked out afresh, as it would be with nothing kept.</summary>
    private async Task<string> FreshAsync()
    {
        foreach (var key in HomeKeys())
        {
            Cache.Remove(key);
        }

        return await HomeAsync();
    }

    private async Task<(Guid Library, List<Guid> Films)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = "Films", Type = LibraryType.Movies };
        db.Libraries.Add(library);
        var films = Enumerable.Range(1, 5).Select(i => new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Movie, Title = $"Film {i}", SortTitle = $"film {i}", TmdbId = (1000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            AddedAt = DateTime.UtcNow.AddDays(-i),
        }).ToList();
        db.MediaItems.AddRange(films);
        await db.SaveChangesAsync();
        return (library.Id, films.Select(f => f.Id).ToList());
    }

    private static List<Guid> Row(string home, string id) =>
        JsonDocument.Parse(home).RootElement.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("id").GetString() == id)
            .SelectMany(r => r.GetProperty("items").EnumerateArray()).Select(i => i.GetProperty("id").GetGuid()).ToList();

    [Fact]
    public async Task OpeningHomeAgain_IsAnsweredFromWhatWasKept_UntilSomethingIsWritten()
    {
        var (_, films) = await SeedAsync();
        var first = await HomeAsync();
        Assert.Equal(films, Row(first, "recent-movies"));
        var key = Assert.Single(HomeKeys());

        // Something only the kept copy holds shows the second answer came from it.
        var entry = Assert.IsType<HomeCache.Entry>(Cache.Get(key));
        Cache.Set(key, entry with { Home = entry.Home with { Rows = [] } }, HomeCache.KeptFor);
        Assert.Empty(Row(await HomeAsync(), "recent-movies"));

        // Any write drops it.
        (await Client().PostAsJsonAsync(Api($"/items/{films[0]}/watched"), new { watched = true })).EnsureSuccessStatusCode();
        var after = await HomeAsync();
        Assert.Equal(films.Skip(1), Row(after, "recent-movies"));
        Assert.Equal(await FreshAsync(), after);
    }

    [Fact]
    public async Task AfterEachKindOfChange_TheAnswerIsTheSameAsWorkingItOutAfresh()
    {
        var (library, films) = await SeedAsync();
        var client = Client();
        var changes = new (string What, Func<Task> Change)[]
        {
            ("marked watched", async () => (await client.PostAsJsonAsync(Api($"/items/{films[1]}/watched"), new { watched = true })).EnsureSuccessStatusCode()),
            ("progress", async () => (await client.PostAsJsonAsync(Api($"/items/{films[2]}/offline-progress"),
                new { positionSeconds = 1200, finished = false, playedAt = DateTime.UtcNow })).EnsureSuccessStatusCode()),
            ("watchlist", async () => (await client.PostAsJsonAsync(Api("/watchlist"), new { libraryItemId = films[3] })).EnsureSuccessStatusCode()),
            ("layout", async () => (await client.PutAsJsonAsync(Api("/settings/layout"),
                new { home = new[] { new { id = "continue", enabled = false } }, movies = Array.Empty<object>(), shows = Array.Empty<object>() })).EnsureSuccessStatusCode()),
            ("a new title", async () =>
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                db.MediaItems.Add(new MediaItem { LibraryId = library, Kind = MediaKind.Movie, Title = "Newest", SortTitle = "newest", AddedAt = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }),
            ("a bulk update", async () =>
            {
                using var scope = factory.Services.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                await db.MediaItems.Where(i => i.Id == films[4]).ExecuteUpdateAsync(u => u.SetProperty(i => i.AddedAt, DateTime.UtcNow.AddMinutes(1)));
            }),
        };

        foreach (var (what, change) in changes)
        {
            var before = await HomeAsync();
            Assert.Equal(before, await HomeAsync());
            await change();
            var after = await HomeAsync();
            Assert.True(before != after, $"Home didn't change after {what}");
            Assert.Equal(await FreshAsync(), after);
        }
    }

    [Fact]
    public async Task EachProfile_IsKeptApart()
    {
        await SeedAsync();
        await HomeAsync();
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = "Guest" })).Content.ReadFromJsonAsync<JsonElement>();
        var selected = await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { });
        var guest = Client();
        guest.DefaultRequestHeaders.Add("X-Flexarr-Profile", (await selected.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        await guest.GetStringAsync(Api("/home"));

        Assert.Equal(2, HomeKeys().Count);
    }
}
