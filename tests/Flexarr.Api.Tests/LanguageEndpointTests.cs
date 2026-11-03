using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flexarr.Api.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class LanguageEndpointTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    // en-XA, the pseudo-language: harmless if other tests' requests ever saw it.
    private const string Pack = """{"format":1,"code":"en-XA","name":"Pseudo (test)","direction":"ltr","version":7,"coverage":{"web":1},"messages":{"Play":"⟦Ƥĺàý~⟧","Only on Android":"⟦x⟧"},"scopes":{"Play":"wan","Only on Android":"n"}}""";

    private void KeepPseudo()
    {
        var folder = Directory.CreateDirectory(Path.Combine(factory.DataDirectory, "languages")).FullName;
        File.WriteAllText(Path.Combine(folder, "en-XA.json"), Pack);
        factory.Services.GetRequiredService<LanguageIndex>().LoadKept();
    }

    [Fact]
    public async Task Lists_english_and_kept_languages_without_signing_in()
    {
        KeepPseudo();
        var list = await factory.CreateClient().GetFromJsonAsync<LanguagesDto>("/api/v1/languages");

        Assert.Equal("en", list!.Languages[0].Code);
        Assert.Contains(list.Languages, l => l.Code == "en-XA" && l.Version == 7);
    }

    [Fact]
    public async Task Serves_a_pack_for_one_codebase_with_an_etag()
    {
        KeepPseudo();
        var client = factory.CreateClient();
        var response = await client.GetAsync("/api/v1/languages/en-XA?scope=web");
        var pack = await response.Content.ReadFromJsonAsync<LanguagePackDto>();

        Assert.Equal(["Play"], pack!.Messages.Keys);
        var again = new HttpRequestMessage(HttpMethod.Get, "/api/v1/languages/en-XA?scope=web");
        again.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(response.Headers.ETag!.ToString()));
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(again)).StatusCode);
    }

    [Theory]
    [InlineData("/api/v1/languages/xx", HttpStatusCode.NotFound)]
    [InlineData("/api/v1/languages/..%2Fetc", HttpStatusCode.BadRequest)]
    [InlineData("/api/v1/languages/en-XA?scope=ios", HttpStatusCode.BadRequest)]
    public async Task Refuses_what_it_doesnt_have(string url, HttpStatusCode expected)
    {
        KeepPseudo();
        Assert.Equal(expected, (await factory.CreateClient().GetAsync(url)).StatusCode);
    }
}
