using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Configuration;

namespace Flexarr.Api.Tests;

public class BookMetadataSettingsTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Fact]
    public void AListSavedBeforeAppleBooks_GetsItSwitchedOn_AndLosesTheProvidersTakenOut()
    {
        var saved = new BookMetadataOptions { Providers = ["audible", "google", "hardcover"] };

        Assert.Equal(["audible", "itunes"], saved.Resolved("us").Providers);
    }

    [Fact]
    public async Task TurningAppleBooksOff_StaysOff()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var put = await client.PutAsJsonAsync(Api("/settings/book-metadata"), new { providers = new[] { "audible", "openlibrary" } });
        put.EnsureSuccessStatusCode();
        var regionOnly = await client.PutAsJsonAsync(Api("/settings/book-metadata"), new { region = "uk" });
        var settings = await regionOnly.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(["audible", "openlibrary"], settings.GetProperty("providers").EnumerateArray().Select(p => p.GetString()));
        Assert.Contains("itunes", settings.GetProperty("allProviders").EnumerateArray().Select(p => p.GetString()));
    }
}
