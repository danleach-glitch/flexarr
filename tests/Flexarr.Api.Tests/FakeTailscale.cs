using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for Tailscale's API: records each call and answers like Tailscale does.</summary>
public sealed class FakeTailscale : HttpMessageHandler
{
    public sealed record ApiCall(HttpMethod Method, string Path, string? Authorization, string Body);

    public ConcurrentQueue<ApiCall> Calls { get; } = new();

    /// <summary>The tailnet policy GET returns.</summary>
    public JsonObject Policy { get; set; } = new();

    /// <summary>Works out what validation says about a policy; by default everything passes.</summary>
    public Func<JsonObject, string> Validate { get; set; } = _ => "{}";

    public bool AcceptClient { get; set; } = true;

    /// <summary>The scopes the token says the client has.</summary>
    public string Scope { get; set; } = "auth_keys policy_file:read";

    public List<string> DeletedKeys { get; } = [];

    /// <summary>Set to make every call go wrong the way a bad network does: "timeout", or "html" (a captive portal's page).</summary>
    public string? Broken { get; set; }

    private int _keys;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        var path = request.RequestUri!.AbsolutePath;
        Calls.Enqueue(new ApiCall(request.Method, path, request.Headers.Authorization?.ToString(), body));
        if (Broken == "timeout")
        {
            // What HttpClient throws when its own timeout runs out.
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
        }

        if (Broken == "html")
        {
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>Sign in to the Wi-Fi</html>", Encoding.UTF8, "text/html") };
        }

        if (path == "/api/v2/oauth/token")
        {
            return AcceptClient
                ? Json($$"""{"access_token":"tskey-api-test","token_type":"Bearer","expires_in":3600,"scope":"{{Scope}}"}""")
                : new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (request.Headers.Authorization?.Parameter != "tskey-api-test")
        {
            return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        }

        if (path == "/api/v2/tailnet/-/keys" && request.Method == HttpMethod.Post)
        {
            var n = Interlocked.Increment(ref _keys);
            return Json($$"""{"id":"k{{n}}","key":"tskey-auth-k{{n}}-secret","created":"2026-10-31T00:00:00Z","expires":"2026-11-03T00:00:00Z"}""");
        }

        if (path.StartsWith("/api/v2/tailnet/-/keys/", StringComparison.Ordinal) && request.Method == HttpMethod.Delete)
        {
            lock (DeletedKeys)
            {
                DeletedKeys.Add(path["/api/v2/tailnet/-/keys/".Length..]);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        if (path == "/api/v2/tailnet/-/acl" && request.Method == HttpMethod.Get)
        {
            return Json(Policy.ToJsonString());
        }

        if (path == "/api/v2/tailnet/-/acl/validate")
        {
            return Json(Validate((JsonObject)JsonNode.Parse(body)!));
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}
