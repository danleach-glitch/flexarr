using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

/// <summary>
/// Flexarr's sign-in, checked in this order:
/// <list type="number">
/// <item>An API key, as an <c>X-Api-Key</c> header or <c>apikey</c> query parameter (apps, *arr webhooks).</item>
/// <item>The web app's session cookie, which it's given in exchange for the API key so the browser never keeps the key.
/// Changes that arrive with it must also carry the <c>X-Requested-With</c> header, which another site can't add.</item>
/// <item>A Tailscale identity: <c>tailscale serve</c> adds the sender's verified login to requests from your tailnet.
/// Only trusted when the connection really came from the local Tailscale proxy, and mapped to the profile linked
/// to that Tailscale account.</item>
/// <item>The home network, when "only away from home" is chosen. Never applies to tunnelled traffic.</item>
/// </list>
/// </summary>
public sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> schemeOptions,
    IOptionsMonitor<FlexarrOptions> flexarrOptions,
    ILoggerFactory loggerFactory,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(schemeOptions, loggerFactory, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string QueryName = "apikey";
    public const string TailscaleLoginHeader = "Tailscale-User-Login";
    public const string SessionCookieName = "flexarr.session";
    public const string RequestedWithHeader = "X-Requested-With";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var options = flexarrOptions.CurrentValue;

        string? provided = Request.Headers[HeaderName].FirstOrDefault() ?? Request.Query[QueryName].FirstOrDefault();
        if (!string.IsNullOrEmpty(provided))
        {
            var matches = CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(options.ApiKey ?? string.Empty));
            return matches ? Success("api-key") : AuthenticateResult.Fail("Invalid API key");
        }

        // A stale cookie (from before the key changed) is ignored rather than refused, so the other ways still apply.
        if (Request.Cookies[SessionCookieName] is { Length: > 0 } session && !string.IsNullOrEmpty(options.ApiKey)
            && (HttpMethods.IsGet(Request.Method) || HttpMethods.IsHead(Request.Method) || Request.Headers.ContainsKey(RequestedWithHeader))
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(session), Encoding.UTF8.GetBytes(SessionValue(options.ApiKey))))
        {
            return Success("api-key");
        }

        if (TrustedTailscaleLogin(Context) is { } login)
        {
            var db = Context.RequestServices.GetRequiredService<FlexarrDbContext>();
            var profile = await db.Profiles.AsNoTracking().Where(p => p.TailscaleLogin == login).Select(p => p.Id).FirstOrDefaultAsync(Context.RequestAborted);
            return profile is null
                ? AuthenticateResult.Fail($"The Tailscale account {login} isn't linked to a Flexarr profile")
                : Success("tailscale", profile, login);
        }

        if (options.AuthenticationRequired == AuthenticationRequired.DisabledForLocalAddresses && NetworkZones.Of(Context) == NetworkZone.Local)
        {
            return Success("local");
        }

        return AuthenticateResult.NoResult();
    }

    /// <summary>The session cookie's value: derived from the API key, so changing the key signs every browser out.</summary>
    public static string SessionValue(string apiKey) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey), "flexarr web session"u8));

    /// <summary>
    /// The Tailscale login on a request, if it can be believed: it came over a loopback connection (from
    /// <c>tailscale serve</c> on this machine) for a client on the tailnet, and not through Funnel. A device on the LAN
    /// connecting directly can't set it, because its connection isn't loopback.
    /// </summary>
    public static string? TrustedTailscaleLogin(HttpContext http)
    {
        var login = http.Request.Headers[TailscaleLoginHeader].FirstOrDefault();
        var peer = RequestPeer.Of(http);
        return !string.IsNullOrWhiteSpace(login)
            && peer is not null && IPAddress.IsLoopback(peer.IsIPv4MappedToIPv6 ? peer.MapToIPv4() : peer)
            && NetworkZones.Of(http) == NetworkZone.Tailnet
            ? login.Trim()
            : null;
    }

    private static AuthenticateResult Success(string method, string? profile = null, string? login = null)
    {
        var claims = new List<Claim> { new(FlexarrClaims.Method, method) };
        if (profile is not null)
        {
            claims.Add(new Claim(FlexarrClaims.Profile, profile));
        }

        if (login is not null)
        {
            claims.Add(new Claim(FlexarrClaims.TailscaleLogin, login));
        }

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName)), SchemeName));
    }
}
