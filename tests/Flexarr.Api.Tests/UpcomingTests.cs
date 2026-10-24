using System.Net;
using Flexarr.Api.Endpoints;

namespace Flexarr.Api.Tests;

public class UpcomingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task AQueueFetchUnderWayWhenAWebhookArrives_IsNotCached()
    {
        var arr = new FakeArr();
        var sonarrQueue = 0;
        arr.OnRequest = request =>
        {
            if (request.RequestUri!.Host == "sonarr.test" && request.RequestUri.AbsolutePath == "/api/v3/queue" && Interlocked.Increment(ref sonarrQueue) == 1)
            {
                UpcomingEndpoints.ForgetArrCalls(); // A download finishes while Sonarr is answering.
            }
        };
        using var app = arr.Serve(factory);
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/downloads", UriKind.Relative))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("/api/v1/downloads", UriKind.Relative))).StatusCode);

        Assert.Equal(2, sonarrQueue); // Asked again: the first answer may predate the download.
    }
}
