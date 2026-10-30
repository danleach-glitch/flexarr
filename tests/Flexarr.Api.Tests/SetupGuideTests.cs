using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flexarr.Api.Endpoints;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Flexarr.Api.Tests;

public sealed class SetupGuideTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Guide_StartsUnopened_AndRemembersWhatsSkipped()
    {
        using var server = new FlexarrApiFactory();
        var client = Client(server);

        var fresh = await client.GetFromJsonAsync<SetupGuideDto>("/api/v1/setup-guide", Json);
        Assert.False(fresh!.Opened);
        Assert.False(fresh.Libraries);
        Assert.False(fresh.Profiles);

        var saved = await client.PutAsJsonAsync("/api/v1/setup-guide", new { opened = true, skipped = new[] { "away", "nonsense", "away" } });
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        var after = await client.GetFromJsonAsync<SetupGuideDto>("/api/v1/setup-guide", Json);
        Assert.True(after!.Opened); // Straight away: the save waits for the options to reload.
        Assert.Equal(["away"], after.Skipped);
        Assert.False(after.Dismissed);
    }

    [Fact]
    public async Task Discover_FindsAppsAnsweringAtTheirUsualPorts_ThatArentConnected()
    {
        using var server = new FlexarrApiFactory();
        var arr = new FakeArr();
        arr.Responses["GET //lidarr/ping"] = """{"status":"OK"}""";
        arr.Responses["GET //sonarr/ping"] = """{"status":"OK"}"""; // Connected already: not offered.
        using var app = arr.Serve(server);

        var found = await Client(app).GetFromJsonAsync<List<FoundArrDto>>("/api/v1/arr/discover", Json);

        var lidarr = Assert.Single(found!);
        Assert.Equal((ArrKind.Lidarr, "http://lidarr:8686"), (lidarr.Kind, lidarr.BaseUrl));
    }

    [Fact]
    public async Task Webhook_AddsFlexarrToTheApp_WithTheTriggersItSupports()
    {
        using var server = new FlexarrApiFactory();
        var arr = new FakeArr();
        arr.Responses["GET //sonarr.test/api/v3/notification"] = "[]";
        arr.Responses["GET //sonarr.test/api/v3/notification/schema"] = """
            [{"implementation":"Discord","fields":[]},
             {"implementation":"Webhook","name":"","onGrab":false,"onDownload":false,"onUpgrade":false,"onRename":false,"supportsOnRename":false,
              "onSeriesDelete":false,"fields":[{"name":"url","value":null},{"name":"method","value":1}]}]
            """;
        arr.Responses["POST //sonarr.test/api/v3/notification/test"] = "{}";
        arr.Responses["POST //sonarr.test/api/v3/notification"] = """{"id":7}""";
        var bodies = new List<(string Path, JsonObject Body)>();
        arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Post)
            {
                lock (bodies)
                {
                    bodies.Add((r.RequestUri!.AbsolutePath, JsonNode.Parse(r.Content!.ReadAsStringAsync().Result)!.AsObject()));
                }
            }
        };
        using var app = arr.Serve(server);

        var result = await (await Client(app).PostAsJsonAsync("/api/v1/arr/webhook", new { name = "Sonarr" })).Content.ReadFromJsonAsync<ArrWebhookDto>(Json);

        Assert.True(result!.Ok, result.Error);
        var saved = Assert.Single(bodies, b => b.Path == "/api/v3/notification").Body;
        Assert.Equal("Flexarr", saved["name"]!.GetValue<string>());
        Assert.True(saved["onDownload"]!.GetValue<bool>());
        Assert.True(saved["onSeriesDelete"]!.GetValue<bool>());
        Assert.False(saved["onGrab"]!.GetValue<bool>()); // Not a file change.
        Assert.False(saved["onRename"]!.GetValue<bool>()); // This version can't.
        var url = saved["fields"]!.AsArray().First(f => f!["name"]!.GetValue<string>() == "url")!["value"]!.GetValue<string>();
        Assert.Contains("/api/v1/webhooks/sonarr?secret=", url, StringComparison.Ordinal);
        Assert.StartsWith(result.Address!, url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_LeavesAnotherFlexarrsWebhookAlone()
    {
        using var server = new FlexarrApiFactory();
        var arr = new FakeArr();
        arr.Responses["GET //sonarr.test/api/v3/notification"] = """
            [{"id":3,"implementation":"Webhook","name":"Flexarr","onDownload":true,
              "fields":[{"name":"url","value":"http://other:8383/api/v1/webhooks/sonarr?secret=theirs"},{"name":"method","value":1}]}]
            """;
        arr.Responses["GET //sonarr.test/api/v3/notification/schema"] = """[{"implementation":"Webhook","name":"","onDownload":false,"fields":[]}]""";
        arr.Responses["POST //sonarr.test/api/v3/notification/test"] = "{}";
        arr.Responses["POST //sonarr.test/api/v3/notification"] = """{"id":8}""";
        string? saved = null;
        arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v3/notification")
            {
                saved = r.Content!.ReadAsStringAsync().Result;
            }
        };
        using var app = arr.Serve(server);

        var result = await (await Client(app).PostAsJsonAsync("/api/v1/arr/webhook", new { name = "Sonarr" })).Content.ReadFromJsonAsync<ArrWebhookDto>(Json);

        Assert.True(result!.Ok, result.Error);
        Assert.DoesNotContain(arr.Requests, r => r.StartsWith("PUT ", StringComparison.Ordinal));
        Assert.StartsWith("Flexarr (", JsonNode.Parse(saved!)!["name"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhook_SaysSoWhenTheAppCantReachFlexarr()
    {
        using var server = new FlexarrApiFactory();
        var arr = new FakeArr();
        arr.Responses["GET //radarr.test/api/v3/notification"] = "[]";
        arr.Responses["GET //radarr.test/api/v3/notification/schema"] = """[{"implementation":"Webhook","onDownload":false,"fields":[]}]""";
        // No answer to notification/test: every address fails.
        using var app = arr.Serve(server);

        var result = await (await Client(app).PostAsJsonAsync("/api/v1/arr/webhook", new { name = "Radarr" })).Content.ReadFromJsonAsync<ArrWebhookDto>(Json);

        Assert.False(result!.Ok);
        Assert.DoesNotContain(arr.Requests, r => r == "POST /api/v3/notification");
    }

    private static HttpClient Client(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }
}
