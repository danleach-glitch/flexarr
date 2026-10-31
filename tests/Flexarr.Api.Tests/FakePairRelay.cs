using System.Net;
using System.Text;
using System.Text.Json.Nodes;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for the relay's TV code meeting point (relay/api/pair.js).</summary>
public sealed class FakePairRelay : HttpMessageHandler
{
    /// <summary>Codes TVs are showing, with their public keys (base64).</summary>
    public Dictionary<string, string> Keys { get; } = [];

    /// <summary>Sealed invites left for each code.</summary>
    public Dictionary<string, JsonObject> Invites { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri!.Query);
        var code = query["code"] ?? "";
        if (request.Method == HttpMethod.Get)
        {
            return Keys.TryGetValue(code, out var key)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent($$"""{"publicKey":"{{key}}"}""", Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        if (request.Method == HttpMethod.Post && query["action"] == "invite")
        {
            if (!Keys.ContainsKey(code))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            if (Invites.ContainsKey(code))
            {
                return new HttpResponseMessage(HttpStatusCode.Conflict);
            }

            Invites[code] = (JsonObject)JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
            return new HttpResponseMessage(HttpStatusCode.Created);
        }

        return new HttpResponseMessage(HttpStatusCode.BadRequest);
    }
}
