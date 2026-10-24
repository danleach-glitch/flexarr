using Flexarr.Api.Authentication;

namespace Flexarr.Api.Security;

/// <summary>
/// Stops other websites using a visitor's browser to make changes here. The session cookie, the home network and a
/// Tailscale identity all come along with any request a browser makes, including ones a page on another site starts;
/// so a write signed in one of those ways must show it came from Flexarr's own pages: the web app's
/// <c>X-Requested-With</c> header (which another site can't add), <c>Sec-Fetch-Site: same-origin</c>, or an
/// <c>Origin</c> naming this server. Requests carrying a key (apps, *arr webhooks) are exempt: a page can't know the key.
/// Browsers always say where a cross-site write comes from, so requests with none of these markers are from apps
/// (the Apple TV on the home network, say) and pass.
/// </summary>
public static class RequestForgery
{
    public static IApplicationBuilder UseFlexarrRequestForgeryGuard(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        if (IsForged(http))
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Results.Problem("This request came from another website, so Flexarr didn't act on it.", statusCode: 403).ExecuteAsync(http);
            return;
        }

        await next(http);
    });

    public static bool IsForged(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        var request = http.Request;
        if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method))
        {
            return false;
        }

        if (http.User.FindFirst(FlexarrClaims.Method)?.Value is ApiKeyAuthenticationHandler.Methods.ApiKey or ApiKeyAuthenticationHandler.Methods.Device)
        {
            return false;
        }

        // Only a script on the same origin can add a custom header (anything else needs CORS, which Flexarr doesn't allow).
        if (request.Headers.ContainsKey(ApiKeyAuthenticationHandler.RequestedWithHeader))
        {
            return false;
        }

        if (request.Headers["Sec-Fetch-Site"].FirstOrDefault() is { } site)
        {
            return site is not ("same-origin" or "none");
        }

        if (request.Headers.Origin.FirstOrDefault() is { } origin)
        {
            return !Uri.TryCreate(origin, UriKind.Absolute, out var from)
                || !string.Equals(from.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }
}
