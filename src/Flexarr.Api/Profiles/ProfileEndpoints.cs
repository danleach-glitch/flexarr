using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <param name="Image">The profile picture's URL, or null to show the initial on the colour.</param>
public sealed record ProfileDto(string Id, string Name, string Color, bool HasPin, bool IsMain, bool IsCurrent, IReadOnlyList<Guid> HiddenLibraryIds, string? Image);

/// <param name="HiddenLibraryIds">Libraries the profile can't see; null leaves them as they are.</param>
/// <param name="CurrentPin">Changing or removing the main profile's PIN takes the one it has now...</param>
/// <param name="ApiKey">...or, if that's forgotten, the server's API key.</param>
public sealed record ProfileRequest(string Name, string? Color, string? Pin, bool? RemovePin, IReadOnlyList<Guid>? HiddenLibraryIds = null, string? CurrentPin = null,
    string? ApiKey = null);

public sealed record SelectProfileRequest(string? Pin);

public static partial class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/profiles").WithTags("Profiles");

        group.MapGet("/", async (FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
            (await db.Profiles.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct)).Select(p => ToDto(p, current)).ToList());

        group.MapPost("/", async (ProfileRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            var profile = new Profile { Id = Profile.NewId(), Name = request.Name.Trim(), Color = request.Color ?? "#a9c2ec", HiddenLibraryIds = request.HiddenLibraryIds?.Distinct().ToList() ?? [] };
            profile.SetPin(request.Pin);
            db.Profiles.Add(profile);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/v1/profiles/{profile.Id}", ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPut("/{id}", async (string id, ProfileRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard, SignIns signIns,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            // The main profile's PIN guards showing the API key, so a script in the page mustn't be able to take it
            // off (or swap it) first: that takes the PIN it has now, or the key itself.
            var changesMainPin = profile.IsMain && profile.PinHash is not null && (request.RemovePin == true || !string.IsNullOrEmpty(request.Pin));
            if (changesMainPin && await ConfirmMainPinChangeAsync(request, profile, http, current, guard, options.CurrentValue.ApiKey, ct) is { } refused)
            {
                return refused;
            }

            profile.Name = request.Name.Trim();
            profile.Color = request.Color ?? profile.Color;
            if (request.HiddenLibraryIds is { } hidden)
            {
                profile.HiddenLibraryIds = hidden.Distinct().ToList();
            }

            if (request.RemovePin == true)
            {
                profile.SetPin(null);
            }
            else if (!string.IsNullOrEmpty(request.Pin))
            {
                profile.SetPin(request.Pin);
            }

            await db.SaveChangesAsync(ct);
            if (changesMainPin)
            {
                // Browsers that signed in with the old PIN have to give the new one.
                signIns.RevokePinVerifiedExcept(current.SignInId);
            }

            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapDelete("/{id}", async (string id, FlexarrDbContext db, ProfileImages images, CancellationToken ct) =>
        {
            if (id == PlaybackState.DefaultUser)
            {
                return Results.Problem("The main profile can't be removed.", statusCode: 409);
            }

            // Watch history goes with the profile (cascade).
            if (await db.Profiles.Where(p => p.Id == id).ExecuteDeleteAsync(ct) == 0)
            {
                return Results.NotFound();
            }

            images.Delete(id);
            return Results.NoContent();
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // The picture is the request body (any common image format).
        group.MapPut("/{id}/image", async (string id, HttpRequest http, FlexarrDbContext db, ProfileImages images, CurrentProfile current, CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            if (http.ContentLength is not > 0 || http.ContentLength > ProfileImages.MaxUploadBytes)
            {
                return Results.Problem("Pictures can be up to 15 MB.", statusCode: 413);
            }

            if (!await images.SaveAsync(id, http.Body, ct))
            {
                return Results.Problem("That file isn't a picture Flexarr can read. Try a JPEG or PNG.", statusCode: 400);
            }

            profile.ImageUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapDelete("/{id}/image", async (string id, FlexarrDbContext db, ProfileImages images, CurrentProfile current, CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            images.Delete(id);
            profile.ImageUpdatedAt = null;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Choosing a profile returns the token clients send as X-Flexarr-Profile. On a browser or device signed in
        // with its own token, the choice is also kept on the server, so leaving the header off doesn't undo it.
        group.MapPost("/{id}/select", async (string id, SelectProfileRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current,
            PinGuard guard, SignIns signIns, CancellationToken ct) =>
        {
            if (await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } profile)
            {
                return Results.NotFound();
            }

            // Without a PIN on the main profile, choosing it would make anyone the server's admin. A browser or device
            // the main profile signed in may (the household chose not to protect it); a Tailscale guest may not.
            if (profile.IsMain && profile.PinHash is null && !current.IsTrusted)
            {
                return Results.Problem("The main profile can only be chosen here once it has a PIN.", statusCode: 403);
            }

            if (profile.PinHash is not null)
            {
                var (callers, browser) = PinCaller(http, current);
                // Someone who may already manage the server could change the PIN anyway, so others' guesses don't hold them up.
                var (result, retryAfter) = await guard.CheckAsync(profile.Id, callers, () => profile.CheckPin(request.Pin), ct, mayManage: current.IsAdmin, browser);
                if (result == PinCheck.LockedOut)
                {
                    return TooManyPins(http, retryAfter);
                }

                if (result == PinCheck.Wrong)
                {
                    return Results.Problem("That PIN isn't right.", statusCode: 403);
                }
            }

            if (current.SignInId is { } signIn)
            {
                signIns.ChooseProfile(signIn, profile.IsMain ? null : profile.Id);
            }
            else if (profile.IsMain && profile.PinHash is not null
                && http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.Tailscale)
            {
                // Over Tailscale the identity alone doesn't manage the server. The PIN signs this browser in as the
                // main profile instead: an HttpOnly cookie of its own, listed in Settings, that can be signed out and
                // lapses when left unused. It only works with the Tailscale login that earned it, still linked to a
                // profile. (At home the main profile manages the server already.)
                var (_, token) = signIns.Issue(SignInKind.Browser, ApiKeyAuthenticationHandler.BrowserName(http.Request), NetworkZones.Of(http),
                    http.Connection.RemoteIpAddress?.ToString(), pinVerified: true, tailscaleLogin: http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value);
                ApiKeyAuthenticationHandler.AppendSessionCookie(http, token);
            }

            return Results.Ok(new { token = current.TokenFor(profile), profile = ToDto(profile, current) with { IsCurrent = true } });
        });

        return api;
    }

    /// <summary>
    /// Profile pictures are shown by &lt;img&gt; tags (the profile picker too) and the Apple TV, so they're served without
    /// signing in, but only at the signed URL the profile list gives out.
    /// </summary>
    public static RouteGroupBuilder MapProfileImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/profiles/{id}/image", (string id, string? v, string? s, ProfileImages images, HttpContext http, IOptionsMonitor<FlexarrOptions> options) =>
        {
            var path = images.PathFor(id);
            var signed = v is not null && s is not null && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(s), Encoding.UTF8.GetBytes(CurrentProfile.ImageSignature(options.CurrentValue.ApiKey, id, v)));
            if (!signed && http.User.Identity?.IsAuthenticated != true || !File.Exists(path))
            {
                return Results.NotFound();
            }

            // The URL carries a version, so a changed picture gets a new URL.
            http.Response.Headers.CacheControl = signed ? "private, max-age=31536000, immutable" : "private, no-cache";
            return Results.File(path, "image/jpeg");
        }).WithTags("Profiles").AllowAnonymous();

        return api;
    }

    /// <summary>A browser's own random id, so <see cref="PinGuard"/> can tell browsers apart that share an address.</summary>
    public const string BrowserCookieName = "flexarr.browser";

    /// <summary>
    /// Who is guessing a PIN, for <see cref="PinGuard"/>: the browser or device's sign-in, else the address, along with
    /// the browser's own id, which it's given now if it has none. Over Tailscale it's the login as well as the address:
    /// one person has several devices, each with two tailnet addresses, and none of them earns fresh attempts.
    /// </summary>
    internal static (IReadOnlyList<string> Callers, string? Browser) PinCaller(HttpContext http, CurrentProfile current)
    {
        if (current.SignInId is { } signIn)
        {
            return ([$"sign-in:{signIn}"], null);
        }

        var browser = http.Request.Cookies[BrowserCookieName];
        if (browser is not { Length: 32 } || !browser.All(char.IsAsciiHexDigitLower))
        {
            browser = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            http.Response.Cookies.Append(BrowserCookieName, browser, new CookieOptions
            {
                HttpOnly = true,
                Secure = http.Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                MaxAge = TimeSpan.FromDays(400),
            });
        }

        var address = $"address:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        return (http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value is { } login ? [$"tailscale:{login.ToLowerInvariant()}", address] : [address], browser);
    }

    /// <summary>Null when the request may change the main profile's PIN: it holds the API key, or gives the current PIN.</summary>
    private static async Task<IResult?> ConfirmMainPinChangeAsync(ProfileRequest request, Profile main, HttpContext http, CurrentProfile current, PinGuard guard,
        string? apiKey, CancellationToken ct)
    {
        if (http.User.FindFirst(FlexarrClaims.Method)?.Value == ApiKeyAuthenticationHandler.Methods.ApiKey
            || !string.IsNullOrEmpty(request.ApiKey) && !string.IsNullOrEmpty(apiKey)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(request.ApiKey), Encoding.UTF8.GetBytes(apiKey)))
        {
            return null;
        }

        if (!string.IsNullOrEmpty(request.ApiKey))
        {
            return Results.Problem("That isn't the right API key.", statusCode: 403);
        }

        if (string.IsNullOrEmpty(request.CurrentPin))
        {
            return Results.Problem("Enter the main profile's current PIN to change it.", statusCode: 403);
        }

        var (callers, browser) = PinCaller(http, current);
        var (result, retryAfter) = await guard.CheckAsync(main.Id, callers, () => main.CheckPin(request.CurrentPin), ct, mayManage: true, browser);
        return result switch
        {
            PinCheck.Right => null,
            PinCheck.LockedOut => TooManyPins(http, retryAfter),
            _ => Results.Problem("That isn't the main profile's current PIN.", statusCode: 403),
        };
    }

    /// <summary>A 429 saying how long to wait, with Retry-After.</summary>
    internal static IResult TooManyPins(HttpContext http, TimeSpan retryAfter) =>
        TryAgainLater(http, retryAfter, when => $"Too many wrong PINs. Try again in {when}.");

    /// <summary>
    /// A 429 whose message says how long to wait, with Retry-After and the same number of seconds as
    /// <c>retryAfterSeconds</c> in the body, so the web app can count down.
    /// </summary>
    internal static IResult TryAgainLater(HttpContext http, TimeSpan retryAfter, Func<string, string> message)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(message);
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var minutes = (int)Math.Ceiling(seconds / 60.0);
        var when = seconds < 60 ? $"{seconds} second{(seconds == 1 ? "" : "s")}" : minutes <= 1 ? "a minute" : $"{minutes} minutes";
        return Results.Problem(message(when), statusCode: 429, extensions: new Dictionary<string, object?> { ["retryAfterSeconds"] = seconds });
    }

    internal static ProfileDto ToDto(Profile p, CurrentProfile current) =>
        new(p.Id, p.Name, p.Color, p.PinHash is not null, p.IsMain, p.Id == current.Id, p.HiddenLibraryIds, current.ImageUrlFor(p));

    private static IResult? Validate(ProfileRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 60)
        {
            errors["name"] = ["Give the profile a name (up to 60 characters)."];
        }

        if (!string.IsNullOrEmpty(request.Pin) && (request.Pin.Length is < 4 or > 8 || !request.Pin.All(char.IsAsciiDigit)))
        {
            errors["pin"] = ["A PIN is 4 to 8 digits."];
        }

        if (request.Color is { } c && !HexColour().IsMatch(c))
        {
            errors["color"] = ["Colours look like #a9c2ec."];
        }

        return errors.Count > 0 ? Results.ValidationProblem(errors) : null;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexColour();
}
