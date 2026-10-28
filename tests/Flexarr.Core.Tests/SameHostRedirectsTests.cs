using System.Net;
using Flexarr.Integrations.Arr;

namespace Flexarr.Core.Tests;

public class SameHostRedirectsTests
{
    [Fact]
    public async Task ARedirect_IsFollowedOnTheSameHost_WithTheKey()
    {
        var server = new Answers(new()
        {
            ["http://sonarr.lan/api/v3/series"] = Redirect("https://sonarr.lan/api/v3/series"),
            ["https://sonarr.lan/api/v3/series"] = new HttpResponseMessage(HttpStatusCode.OK),
        });
        using var client = new HttpClient(new SameHostRedirects { InnerHandler = server });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://sonarr.lan/api/v3/series");
        request.Headers.Add("X-Api-Key", "secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("secret", server.KeysSent["https://sonarr.lan/api/v3/series"]);
    }

    [Fact]
    public async Task ARedirect_ToAnotherHost_IsNotFollowed_SoTheKeyStaysHome()
    {
        var server = new Answers(new() { ["http://sonarr.lan/api/v3/series"] = Redirect("https://elsewhere.example/steal") });
        using var client = new HttpClient(new SameHostRedirects { InnerHandler = server });
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://sonarr.lan/api/v3/series");
        request.Headers.Add("X-Api-Key", "secret");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.DoesNotContain("https://elsewhere.example/steal", server.KeysSent.Keys);
    }

    private static HttpResponseMessage Redirect(string to)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(to);
        return response;
    }

    private sealed class Answers(Dictionary<string, HttpResponseMessage> answers) : HttpMessageHandler
    {
        public Dictionary<string, string?> KeysSent { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            KeysSent[url] = request.Headers.TryGetValues("X-Api-Key", out var keys) ? keys.Single() : null;
            return Task.FromResult(answers.TryGetValue(url, out var answer) ? answer : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
