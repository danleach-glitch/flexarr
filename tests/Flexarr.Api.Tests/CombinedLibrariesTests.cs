using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class CombinedLibrariesTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Fact]
    public async Task Home_CombinesFilmLibraries_ShowingATitleInBothOnce_UntilSwitchedOff()
    {
        var (films, uhd, older, newer, other) = await SeedAsync();
        var client = Client();

        var home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
        var rows = home.GetProperty("rows").EnumerateArray().ToList();
        var combined = Assert.Single(rows, r => r.GetProperty("id").GetString()!.StartsWith("recent-", StringComparison.Ordinal));
        Assert.Equal("recent-movies", combined.GetProperty("id").GetString());
        Assert.Equal("Latest unwatched films", combined.GetProperty("title").GetString());
        Assert.Equal(JsonValueKind.Null, combined.GetProperty("link").ValueKind);
        // The newest copy of the film in both, then the other one; never the same film twice.
        Assert.Equal([newer, other], combined.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.DoesNotContain(older.ToString(), combined.GetRawText(), StringComparison.Ordinal);

        var layout = await client.GetFromJsonAsync<JsonElement>(Api("/settings/layout"));
        Assert.True(layout.GetProperty("combineLibraries").GetBoolean());
        Assert.True(layout.GetProperty("combinable").GetBoolean());
        Assert.Contains(layout.GetProperty("home").EnumerateArray(), r => r.GetProperty("id").GetString() == "recent-movies");

        var rows2 = layout.GetProperty("home").EnumerateArray().Select(r => new { id = r.GetProperty("id").GetString(), enabled = r.GetProperty("enabled").GetBoolean() }).ToList();
        var put = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = rows2, movies = Array.Empty<object>(), shows = Array.Empty<object>(), combineLibraries = false });

        // Settings redraws from the answer.
        layout = await put.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>();
        var ids = layout.GetProperty("home").EnumerateArray().Select(r => r.GetProperty("id").GetString()).ToList();
        Assert.False(layout.GetProperty("combineLibraries").GetBoolean());
        Assert.DoesNotContain("recent-movies", ids);
        // Each library's row is back where the combined one was.
        Assert.Equal(ids.IndexOf($"recent-{films}") + 1, ids.IndexOf($"recent-{uhd}"));
        Assert.Equal(1, ids.IndexOf($"recent-{films}") - ids.IndexOf("live-tv"));
        List<JsonElement> own = [];
        for (var i = 0; i < 30 && own.Count < 2; i++)
        {
            await Task.Delay(100);
            home = await client.GetFromJsonAsync<JsonElement>(Api("/home"));
            own = [.. home.GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("id").GetString()!.StartsWith("recent-", StringComparison.Ordinal))];
        }

        Assert.Equal([$"recent-{films}", $"recent-{uhd}"], own.Select(r => r.GetProperty("id").GetString()));
        Assert.All(own, r => Assert.StartsWith("Latest unwatched films in ", r.GetProperty("title").GetString(), StringComparison.Ordinal));

        // An app from before the setting sends the rows without it: the setting stays off.
        var withoutIt = await client.PutAsJsonAsync(Api("/settings/layout"), new { home = rows2, movies = Array.Empty<object>(), shows = Array.Empty<object>() });
        Assert.False((await withoutIt.EnsureSuccessStatusCode().Content.ReadFromJsonAsync<JsonElement>()).GetProperty("combineLibraries").GetBoolean());
    }

    private async Task<(Guid Films, Guid Uhd, Guid Older, Guid Newer, Guid Other)> SeedAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var films = new Library { Name = "Films", Type = LibraryType.Movies };
        var uhd = new Library { Name = "Films in 4K", Type = LibraryType.Movies };
        var now = DateTime.UtcNow;
        var older = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Both", SortTitle = "both", TmdbId = "603", AddedAt = now.AddDays(-3) };
        var other = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "Only HD", SortTitle = "only hd", TmdbId = "604", AddedAt = now.AddDays(-2) };
        var newer = new MediaItem { LibraryId = uhd.Id, Kind = MediaKind.Movie, Title = "Both", SortTitle = "both", TmdbId = "603", AddedAt = now.AddDays(-1) };
        db.AddRange(films, uhd, older, other, newer);
        await db.SaveChangesAsync();
        return (films.Id, uhd.Id, older.Id, newer.Id, other.Id);
    }
}
