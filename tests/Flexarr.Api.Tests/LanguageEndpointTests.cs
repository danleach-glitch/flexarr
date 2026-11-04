using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Flexarr.Api.Localization;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public sealed class LanguageEndpointTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    // en-XA, the pseudo-language: harmless if other tests' requests ever saw it.
    private const string Pack = """{"format":1,"code":"en-XA","name":"Pseudo (test)","direction":"ltr","version":7,"coverage":{"web":1},"messages":{"Play":"⟦Ƥĺàý~⟧","Only on Android":"⟦x⟧","That isn’t a language code.":"⟦Ţĥàţ íšñ'ţ à ĺàñĝûàĝé çöđé.~~~~⟧"},"scopes":{"Play":"wan","Only on Android":"n","That isn’t a language code.":"s"}}""";

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

    [Fact]
    public async Task A_profile_picks_its_language_and_me_shows_it()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync("/api/v1/me/language", new { language = "pt-BR" })).StatusCode);
        Assert.Equal("pt-BR", (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/me")).GetProperty("language").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/v1/me/language", new { language = "not a code" })).StatusCode);

        await client.PutAsJsonAsync("/api/v1/me/language", new { language = (string?)null });
        Assert.Equal(System.Text.Json.JsonValueKind.Null, (await client.GetFromJsonAsync<System.Text.Json.JsonElement>("/api/v1/me")).GetProperty("language").ValueKind);
    }

    [Fact]
    public async Task Answers_in_the_apps_language_then_the_profiles()
    {
        KeepPseudo();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("fr;q=0.9, en-XA");
        await client.PutAsJsonAsync("/api/v1/me/language", new { language = (string?)null });

        var refused = await client.PutAsJsonAsync("/api/v1/me/language", new { language = "not a code" });
        var body = await refused.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
        Assert.Contains("Ţĥàţ", body.GetProperty("errors").GetProperty("language")[0].GetString());
        Assert.Equal("That isn’t a language code.", body.GetProperty("code").GetString());

        // The profile's own choice wins over the app's.
        await client.PutAsJsonAsync("/api/v1/me/language", new { language = "en" });
        refused = await client.PutAsJsonAsync("/api/v1/me/language", new { language = "not a code" });
        Assert.Contains("That isn’t a language code.", await refused.Content.ReadAsStringAsync());
        await client.PutAsJsonAsync("/api/v1/me/language", new { language = (string?)null });
    }

    [Theory]
    [InlineData("pt-br", "pt-BR")]
    [InlineData("zh-hans-cn", "zh-Hans-CN")]
    [InlineData("EN", "en")]
    public void Normalises_language_tags(string tag, string expected) => Assert.Equal(expected, RequestLanguage.Normalise(tag));

    [Fact]
    public async Task Api_answers_vary_by_language()
    {
        var response = await factory.CreateClient().GetAsync("/api/v1/languages");
        Assert.Contains("Accept-Language", response.Headers.Vary);
    }
}
