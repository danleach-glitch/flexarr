using System.Net;
using System.Net.Http.Json;

namespace Flexarr.Api.Tests;

public class ApiTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task Health_IsAnonymous()
    {
        var response = await factory.CreateClient().GetAsync(new Uri("/health", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task SystemStatus_RequiresApiKey()
    {
        var response = await factory.CreateClient().GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SystemStatus_WithHeaderKey_ReturnsStatus()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var status = await client.GetFromJsonAsync<Dictionary<string, object>>(new Uri("/api/v1/system/status", UriKind.Relative));

        Assert.Equal("Flexarr", status!["appName"].ToString());
    }

    [Fact]
    public async Task SystemStatus_WithWrongKey_IsRejected()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "nope");

        var response = await client.GetAsync(new Uri("/api/v1/system/status", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_TestEvent_ReturnsOk()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/webhooks/radarr?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative),
            new { eventType = "Test" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_SonarrDownload_QueuesScan()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/webhooks/sonarr?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative),
            new { eventType = "Download", series = new { title = "Andor", path = "/tv/Andor" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == "/tv/Andor");
    }

    [Fact]
    public async Task Webhook_UnknownSource_IsNotFound()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/webhooks/readarr?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative),
            new { eventType = "Download" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Webhook_AppliesPathMappings()
    {
        var response = await factory.CreateClient().PostAsJsonAsync(
            new Uri($"/api/v1/webhooks/radarr?apikey={FlexarrApiFactory.ApiKey}", UriKind.Relative),
            new { eventType = "Download", movie = new { title = "Dune", folderPath = "/data/movies/Dune (2021)" } });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains(factory.ScanQueue.Requests, r => r.Path == "/Volumes/Media/data/movies/Dune (2021)");
    }
}
