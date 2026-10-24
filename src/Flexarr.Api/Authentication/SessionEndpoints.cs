using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Authentication;

public sealed record DeviceSignInRequest(string? DeviceName);

public sealed record SignInRename(string? Name);

/// <param name="IsThisOne">The browser or device asking.</param>
public sealed record SignInDto(string Id, SignInKind Kind, string Name, DateTime CreatedAt, DateTime LastUsedAt, NetworkZone Zone, string? Address,
    string ProfileId, string? ProfileName, bool IsThisOne);

public static class SessionEndpoints
{
    public static RouteGroupBuilder MapSessionEndpoints(this RouteGroupBuilder api)
    {
        // The web app signs in by sending the API key once; it gets back an HttpOnly cookie that scripts can't read,
        // holding a token of its own that can be signed out from Settings without affecting anything else.
        api.MapPost("/session", (HttpContext http, SignIns signIns, CurrentProfile profile) =>
        {
            // Only for the key itself: being on the home network or tailnet doesn't earn a cookie that works anywhere.
            if (!SignedInWithApiKey(http))
            {
                return Results.Problem("Send the API key to sign in.", statusCode: 400);
            }

            var (_, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZones.Of(http),
                http.Connection.RemoteIpAddress?.ToString(), Chosen(profile));
            ApiKeyAuthenticationHandler.AppendSessionCookie(http, token);
            return Results.NoContent();
        }).WithTags("System");

        // Signing out: forgets this browser's (or device's) token on the server, and the cookie in the browser.
        api.MapDelete("/session", (HttpContext http, SignIns signIns, CurrentProfile profile) =>
        {
            if (profile.SignInId is { } id)
            {
                signIns.Revoke(id);
            }

            http.Response.Cookies.Delete(ApiKeyAuthenticationHandler.SessionCookieName, new CookieOptions { Path = "/", SameSite = SameSiteMode.Strict, HttpOnly = true });
            return Results.NoContent();
        }).WithTags("System").AllowAnonymous();

        // An app holding the API key (an Apple TV set up before per-device sign-ins) swaps it for a token of its own.
        api.MapPost("/session/device", (DeviceSignInRequest request, HttpContext http, SignIns signIns, CurrentProfile profile) =>
        {
            if (!SignedInWithApiKey(http))
            {
                return Results.Problem("Send the API key to sign in.", statusCode: 400);
            }

            var (signIn, token) = signIns.Issue(SignInKind.Device, request.DeviceName ?? "", NetworkZones.Of(http),
                http.Connection.RemoteIpAddress?.ToString(), Chosen(profile));
            return Results.Ok(new { id = signIn.Id, token });
        }).WithTags("System");

        var group = api.MapGroup("/sign-ins").WithTags("System").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (SignIns signIns, CurrentProfile current, FlexarrDbContext db, CancellationToken ct) =>
        {
            var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            return signIns.List().Select(s =>
            {
                var profileId = s.ProfileId is { } chosen && names.ContainsKey(chosen) ? chosen : PlaybackState.DefaultUser;
                return new SignInDto(s.Id, s.Kind, s.Name, s.CreatedAt, s.LastUsedAt, s.Zone, s.Address, profileId, names.GetValueOrDefault(profileId), s.Id == current.SignInId);
            }).ToList();
        });

        group.MapDelete("/{id}", (string id, SignIns signIns) => signIns.Revoke(id) ? Results.NoContent() : Results.NotFound());

        group.MapPatch("/{id}", (string id, SignInRename rename, SignIns signIns) =>
            string.IsNullOrWhiteSpace(rename.Name) ? Results.Problem("Give it a name.", statusCode: 400)
            : signIns.Rename(id, rename.Name) ? Results.NoContent() : Results.NotFound());

        // Everything but the browser asking (which may have no sign-in of its own, at home or with the API key).
        group.MapPost("/sign-out-others", (SignIns signIns, CurrentProfile current) => Results.Ok(new { signedOut = signIns.RevokeAllExcept(current.SignInId) }));

        return api;
    }

    private static bool SignedInWithApiKey(HttpContext http) =>
        http.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
        && http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey;

    /// <summary>The profile already chosen on the browser or device (its X-Flexarr-Profile token), carried over to its sign-in.</summary>
    private static string? Chosen(CurrentProfile profile) => profile.Id == PlaybackState.DefaultUser ? null : profile.Id;
}
