using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>The API key in an address (?apikey=) still works for now, but each use is logged and shown in Settings.</summary>
public sealed class KeyInAddressTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-key-in-address-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    private sealed class RecordingLogger : ILogger<KeyInAddress>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }

    private (KeyInAddress Uses, RecordingLogger Logger, TestClock Clock) Make()
    {
        var logger = new RecordingLogger();
        var clock = new TestClock(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));
        return (new KeyInAddress(new StaticOptions(new FlexarrOptions { DataDirectory = _dir }), clock, logger), logger, clock);
    }

    [Fact]
    public void AUse_IsLoggedWithThePath_AndRecorded()
    {
        var (uses, logger, _) = Make();

        uses.Note(new PathString("/api/v1/webhooks/sonarr"), ApiKeyAuthenticationHandler.Methods.ApiKey);

        var warning = Assert.Single(logger.Warnings);
        Assert.Contains("/api/v1/webhooks/sonarr", warning, StringComparison.Ordinal);
        Assert.Contains("X-Api-Key", warning, StringComparison.Ordinal);
        Assert.Equal("/api/v1/webhooks/sonarr", uses.Recent()?.Path);
        Assert.True(File.Exists(Path.Combine(_dir, KeyInAddress.FileName)));
    }

    [Fact]
    public void TheWarning_ComesAtMostOnceAnHour_ForEachPathAndKind()
    {
        var (uses, logger, clock) = Make();

        uses.Note(new PathString("/api/v1/library"), ApiKeyAuthenticationHandler.Methods.ApiKey);
        clock.Advance(TimeSpan.FromMinutes(10));
        uses.Note(new PathString("/api/v1/library"), ApiKeyAuthenticationHandler.Methods.ApiKey);
        uses.Note(new PathString("/api/v1/library"), ApiKeyAuthenticationHandler.Methods.Device);
        uses.Note(new PathString("/api/v1/home"), ApiKeyAuthenticationHandler.Methods.ApiKey);
        Assert.Equal(3, logger.Warnings.Count);

        clock.Advance(TimeSpan.FromMinutes(51));
        uses.Note(new PathString("/api/v1/library"), ApiKeyAuthenticationHandler.Methods.ApiKey);
        Assert.Equal(4, logger.Warnings.Count);
        // The last use is still the latest one, warned or not.
        Assert.Equal(clock.GetUtcNow().UtcDateTime, uses.Recent()?.LastUsed);
    }

    [Fact]
    public void TheLastUse_IsShownFor30Days_AndKeptAcrossRestarts()
    {
        var (uses, _, clock) = Make();
        uses.Note(new PathString("/api/v1/library"), ApiKeyAuthenticationHandler.Methods.ApiKey);

        var restarted = new KeyInAddress(new StaticOptions(new FlexarrOptions { DataDirectory = _dir }), clock, new RecordingLogger());
        clock.Advance(TimeSpan.FromDays(29));
        Assert.Equal("/api/v1/library", restarted.Recent()?.Path);

        clock.Advance(TimeSpan.FromDays(2));
        Assert.Null(restarted.Recent());
        Assert.Null(uses.Recent());
    }

    [Fact]
    public async Task TheServer_RecordsTheQueryForm_AndNotTheHeader()
    {
        using var factory = new FlexarrApiFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        // The header records nothing.
        var settings = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/settings", UriKind.Relative));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("keyInAddress").ValueKind);

        // The query form still works, and is recorded by path, never with the key.
        using var bare = factory.CreateClient();
        var viaAddress = await bare.GetAsync(new Uri($"/api/v1/settings?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative));
        Assert.True(viaAddress.IsSuccessStatusCode, viaAddress.StatusCode.ToString());

        settings = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/settings", UriKind.Relative));
        var use = settings.GetProperty("keyInAddress");
        Assert.Equal("/api/v1/settings", use.GetProperty("path").GetString());
        Assert.DoesNotContain(FlexarrApiFactory.ApiKey, File.ReadAllText(Path.Combine(factory.DataDirectory, KeyInAddress.FileName)), StringComparison.Ordinal);

        // A wrong key in the address is refused, and not recorded.
        using var other = new FlexarrApiFactory();
        using var wrong = other.CreateClient();
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await wrong.GetAsync(new Uri("/api/v1/settings?apikey=nope", UriKind.Relative))).StatusCode);
        Assert.Null(other.Services.GetRequiredService<KeyInAddress>().Recent());
    }

    [Fact]
    public async Task TheSettingsField_GoesAfter30Days()
    {
        var clock = new TestClock(DateTimeOffset.UtcNow);
        using var factory = new FlexarrApiFactory();
        using var server = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        using var client = server.CreateClient();

        Assert.True((await client.GetAsync(new Uri($"/api/v1/settings?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative))).IsSuccessStatusCode);
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var settings = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/settings", UriKind.Relative));
        Assert.Equal(JsonValueKind.Object, settings.GetProperty("keyInAddress").ValueKind);

        clock.Advance(TimeSpan.FromDays(31));
        settings = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/settings", UriKind.Relative));
        Assert.Equal(JsonValueKind.Null, settings.GetProperty("keyInAddress").ValueKind);
    }
}
