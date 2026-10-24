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
public sealed record ProfileRequest(string Name, string? Color, string? Pin, bool? RemovePin, IReadOnlyList<Guid>? HiddenLibraryIds = null);

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

        group.MapPut("/{id}", async (string id, ProfileRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
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
                var caller = PinCaller(http, current);
                // Someone who may already manage the server could change the PIN anyway, so others' guesses don't hold them up.
                var (result, retryAfter) = await guard.CheckAsync(profile.Id, caller, () => profile.CheckPin(request.Pin), ct, mayManage: current.IsAdmin);
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

    /// <summary>Who is guessing a PIN, for <see cref="PinGuard"/>: the browser or device, else the Tailscale account, else the address.</summary>
    internal static string PinCaller(HttpContext http, CurrentProfile current) =>
        current.SignInId ?? http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>A 429 saying how long to wait, with Retry-After.</summary>
    internal static IResult TooManyPins(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = (int)Math.Ceiling(retryAfter.TotalSeconds);
        http.Response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var minutes = (int)Math.Ceiling(retryAfter.TotalMinutes);
        var when = seconds < 60 ? $"{seconds} second{(seconds == 1 ? "" : "s")}" : minutes <= 1 ? "a minute" : $"{minutes} minutes";
        return Results.Problem($"Too many wrong PINs. Try again in {when}.", statusCode: 429);
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
