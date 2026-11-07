using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Where each letter starts in a library's A–Z list (audit perf-14).</summary>
public class LibraryLetterTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<Guid> LibraryAsync(params string[] titles)
    {
        var library = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.Libraries.Add(library);
        var year = 1990;
        foreach (var title in titles)
        {
            db.MediaItems.Add(new MediaItem { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = SortTitle.For(title), Year = year++ });
        }

        await db.SaveChangesAsync();
        return library.Id;
    }

    [Fact]
    public async Task Each_letter_starts_where_the_A_to_Z_list_has_it()
    {
        var id = await LibraryAsync("Alien", "Aliens", "The Birds", "Heat", "12 Angry Men", "Brazil", "Zodiac", "Élite", "Amélie");

        var letters = await Client().GetFromJsonAsync<List<LetterOffsetDto>>(new Uri($"/api/v1/libraries/{id}/items/letters", UriKind.Relative), Json);
        var page = await Client().GetFromJsonAsync<PagedDto<ItemSummaryDto>>(new Uri($"/api/v1/libraries/{id}/items?take=500", UriKind.Relative), Json);

        Assert.Equal(
            [new("#", 0, 1), new("A", 1, 3), new("B", 4, 2), new("H", 6, 1), new("Z", 7, 1), new("#", 8, 1)],
            letters);
        // Each offset is the skip of its first title in the items list as it comes today.
        Assert.Equal(9, page!.Total);
        foreach (var run in letters!.Where(l => l.Letter != "#"))
        {
            var first = page.Items[run.Offset];
            Assert.Equal(run.Letter, SortTitle.For(first.Title)[..1].ToUpperInvariant());
            var one = await Client().GetFromJsonAsync<PagedDto<ItemSummaryDto>>(
                new Uri($"/api/v1/libraries/{id}/items?skip={run.Offset}&take=1", UriKind.Relative), Json);
            Assert.Equal(first.Id, one!.Items.Single().Id);
        }
    }

    [Fact]
    public async Task Letters_follow_the_search_and_an_unknown_library_has_none()
    {
        var id = await LibraryAsync("Alien", "Aliens", "Brazil", "Zodiac");

        var letters = await Client().GetFromJsonAsync<List<LetterOffsetDto>>(new Uri($"/api/v1/libraries/{id}/items/letters?search=ali", UriKind.Relative), Json);
        Assert.Equal([new("A", 0, 2)], letters);

        Assert.Empty((await Client().GetFromJsonAsync<List<LetterOffsetDto>>(new Uri($"/api/v1/libraries/{Guid.NewGuid()}/items/letters", UriKind.Relative), Json))!);
    }

    [Fact]
    public void Adjacent_first_characters_of_one_letter_merge()
    {
        Assert.Equal([new("#", 0, 5), new("A", 5, 3)], LibraryLetterEndpoints.Runs([("1", 2), ("9", 3), ("a", 3)]));
        Assert.Equal([new("#", 0, 1)], LibraryLetterEndpoints.Runs([("", 1)]));
    }
}
