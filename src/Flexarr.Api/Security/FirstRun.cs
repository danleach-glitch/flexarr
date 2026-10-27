using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

public sealed record SetupStatusDto(bool Open);

/// <summary>
/// Setting up a new server without its API key: until anyone has signed in, the first browser on the home network
/// becomes the owner (see <see cref="FlexarrOptions.SetupOpen"/>). Only from the home network, and only
/// addressed to one of this server's own names, so neither the internet nor a web page using your browser (DNS
/// rebinding) can claim it. Claiming closes it for good: one owner, then codes and the key as before.
/// </summary>
public sealed class FirstRun(IOptionsMonitor<FlexarrOptions> options)
{
    private static readonly Lock ClaimLock = new();

    /// <summary>Whether <paramref name="http"/> may set this server up.</summary>
    public bool OpenTo(HttpContext http) =>
        options.CurrentValue.SetupOpen && NetworkZones.Of(http) == NetworkZone.Local
        && NetworkZones.AddressedToKnownHost(http, options.CurrentValue.KnownHosts);

    /// <summary>Takes the one claim, if <paramref name="http"/> may and nobody has yet.</summary>
    public bool TryClaim(HttpContext http)
    {
        lock (ClaimLock)
        {
            if (!OpenTo(http))
            {
                return false;
            }

            Close(options.CurrentValue);
            return true;
        }
    }

    /// <summary>Any sign-in closes setup: the server has an owner now.</summary>
    public static void Close(FlexarrOptions current)
    {
        ArgumentNullException.ThrowIfNull(current);
        lock (ClaimLock)
        {
            if (!current.SetupOpen)
            {
                return;
            }

            current.SetupOpen = false;
            ServerConfigStore.Update(current.DataDirectory, config => config["setupOpen"] = false);
        }
    }
}

public static class FirstRunEndpoints
{
    /// <summary>
    /// Anonymous: a browser that isn't signed in asks whether it can set the server up instead, and a phone waiting
    /// for its code asks whether to say "set it up on a computer first".
    /// </summary>
    public static RouteGroupBuilder MapFirstRunEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/setup").WithTags("System").AllowAnonymous();
        group.MapGet("/", (HttpContext http, FirstRun firstRun) => Results.Ok(new SetupStatusDto(firstRun.OpenTo(http))));
        // Only the web app's own scripts send X-Requested-With, so a form on another site can't claim the server.
        group.MapPost("/claim", (HttpContext http, FirstRun firstRun, SignIns signIns) =>
        {
            if (!http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.RequestedWithHeader) || !firstRun.TryClaim(http))
            {
                return Results.Problem("This server is already set up. Sign in with a code from a device that's signed in.", statusCode: 403);
            }

            var (_, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZone.Local,
                http.Connection.RemoteIpAddress?.ToString());
            ApiKeyAuthenticationHandler.AppendSessionCookie(http, token);
            return Results.NoContent();
        });
        return api;
    }
}
