using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Webhooks;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public static class WebhookEndpoints
{
    /// <summary>
    /// Point a Sonarr/Radarr/Lidarr "Webhook" connection at
    /// <c>http://flexarr:8383/api/v1/webhooks/{sonarr|radarr|lidarr}?secret=...</c>, with the webhook secret from
    /// Settings › Server. It can only queue scans, so the API key needn't sit in the *arr apps' settings. Webhooks set up
    /// with <c>?apikey=</c> keep working.
    /// </summary>
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/{source}", async (string source, string? secret, ArrWebhookPayload payload, HttpContext http, ILibraryScanQueue queue,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
            {
                var expected = options.CurrentValue.WebhookSecret;
                var withSecret = !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(expected)
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected));
                if (!withSecret && http.User.Identity?.IsAuthenticated != true)
                {
                    return Results.Unauthorized();
                }

                if (!Enum.TryParse<ArrKind>(source, ignoreCase: true, out var kind))
                {
                    return Results.NotFound();
                }

                if (ArrWebhookTranslator.IsTest(payload))
                {
                    return Results.Ok();
                }

                var mappings = options.CurrentValue.PathMappings;
                var requests = ArrWebhookTranslator.ToScanRequests(kind, payload)
                    .Select(r => r with { Path = PathMapper.Map(r.Path, mappings) })
                    .ToList();
                foreach (var request in requests)
                {
                    await queue.EnqueueAsync(request, ct);
                }

                return Results.Accepted(value: new { queued = requests.Count });
            })
            .WithTags("Webhooks")
            .AllowAnonymous();

        return api;
    }
}
