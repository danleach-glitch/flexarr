using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Flexarr.Api.Trakt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// Stands in for Trakt (at http://trakt.test) and Flexarr's relay (at http://relay.test/api/trakt): answers by method
/// and path, and records each request with its body. Unknown requests are a 404.
/// </summary>
public sealed class FakeTrakt : HttpMessageHandler
{
    public const string Relay = "http://relay.test/api/trakt";

    public sealed record Answer(HttpStatusCode Status, string Body = "{}", int? PageCount = null);

    /// <summary>Answers by "METHOD /path" (the query left off); a queue answers in turn, its last answer staying.</summary>
    public ConcurrentDictionary<string, ConcurrentQueue<Answer>> Answers { get; } = new();

    public ConcurrentQueue<(string Key, string Query, string Body, string? Token)> Requests { get; } = new();

    public FakeTrakt()
    {
        Set("GET /api/trakt", """{"clientId":"flexarr-id"}""");
        Set("POST /oauth/device/code", """{"device_code":"devicecode0123456789","user_code":"ABCD1234","verification_url":"https://trakt.tv/activate","expires_in":600,"interval":0}""");
        Set("GET /users/settings", """{"user":{"username":"Dan","ids":{"slug":"dan"}}}""");
        Set("GET /sync/watched/movies", "[]");
        Set("GET /sync/watched/shows", "[]");
        Set("GET /sync/history", "[]");
        Set("GET /sync/playback", "[]");
        Set("GET /sync/watchlist", "[]");
        Set("POST /sync/history", "{}");
        Set("POST /sync/watchlist", "{}");
        Set("POST /sync/watchlist/remove", "{}");
        Set("POST /scrobble/start", "{}");
        Set("POST /scrobble/pause", "{}");
        Set("POST /scrobble/stop", "{}");
        Set("POST /oauth/revoke", "{}");
    }

    public void Set(string key, string body, HttpStatusCode status = HttpStatusCode.OK) =>
        Answers[key] = new ConcurrentQueue<Answer>([new Answer(status, body)]);

    /// <summary>Answers in turn: each request takes the next, and the last keeps answering.</summary>
    public void Then(string key, params Answer[] answers) => Answers[key] = new ConcurrentQueue<Answer>(answers);

    /// <summary>Token calls the relay passed on: the grant, by request body.</summary>
    public IEnumerable<string> RelayBodies => Requests.Where(r => r.Key == "POST /api/trakt").Select(r => r.Body);

    public IEnumerable<string> Bodies(string key) => Requests.Where(r => r.Key == key).Select(r => r.Body);

    public int Count(string key) => Requests.Count(r => r.Key == key);

    public static string Token(string access = "access-token-0123456789", string refresh = "refresh-token-0123456789", int expiresIn = 86_400) =>
        $$"""{"access_token":"{{access}}","refresh_token":"{{refresh}}","expires_in":{{expiresIn}},"created_at":{{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}}}""";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var key = $"{request.Method} {request.RequestUri!.AbsolutePath}";
        Requests.Enqueue((key, request.RequestUri.Query, body, request.Headers.Authorization?.Parameter));

        // The relay's token calls are told apart by their grant.
        if (key == "POST /api/trakt" && body.Contains("\"grant\"", StringComparison.Ordinal))
        {
            var grant = body.Contains("device_token", StringComparison.Ordinal) ? "device" : body.Contains("refresh_token", StringComparison.Ordinal) ? "refresh" : "revoke";
            key = $"RELAY {grant}";
        }

        if (!Answers.TryGetValue(key, out var queue) || !queue.TryPeek(out var answer))
        {
            return new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("{}") };
        }

        if (queue.Count > 1)
        {
            queue.TryDequeue(out _);
        }

        var response = new HttpResponseMessage(answer.Status) { Content = new StringContent(answer.Body, Encoding.UTF8, "application/json") };
        if (answer.PageCount is { } pages)
        {
            response.Headers.Add("X-Pagination-Page-Count", pages.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return response;
    }

    /// <summary>The test server, with Trakt answered by this.</summary>
    public WebApplicationFactory<Program> Serve(WebApplicationFactory<Program> factory) => factory.WithWebHostBuilder(builder =>
    {
        builder.UseSetting("Flexarr:Trakt:RelayUrl", Relay);
        builder.UseSetting("Flexarr:Trakt:ApiUrl", "http://trakt.test");
        builder.ConfigureTestServices(services => services.AddHttpClient<TraktClient>().ConfigurePrimaryHttpMessageHandler(() => this));
    });
}
