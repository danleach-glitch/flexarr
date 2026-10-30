using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Search;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Search forgiving typos, accents and punctuation, and each profile's recent searches.</summary>
public class SearchTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
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

    [Theory]
    [InlineData("Amélie", "amelie")]
    [InlineData("Spider-Man: No Way Home", "spider man no way home")]
    [InlineData("Marvel's Daredevil", "marvels daredevil")]
    [InlineData("Law & Order", "law and order")]
    [InlineData("  WALL·E  ", "wall e")]
    public void Normalize_IgnoresCaseAccentsAndPunctuation(string text, string expected) =>
        Assert.Equal(expected, SearchText.Normalize(text));

    [Theory]
    [InlineData("breaking", "Breaking Bad", 0)]
    [InlineData("bad", "Breaking Bad", 1)]
    [InlineData("spiderman", "Spider-Man", 0)]
    [InlineData("amelie", "Amélie", 0)]
    [InlineData("breking bad", "Breaking Bad", 3)]
    [InlineData("the ofice", "The Office", 3)]
    [InlineData("intersteller", "Interstellar", 3)]
    [InlineData("shawshank redemtion", "The Shawshank Redemption", 3)]
    [InlineData("godfahter", "The Godfather", 3)] // two letters swapped is one typo
    [InlineData("succesio", "Succession", 3)] // still typing
    public void Cost_ForgivesTyposInLongerWords(string query, string title, int cost) =>
        Assert.Equal(cost, SearchText.Cost(SearchText.Query.Parse(query), SearchText.Candidate.Of(title)));

    [Theory]
    [InlineData("bd", "Breaking Bad")] // too short to guess at
    [InlineData("cat", "Cars")] // short words must be exact
    [InlineData("alien", "Aladdin")]
    [InlineData("breaking good", "Breaking Bad")]
    [InlineData("", "Breaking Bad")]
    public void Cost_DoesntGuessWild(string query, string title) =>
        Assert.Null(SearchText.Cost(SearchText.Query.Parse(query), SearchText.Candidate.Of(title)));

    [Fact]
    public async Task Search_FindsTitlesPeopleAndGenres_DespiteTypos()
    {
        var word = Unique();
        var (_, film) = await SeedAsync($"Amélie {word}", MediaKind.Movie);
        var (_, show) = await SeedAsync($"Breaking-{word} Saga", MediaKind.Series);

        var accents = await SearchAsync($"amelie {word}");
        var typo = await SearchAsync($"braeking{word}");

        Assert.Contains(film, Titles(accents));
        Assert.Contains(show, Titles(typo));
    }

    [Fact]
    public async Task Search_ExactMatchesStillComeFirst()
    {
        var word = Unique();
        var (_, close) = await SeedAsync($"{word[..^1]}x Story", MediaKind.Movie);
        var (_, exact) = await SeedAsync($"{word} Story", MediaKind.Movie);

        var titles = Titles(await SearchAsync(word));

        Assert.Equal(exact, titles[0]);
        Assert.Contains(close, titles);
    }

    [Fact]
    public async Task Search_TyposDontReachIntoHiddenLibraries()
    {
        var word = Unique();
        var (library, film) = await SeedAsync($"{word} Hidden", MediaKind.Movie);
        var token = await ProfileAsync("Hides", library);

        var found = await SearchAsync(word[..^1] + "q", token);

        Assert.DoesNotContain(film, Titles(found));
    }

    [Fact]
    public async Task RecentSearches_AreNewestFirst_OncePerSearch_AndPerProfile()
    {
        var token = await ProfileAsync("Recent");
        var other = await ProfileAsync("Other");
        var (_, film) = await SeedAsync($"Recent {Unique()}", MediaKind.Movie);
        var (_, show) = await SeedAsync($"Recent {Unique()}", MediaKind.Series);
        var client = Client(token);

        foreach (var (query, item) in new (string?, Guid?)[] { ("one", film), ("two", null), ("ONE", show), ("three", null), ("four", null), ("five", null), ("six", null), (null, film) })
        {
            Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsJsonAsync(Api("/search/recent"), new { query, itemId = item })).StatusCode);
        }

        var recent = await client.GetFromJsonAsync<JsonElement>(Api("/search/recent"));
        var others = await Client(other).GetFromJsonAsync<JsonElement>(Api("/search/recent"));

        Assert.Equal(["six", "five", "four", "three", "ONE"], Queries(recent));
        Assert.Equal([film, show], Items(recent));
        Assert.Empty(Queries(others));
        Assert.Empty(Items(others));
    }

    [Fact]
    public async Task RecentSearches_CanBeForgotten_OneAtATimeOrAllAtOnce()
    {
        var client = Client(await ProfileAsync("Forget"));
        var (_, film) = await SeedAsync($"Forget {Unique()}", MediaKind.Movie);
        await client.PostAsJsonAsync(Api("/search/recent"), new { query = "kept", itemId = film });
        await client.PostAsJsonAsync(Api("/search/recent"), new { query = "Gone" });
        await client.PostAsJsonAsync(Api("/search/recent"), new { query = "gone" });

        await client.DeleteAsync(Api("/search/recent?query=GONE"));
        await client.DeleteAsync(Api($"/search/recent?itemId={film}"));
        var forgotten = await client.GetFromJsonAsync<JsonElement>(Api("/search/recent"));
        await client.DeleteAsync(Api("/search/recent"));
        var cleared = await client.GetFromJsonAsync<JsonElement>(Api("/search/recent"));

        Assert.Equal(["kept"], Queries(forgotten));
        Assert.Empty(Items(forgotten));
        Assert.Empty(Queries(cleared));
    }

    [Fact]
    public async Task RecentSearch_NeedsWordsOrAnItem_AndIgnoresUnknownItems()
    {
        var client = Client(await ProfileAsync("Unknown"));

        var empty = await client.PostAsJsonAsync(Api("/search/recent"), new { query = "  " });
        var unknown = await client.PostAsJsonAsync(Api("/search/recent"), new { query = "words", itemId = Guid.NewGuid() });
        var recent = await client.GetFromJsonAsync<JsonElement>(Api("/search/recent"));

        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, unknown.StatusCode);
        Assert.Equal(["words"], Queries(recent));
        Assert.Empty(Items(recent));
    }

    private static string Unique() => $"zq{Guid.NewGuid().ToString("N")[..8]}";

    private Task<JsonElement> SearchAsync(string query, string? token = null) =>
        Client(token).GetFromJsonAsync<JsonElement>(Api($"/search?q={Uri.EscapeDataString(query)}"));

    private static List<Guid> Titles(JsonElement results) => [.. results.GetProperty("titles").EnumerateArray().Select(t => t.GetProperty("id").GetGuid())];

    private static List<string> Queries(JsonElement recent) => [.. recent.GetProperty("queries").EnumerateArray().Select(q => q.GetString()!)];

    private static List<Guid> Items(JsonElement recent) => [.. recent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())];

    private async Task<string> ProfileAsync(string name, params Guid[] hidden)
    {
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"{name} {Unique()}", hiddenLibraryIds = hidden }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var selected = await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return selected.GetProperty("token").GetString()!;
    }

    private async Task<(Guid Library, Guid Item)> SeedAsync(string title, MediaKind kind)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Search {Guid.NewGuid():N}", Type = kind == MediaKind.Movie ? LibraryType.Movies : LibraryType.TvShows };
        var item = new MediaItem { LibraryId = library.Id, Kind = kind, Title = title, SortTitle = title.ToLowerInvariant() };
        db.AddRange(library, item);
        await db.SaveChangesAsync();
        return (library.Id, item.Id);
    }
}
