using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Integrations.Arr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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

    /// <summary>Binds config.json as the server does, then checks it.</summary>
    private FlexarrOptions Load(string json)
    {
        var path = ServerConfigStore.PathFor(_dir);
        File.WriteAllText(path, json);
        var config = new ConfigurationBuilder().AddJsonFile(path).Build();
        var options = new FlexarrOptions { DataDirectory = _dir };
        config.Bind(options);
        RetiredSettings.Check([config], _dir, NullLogger.Instance);
        RetiredSettings.DropUnknownKinds(options);
        return options;
    }

    private JsonObject Saved() => JsonNode.Parse(File.ReadAllText(ServerConfigStore.PathFor(_dir)))!.AsObject();

    [Fact]
    public void AConfigWithAReadarrConnection_Loads_WithoutIt_AndIsTidied()
    {
        var options = Load(OldConfig);

        Assert.Equal(["Sonarr", "Radarr"], options.Arr.Select(a => a.Name));
        Assert.Equal([ArrKind.Sonarr, ArrKind.Radarr], options.Arr.Select(a => a.Kind));
        Assert.Equal("kept", options.ApiKey);
        var saved = Saved();
        Assert.Equal(["Sonarr", "Radarr"], saved["arr"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
        Assert.False(saved.ContainsKey("bookRequests"));
        Assert.Equal("kept", saved["apiKey"]!.GetValue<string>());
    }

    [Fact]
    public void KindsTheBinderReads_StillLoad_AndOthersAreKeptInTheFile()
    {
        // Numbers and a missing kind loaded before (a missing one as Sonarr); a typo or a kind from a newer Flexarr
        // doesn't load, but it's someone's connection: it stays in config.json.
        var options = Load("""
            { "arr": [
              { "name": "A", "kind": "1", "baseUrl": "http://localhost:7878", "apiKey": "a" },
              { "name": "B", "baseUrl": "http://localhost:8989", "apiKey": "b" },
              { "name": "C", "kind": "Sonnar", "baseUrl": "http://localhost:8989", "apiKey": "c" },
              { "name": "D", "kind": "Whisparr", "baseUrl": "http://localhost:6969", "apiKey": "d" }
            ] }
            """);

        Assert.Equal([("A", ArrKind.Radarr), ("B", ArrKind.Sonarr)], options.Arr.Select(a => (a.Name, a.Kind)));
        Assert.Equal(["A", "B", "C", "D"], Saved()["arr"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
    }

    [Fact]
    public void ANumberWithNoKind_IsLeftOut_AndAPaddedReadarrIsTidied()
    {
        var options = Load("""
            { "arr": [
              { "name": "Old", "kind": "3", "baseUrl": "http://localhost:8787", "apiKey": "o" },
              { "name": "Padded", "kind": " Readarr ", "baseUrl": "http://localhost:8787", "apiKey": "p" },
              { "name": "Sonarr", "kind": "Sonarr", "baseUrl": "http://localhost:8989", "apiKey": "s" }
            ] }
            """);

        Assert.Equal(["Sonarr"], options.Arr.Select(a => a.Name));
        Assert.Equal(["Old", "Sonarr"], Saved()["arr"]!.AsArray().Select(a => a!["name"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData("Sonarr", true)]
    [InlineData("lidarr", true)]
    [InlineData("1", true)]
    [InlineData("Readarr", false)]
    [InlineData("Whisparr", false)]
    [InlineData("3", false)]
    [InlineData("", false)]
    public void ReadableKinds_AreWhatTheBinderReads(string kind, bool readable) => Assert.Equal(readable, RetiredSettings.Readable(kind));

    [Fact]
    public void AnUntouchedConfig_IsNotRewritten()
    {
        const string json = """{ "arr": [{ "name": "Sonarr", "kind": "Sonarr", "baseUrl": "http://localhost:8989", "apiKey": "s" }] }""";

        var options = Load(json);

        Assert.Single(options.Arr);
        Assert.Equal(json, File.ReadAllText(ServerConfigStore.PathFor(_dir)));
    }

    [Fact]
    public void AConfigItCantRewrite_StillLoads()
    {
        // Comments are fine to the config reader but not to the writer: the Readarr entry is left in, and that's all.
        const string json = """
            { // a note
              "arr": [ { "name": "Bookshelf", "kind": "Readarr", "baseUrl": "http://localhost:8787", "apiKey": "b" } ],
            }
            """;

        var options = Load(json);

        Assert.Empty(options.Arr);
        Assert.Equal(json, File.ReadAllText(ServerConfigStore.PathFor(_dir)));
    }

    [Fact]
    public void TheWarning_ShowsOnce_ThoughEverySaveReloads()
    {
        // From appsettings or the environment, which can't be tidied: still only one warning.
        var name = $"Bookshelf-{Guid.NewGuid():N}";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["arr:0:name"] = name, ["arr:0:kind"] = "Readarr" }).Build();
        var logger = new CountingLogger();

        RetiredSettings.Check([config], _dir, logger);
        RetiredSettings.Check([config], _dir, logger);

        Assert.Equal(1, logger.Warnings);
    }

    private sealed class CountingLogger : ILogger
    {
        public int Warnings { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Warnings += logLevel == LogLevel.Warning ? 1 : 0;
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
