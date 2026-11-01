using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Integrations.Arr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>Dev builds connected to Readarr, Bookshelf or Chaptarr; Flexarr no longer does, and must still start.</summary>
public sealed class RetiredConnectionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const string OldConfig = """
        {
          "apiKey": "kept",
          "arr": [
            { "name": "Sonarr", "kind": "Sonarr", "baseUrl": "http://localhost:8989", "apiKey": "s" },
            { "name": "Bookshelf", "kind": "Readarr", "baseUrl": "http://localhost:8787", "apiKey": "b" },
            { "name": "Radarr", "kind": "radarr", "baseUrl": "http://localhost:7878", "apiKey": "r" }
          ],
          "bookRequests": { "instance": "Bookshelf", "qualityProfileId": 1 }
        }
        """;

    [Fact]
    public void AConfigWithAReadarrConnection_Loads_WithoutIt_AndIsTidied()
    {
        var path = ServerConfigStore.PathFor(_dir);
        File.WriteAllText(path, OldConfig);
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = new FlexarrOptions { DataDirectory = _dir };

        FlexarrConfigBinder.Bind(config, options, NullLogger.Instance, _dir);

        Assert.Equal(["Sonarr", "Radarr"], options.Arr.Select(a => a.Name));
        Assert.Equal([ArrKind.Sonarr, ArrKind.Radarr], options.Arr.Select(a => a.Kind));
        Assert.Equal("kept", options.ApiKey);
        var saved = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Assert.Equal(["Sonarr", "Radarr"], saved["arr"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
        Assert.False(saved.ContainsKey("bookRequests"));
        Assert.Equal("kept", saved["apiKey"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("Sonarr", true)]
    [InlineData("lidarr", true)]
    [InlineData("Readarr", false)]
    [InlineData("Whisparr", false)]
    [InlineData("3", false)]
    [InlineData("1", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyKnownKindsByName(string? kind, bool known) => Assert.Equal(known, FlexarrConfigBinder.IsKnownKind(kind));

    [Fact]
    public void AnUntouchedConfig_IsNotRewritten()
    {
        var path = ServerConfigStore.PathFor(_dir);
        const string json = """{ "arr": [{ "name": "Sonarr", "kind": "Sonarr", "baseUrl": "http://localhost:8989", "apiKey": "s" }] }""";
        File.WriteAllText(path, json);
        var options = new FlexarrOptions { DataDirectory = _dir };

        FlexarrConfigBinder.Bind(new ConfigurationBuilder().AddJsonFile(path).Build(), options, NullLogger.Instance, _dir);

        Assert.Single(options.Arr);
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public async Task TheServer_StartsWithAReadarrConnectionInConfigJson()
    {
        using var factory = new FlexarrApiFactory();
        File.WriteAllText(ServerConfigStore.PathFor(factory.DataDirectory), OldConfig.Replace("\"apiKey\": \"kept\",", "", StringComparison.Ordinal));

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var response = await client.GetAsync(new Uri("/api/v1/arr/status", UriKind.Relative));
        var options = factory.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;

        Assert.True(response.IsSuccessStatusCode, response.StatusCode.ToString());
        Assert.Equal(["Sonarr", "Radarr"], options.Arr.Select(a => a.Name));
    }

    [Fact]
    public async Task TheReadarrWebhook_IsGone_AndScansNothing()
    {
        using var factory = new FlexarrApiFactory();

        var response = await factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/webhooks/readarr?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative),
            new { eventType = "Download", author = new { name = "Someone", path = "/books/Someone" } });

        Assert.Equal(HttpStatusCode.Gone, response.StatusCode);
        Assert.Empty(factory.ScanQueue.Requests);
    }
}
