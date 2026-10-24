using System.Net;
using System.Net.Http.Json;

namespace Flexarr.Api.Tests;

/// <summary>The calls Sonarr and Radarr's "Emby / Jellyfin" connection makes, as their source makes them.</summary>
public class MediaBrowserTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private HttpClient Client(string? token = FlexarrApiFactory.ApiKey)
    {
        var client = factory.CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Add("X-MediaBrowser-Token", token);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"MediaBrowser Token=\"{token}\"");
        }

        return client;
    }

    [Fact]
    public async Task ConnectionTest_NeedsTheApiKey()
    {
        Assert.Equal(HttpStatusCode.OK, (await Client().GetAsync(new Uri("/System/Configuration", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client("wrong").GetAsync(new Uri("/System/Configuration", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Client(null).GetAsync(new Uri("/System/Configuration", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task MediaUpdated_QueuesAScanOfTheMappedPath()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/Library/Media/Updated", UriKind.Relative),
            new { Updates = new[] { new { Path = "/data/tv/Severance", UpdateType = "Created" } } });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == "/Volumes/Media/data/tv/Severance");
    }

    [Fact]
    public async Task MediaUpdated_WithAControlCharacterInAPath_IsRefused()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/Library/Media/Updated", UriKind.Relative),
            new { Updates = new[] { new { Path = "/data/tv/Bad\u0000Path", UpdateType = "Created" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.ScanQueue.Requests, r => r.Path.Contains('\0', StringComparison.Ordinal));
    }

    [Fact]
    public async Task MediaUpdated_WithALineSeparatorInAPath_IsRefused()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/Library/Media/Updated", UriKind.Relative),
            new { Updates = new[] { new { Path = "/data/tv/Forged\u2028Scan of /etc finished", UpdateType = "Created" } } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain(factory.ScanQueue.Requests, r => r.Path.Contains('\u2028', StringComparison.Ordinal));
    }

    [Fact]
    public async Task Items_ReturnsTheShapeSonarrReads()
    {
        var body = await Client().GetStringAsync(new Uri("/Items?recursive=true&includeItemTypes=Series&fields=Path,ProviderIds&years=2022", UriKind.Relative));

        Assert.Contains("\"items\"", body, StringComparison.OrdinalIgnoreCase);
    }
}
