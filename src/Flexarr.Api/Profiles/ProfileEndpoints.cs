using System.Text.RegularExpressions;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

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

        // Choosing a profile returns the token clients send as X-Flexarr-Profile.
        group.MapPost("/{id}/select", async (string id, SelectProfileRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } profile)
            {
                return Results.NotFound();
            }

            if (!profile.CheckPin(request.Pin))
            {
                // A short pause makes guessing a PIN slow.
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return Results.Problem("That PIN isn't right.", statusCode: 403);
            }

            return Results.Ok(new { token = current.TokenFor(profile.Id), profile = ToDto(profile, current) with { IsCurrent = true } });
        });

        return api;
    }

    /// <summary>Profile pictures are shown by &lt;img&gt; tags (the profile picker too), so they're served without the API key.</summary>
    public static RouteGroupBuilder MapProfileImageEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/profiles/{id}/image", (string id, ProfileImages images, HttpContext http) =>
        {
            var path = images.PathFor(id);
            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            // The URL carries a version, so a changed picture gets a new URL.
            http.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
            return Results.File(path, "image/jpeg");
        }).WithTags("Profiles").AllowAnonymous();

        return api;
    }

    internal static ProfileDto ToDto(Profile p, CurrentProfile current) => new(p.Id, p.Name, p.Color, p.PinHash is not null, p.IsMain, p.Id == current.Id, p.HiddenLibraryIds,
        p.ImageUpdatedAt is { } at ? $"/api/v1/profiles/{p.Id}/image?v={at.Ticks}" : null);

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
