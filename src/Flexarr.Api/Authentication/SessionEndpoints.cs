using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        // The web app signs in by sending the API key once; it gets back an HttpOnly cookie that scripts can't read.
        api.MapPost("/session", (HttpContext http, IOptionsMonitor<FlexarrOptions> options) =>
        {
            // Only for the key itself: being on the home network or tailnet doesn't earn a cookie that works anywhere.
            if (!http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName) || string.IsNullOrEmpty(options.CurrentValue.ApiKey))
            {
                return Results.Problem("Send the API key to sign in.", statusCode: 400);
            }

            http.Response.Cookies.Append(ApiKeyAuthenticationHandler.SessionCookieName, ApiKeyAuthenticationHandler.SessionValue(options.CurrentValue.ApiKey), new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromDays(400),
            });
            return Results.NoContent();
        }).WithTags("System");

        return api;
    }
}
