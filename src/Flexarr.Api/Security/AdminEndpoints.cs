using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>The main profile's PIN, or (if it's forgotten) the server's API key.</summary>
public sealed record AdminUnlockRequest(string? Pin, string? ApiKey = null);

/// <param name="Pin">The main profile's new PIN, 4 to 8 digits.</param>
/// <param name="ApiKey">Needed when the main profile has a PIN already (it's been forgotten).</param>
public sealed record AdminPinRequest(string Pin, string? ApiKey = null);

/// <param name="UnlockedUntil">When managing the server takes the PIN again; null for the API key, which always may.</param>
/// <param name="Token">After a new PIN, the main profile's new <c>X-Flexarr-Profile</c> token (the old one stops working).</param>
public sealed record AdminUnlockDto(DateTime? UnlockedUntil, string? Token = null);

/// <summary>
/// Unlocking the server for managing (see <see cref="AdminUnlocks"/>), locking it again, and setting the main profile's
/// PIN when there isn't one yet or it's been forgotten.
/// </summary>
public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/admin").WithTags("Profiles");

        group.MapPost("/unlock", async (AdminUnlockRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard, SignIns signIns,
            AdminUnlocks unlocks, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (current.IsAdmin && current.SignInId is null)
            {
                return Results.Ok(new AdminUnlockDto(null)); // the API key
            }

            if (!current.IsAdmin && !current.CanUnlock)
            {
                return Results.Problem("Choose the main profile to manage the server.", statusCode: 403);
            }

            if (await db.Profiles.FindAsync([PlaybackState.DefaultUser], ct) is not { PinHash: not null } main)
            {
                return PinMissing();
            }

            if (!string.IsNullOrEmpty(request.ApiKey))
            {
                if (!IsApiKey(request.ApiKey, options.CurrentValue.ApiKey))
                {
                    return Results.Problem("That isn't the right API key.", statusCode: 403);
                }
            }
            else
            {
                // A locked main profile doesn't count as already managing the server, so it gets the stricter count.
                var (callers, browser) = ProfileEndpoints.PinCaller(http, current);
                var (result, retryAfter) = await guard.CheckAsync(main.Id, callers, () => main.CheckPin(request.Pin), ct, mayManage: current.IsAdmin, browser);
                if (result == PinCheck.LockedOut)
                {
                    return ProfileEndpoints.TooManyPins(http, retryAfter);
                }

                if (result == PinCheck.Wrong)
                {
                    return Results.Problem("That PIN isn't right.", statusCode: 403);
                }
            }

            var signInId = current.SignInId ?? SignInForUnlock(http, signIns);
            signIns.ChooseProfile(signInId, null); // a sign-in whose profile was removed is back on the main profile
            return Results.Ok(new AdminUnlockDto(unlocks.Unlock(signInId)));
        });

        group.MapPost("/lock", (CurrentProfile current, AdminUnlocks unlocks) =>
        {
            unlocks.Lock(current.SignInId);
            return Results.NoContent();
        });

        // The first PIN, on a server set up before setup asked for one, may be set by the main profile on a browser or
        // device the owner signed in. Once there is one, a new one takes the API key: that's the way back from a
        // forgotten PIN. (Changing it knowing the old one is in Settings › Profiles.)
        group.MapPost("/pin", async (AdminPinRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, SignIns signIns, AdminUnlocks unlocks,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (!IsPin(request.Pin))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["pin"] = ["A PIN is 4 to 8 digits."] });
            }

            if (await db.Profiles.FindAsync([PlaybackState.DefaultUser], ct) is not { } main)
            {
                return Results.NotFound();
            }

            var withKey = http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey;
            if (!withKey && !string.IsNullOrEmpty(request.ApiKey))
            {
                if (!IsApiKey(request.ApiKey, options.CurrentValue.ApiKey))
                {
                    return Results.Problem("That isn't the right API key.", statusCode: 403);
                }

                withKey = true;
            }

            // A first PIN takes a browser or device the owner signed in: a Tailscale identity alone can be posed as by
            // another program on this machine, and the home-network rule lets in anyone at home.
            var first = main.PinHash is null && current.Id == PlaybackState.DefaultUser && current.SignInId is not null && current.IsTrusted;
            if (!withKey && !first)
            {
                return Results.Problem(main.PinHash is null
                    ? "Give the main profile a PIN from a browser or device you signed in, or with the API key."
                    : "Enter the server's API key to set a new PIN. It's in config.json in the server's data folder.", statusCode: 403);
            }

            var replacing = main.PinHash is not null;
            main.SetPin(request.Pin);
            await db.SaveChangesAsync(ct);
            if (replacing)
            {
                // Whoever knew the old one starts again.
                signIns.RevokePinVerifiedExcept(current.SignInId);
                unlocks.LockAllExcept(current.SignInId);
            }

            if (http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey)
            {
                return Results.Ok(new AdminUnlockDto(null, current.TokenFor(main)));
            }

            var signInId = current.SignInId ?? SignInForUnlock(http, signIns);
            signIns.ChooseProfile(signInId, null);
            return Results.Ok(new AdminUnlockDto(unlocks.Unlock(signInId), current.TokenFor(main)));
        });

        return api;
    }

    /// <summary>
    /// A request without a sign-in of its own (over Tailscale, or at home with "only away from home") that gave the
    /// main profile's PIN: signs the browser in as the main profile, so the unlock has somewhere to live. It only works
    /// the way it was earned (with that Tailscale login, or from home while home signs people in; see
    /// <see cref="ApiKeyAuthenticationHandler"/>), and is signed out sooner when left unused, or when the PIN changes.
    /// </summary>
    internal static string SignInForUnlock(HttpContext http, SignIns signIns)
    {
        var login = http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value;
        var overTailscale = http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.Tailscale && login is not null;
        var (signIn, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZones.Of(http),
            http.Connection.RemoteIpAddress?.ToString(), pinVerified: true, tailscaleLogin: overTailscale ? login : null);
        ApiKeyAuthenticationHandler.AppendSessionCookie(http, token);
        return signIn.Id;
    }

    internal static bool IsPin(string? pin) => pin is { Length: >= 4 and <= 8 } && pin.All(char.IsAsciiDigit);

    /// <summary>409 with <c>pinMissing</c>, so apps know to offer setting one rather than asking for it.</summary>
    internal static IResult PinMissing() => Results.Problem("The main profile needs a PIN before the server's settings can be unlocked.", statusCode: 409,
        extensions: new Dictionary<string, object?> { ["pinMissing"] = true });

    private static bool IsApiKey(string provided, string? key) =>
        !string.IsNullOrEmpty(key) && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(key));
}
