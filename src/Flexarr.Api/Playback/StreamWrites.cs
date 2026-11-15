using Flexarr.Api.Security;

namespace Flexarr.Api.Playback;

/// <summary>
/// Who may change a playback or a live channel through its address (report progress, pick subtitles, stop it, keep it
/// going). Reading the stream needs only the address, since players can't add headers; changing it also takes being
/// signed in, as the profile it plays for, on the sign-in that started it, or on one the household trusts. Every
/// Flexarr app and the web app send their sign-in with these calls.
/// </summary>
public static class StreamWrites
{
    /// <summary>Null when the request may change it; otherwise the answer to give (401 signed out, 404 someone else's).</summary>
    public static IResult? Refusal(HttpContext http, string profileId, string? signInId)
    {
        ArgumentNullException.ThrowIfNull(http);
        var user = http.User;
        if (user.Identity?.IsAuthenticated != true)
        {
            return Results.Unauthorized();
        }

        if (user.HasClaim(FlexarrClaims.Trusted, "true")
            || user.FindFirst(FlexarrClaims.Profile)?.Value == profileId
            || signInId is not null && user.FindFirst(FlexarrClaims.SignIn)?.Value == signInId)
        {
            return null;
        }

        // Someone else's, as far as this caller can tell: not found, as for an address that was never handed out.
        return Results.NotFound();
    }
}
