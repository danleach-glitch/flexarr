using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Webhooks;
using Microsoft.AspNetCore.Authorization;
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
            IOptionsMonitor<FlexarrOptions> options, IAuthorizationService authorization, CancellationToken ct) =>
            {
                var expected = options.CurrentValue.WebhookSecret;
                var withSecret = !string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(expected)
                    && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(expected));
                // Without the secret only an admin may send one: a webhook can point the scanner at any path.
                if (!withSecret)
                {
                    if (http.User.Identity?.IsAuthenticated != true)
                    {
                        return Results.Unauthorized();
                    }

                    if (!(await authorization.AuthorizeAsync(http.User, FlexarrClaims.AdminPolicy)).Succeeded)
                    {
                        return Results.Forbid();
                    }
                }

                if (!Enum.TryParse<ArrKind>(source, ignoreCase: true, out var kind))
                {
                    return Results.NotFound();
                }

                if (ArrWebhookTranslator.IsTest(payload))
                {
                    return Results.Ok();
                }

                var requests = ArrWebhookTranslator.ToScanRequests(kind, payload);
                if (requests.Any(r => PathComparison.HasControlCharacters(r.Path)))
                {
                    return Results.Problem("A path in the webhook holds a control, line-separator or text-direction character.", statusCode: 400);
                }

                var mappings = options.CurrentValue.PathMappings;
                foreach (var request in requests)
                {
                    await queue.EnqueueAsync(request with { Path = PathMapper.Map(request.Path, mappings) }, ct);
                }

                // What Coming soon and Downloads show has just changed: ask the *arr apps again rather than wait out the cache.
                UpcomingEndpoints.ForgetArrCalls();
                return Results.Accepted(value: new { queued = requests.Count });
            })
            .WithTags("Webhooks")
            .AllowAnonymous();

        return api;
    }
}
