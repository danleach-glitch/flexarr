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
        Assert.Equal(32, options.ServerId!.Length);
        Assert.Equal(options.ServerId, saved["serverId"]!.GetValue<string>());
        Assert.Equal("/data", saved["pathMappings"]![0]!["from"]!.GetValue<string>());
    }

    [Fact]
    public void KeepsConfiguredApiKey()
    {
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing", WebhookSecret = "hook", ImageSecret = "image", ServerId = "server", LegacySignInsUntil = DateTime.UtcNow };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        Assert.Equal("existing", options.ApiKey);
        Assert.False(File.Exists(Path.Combine(_dir, ServerConfigStore.FileName)));
    }

    [Fact]
    public void ConfigFile_IsReadableOnlyByItsOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var path = Path.Combine(_dir, ServerConfigStore.FileName);
        File.WriteAllText(path, "{}");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing", WebhookSecret = "hook", ImageSecret = "image" };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options); // an existing file is tightened
        var tightened = File.GetUnixFileMode(path);
        ServerConfigStore.Update(_dir, config => config["seerr"] = null); // and rewritten ones stay that way

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, tightened);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    [Fact]
    public void ALeftoverTemporaryFile_IsReplaced_NotReused()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // An interrupted write left one readable by everyone (and read-only, so reusing it would fail outright).
        var path = Path.Combine(_dir, ServerConfigStore.FileName);
        File.WriteAllText(path + ".tmp", "{}");
        File.SetUnixFileMode(path + ".tmp", UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        ServerConfigStore.WritePrivately(path, """{ "apiKey": "secret" }""");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Contains("secret", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void EarlierSignIns_GetAMonthFromTheFirstRun()
    {
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing", WebhookSecret = "hook" };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, ServerConfigStore.FileName)))!;
        Assert.InRange(options.LegacySignInsUntil!.Value, DateTime.UtcNow.AddDays(29), DateTime.UtcNow.AddDays(31));
        Assert.NotNull(saved["legacySignInsUntil"]);
        Assert.True(options.AcceptsLegacySignIns(DateTime.UtcNow));
        Assert.False(options.AcceptsLegacySignIns(DateTime.UtcNow.AddDays(31)));
    }

    [Fact]
    public void GeneratesAWebhookSecret_SeparateFromTheApiKey()
    {
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing" };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, ServerConfigStore.FileName)))!;
        Assert.Equal(32, options.WebhookSecret!.Length);
        Assert.Equal(options.WebhookSecret, saved["webhookSecret"]!.GetValue<string>());
    }

    [Fact]
    public void GeneratesAnImageSecret_ThatSurvivesARestart()
    {
        var options = new FlexarrOptions { DataDirectory = _dir, ApiKey = "existing", WebhookSecret = "hook" };

        new ServerConfigStore(NullLogger<ServerConfigStore>.Instance).PostConfigure(null, options);

        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(_dir, ServerConfigStore.FileName)))!;
        Assert.Equal(64, options.ImageSecret!.Length);
        Assert.NotEqual(options.ApiKey, options.ImageSecret);
        Assert.Equal(options.ImageSecret, saved["imageSecret"]!.GetValue<string>());
    }

    [Fact]
    public async Task AWrite_WaitsForItsOwnReload_NotALateOneOfAnEarlierWrite()
    {
        var options = new ReloadingOptions(new FlexarrOptions { DataDirectory = _dir, ConfigRevision = "earlier" });
        var write = ServerConfigStore.UpdateAndWaitAsync(options, _dir, config => config["podcasts"] = new JsonObject { ["country"] = "gb" }, TimeSpan.FromSeconds(10));

        // A reload left over from the previous write lands first, holding the earlier settings.
        options.Reload(new FlexarrOptions { DataDirectory = _dir, ConfigRevision = "earlier" });
        await Task.Delay(100);
        var waitedPastStale = !write.IsCompleted;

        // Then the reload of this write.
        var revision = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(_dir, ServerConfigStore.FileName)))![ServerConfigStore.RevisionKey]!.GetValue<string>();
        options.Reload(new FlexarrOptions { DataDirectory = _dir, ConfigRevision = revision });
        await write.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(waitedPastStale);
    }

    /// <summary>Options that reload when told to, as the file watcher would.</summary>
    private sealed class ReloadingOptions(FlexarrOptions initial) : Microsoft.Extensions.Options.IOptionsMonitor<FlexarrOptions>
    {
        private readonly List<Action<FlexarrOptions, string?>> _listeners = [];

        public FlexarrOptions CurrentValue { get; private set; } = initial;

        public FlexarrOptions Get(string? name) => CurrentValue;

        public void Reload(FlexarrOptions options)
        {
            CurrentValue = options;
            foreach (var listener in _listeners.ToList())
            {
                listener(options, null);
            }
        }

        public IDisposable OnChange(Action<FlexarrOptions, string?> listener)
        {
            _listeners.Add(listener);
            return new Unsubscribe(() => _listeners.Remove(listener));
        }

        private sealed class Unsubscribe(Action remove) : IDisposable
        {
            public void Dispose() => remove();
        }
    }
}
