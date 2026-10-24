using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public sealed class ServerConfigStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-test-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void GeneratesApiKey_AndPreservesExistingSettings()
    {
        var path = Path.Combine(_dir, ServerConfigStore.FileName);
        File.WriteAllText(path, """{ "pathMappings": [{ "from": "/data", "to": "/Volumes/Media/data" }] }""");
        var options = new FlexarrOptions { DataDirectory = _dir };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        var saved = JsonNode.Parse(File.ReadAllText(path))!;
        Assert.Equal(32, options.ApiKey!.Length);
        Assert.Equal(options.ApiKey, saved["apiKey"]!.GetValue<string>());
        Assert.Equal("/data", saved["pathMappings"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void KeepsConfiguredApiKey()
    {
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing" };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        Assert.Equal("existing", options.ApiKey);
        Assert.False(File.Exists(Path.Combine(_dir, ServerConfigStore.FileName)));
    }
}
