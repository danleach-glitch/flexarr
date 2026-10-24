using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Webhooks;

namespace Flexarr.Api.Endpoints;

public static class WebhookEndpoints
{
    /// <summary>
    /// Point a Sonarr/Radarr/Lidarr "Webhook" connection at
    /// <c>http://flexarr:8383/api/v1/webhooks/{sonarr|radarr|lidarr}?apikey=...</c>.
    /// </summary>
    public static RouteGroupBuilder MapWebhookEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/webhooks/{source}", async (string source, ArrWebhookPayload payload, ILibraryScanQueue queue, CancellationToken ct) =>
            {
                if (!Enum.TryParse<ArrKind>(source, ignoreCase: true, out var kind))
                {
                    return Results.NotFound();
                }

                if (ArrWebhookTranslator.IsTest(payload))
                {
                    return Results.Ok();
                }

                var requests = ArrWebhookTranslator.ToScanRequests(kind, payload);
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
