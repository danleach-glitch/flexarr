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
    /// <c>http://flexarr:8383/api/v1/webhooks/{sonarr|radarr|lidarr}?apikey=...</c>.
    /// </summary>
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/{source}", async (string source, ArrWebhookPayload payload, ILibraryScanQueue queue, IOptions<FlexarrOptions> options, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ArrKind>(source, ignoreCase: true, out var kind))
                {
                    return Results.NotFound();
                }

                if (ArrWebhookTranslator.IsTest(payload))
                {
                    return Results.Ok();
                }

                var mappings = options.Value.PathMappings;
                var requests = ArrWebhookTranslator.ToScanRequests(kind, payload)
                    .Select(r => r with { Path = PathMapper.Map(r.Path, mappings) })
                    .ToList();
                foreach (var request in requests)
                {
                    await queue.EnqueueAsync(request, ct);
                }

                return Results.Accepted(value: new { queued = requests.Count });
            })
            .WithTags("Webhooks");

        return api;
    }
}
