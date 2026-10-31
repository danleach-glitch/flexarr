using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <param name="Image">The profile picture's URL, or null to show the initial on the colour.</param>
/// <param name="RequestShowsLibraryId">The library this profile's show requests go to; null, Seerr's default.</param>
/// <param name="RequestFilmsLibraryId">The library its film requests go to; null, Seerr's default.</param>
/// <param name="LibraryOrder">The order of the libraries in the menu (ids, and "live-tv"); empty, by name.</param>
/// <param name="AsksPin">
/// Choosing it here asks for a PIN (absent from servers before 0.56: there, HasPin). For the main profile, that's its
/// own PIN, or where the owner didn't sign in (a Tailscale guest), the admin PIN: see <paramref name="AsksAdminPin"/>.
/// </param>
/// <param name="AutoRequestWatchlist">Titles it adds to its watchlist that aren't in the library are requested (absent from servers before 0.56).</param>
/// <param name="MaxRatingAge">Its age limit, on the UK's steps (0 is U, 8 PG, then 12, 15, 18); null, none. Absent from servers before 0.56.</param>
/// <param name="IsKids">A child's profile: the simpler app, and leaving it takes the admin PIN. Absent from servers before 0.56.</param>
/// <param name="AsksAdminPin">The PIN choosing it here asks for is the admin PIN, not its own. Absent from servers before 0.57.</param>
public sealed record ProfileDto(string Id, string Name, string Color, bool HasPin, bool IsMain, bool IsCurrent, IReadOnlyList<Guid> HiddenLibraryIds, string? Image,
    Guid? RequestShowsLibraryId = null, Guid? RequestFilmsLibraryId = null, IReadOnlyList<string>? LibraryOrder = null, bool AsksPin = false,
    bool AutoRequestWatchlist = false, int? MaxRatingAge = null, bool IsKids = false, bool AsksAdminPin = false);

/// <param name="MaxRatingAge">The age limit: 0 (U), 8 (PG), 12, 15 or 18; null for none.</param>
/// <param name="IsKids">A child's profile; it needs an age limit.</param>
public sealed record AgeLimitRequest(int? MaxRatingAge, bool IsKids = false);

/// <param name="Pin">The admin PIN.</param>
/// <param name="ProfileId">Locking: the kids profile to choose here (else the one chosen already, or the first).</param>
public sealed record KidsLockRequest(string? Pin, string? ProfileId = null);

/// <summary>The order of a profile's libraries in the menu: library ids, and "live-tv"; empty goes back to by name.</summary>
public sealed record LibraryOrderRequest(IReadOnlyList<string> Order);

/// <summary>Where a profile's requests go. Both are set each time; null is wherever Seerr adds them by default.</summary>
public sealed record RequestLibrariesRequest(Guid? ShowsLibraryId, Guid? FilmsLibraryId);

/// <summary>Whether a profile's watchlist requests what it adds. Only the admin chooses, since it sets Sonarr and Radarr downloading.</summary>
public sealed record WatchlistRequestsRequest(bool AutoRequest);

/// <param name="HiddenLibraryIds">Libraries the profile can't see; null leaves them as they are.</param>
/// <param name="CurrentPin">No longer needed: the main profile's PIN is its own, and the admin PIN changes through /admin/pin.</param>
/// <param name="ApiKey">No longer needed, as <paramref name="CurrentPin"/>.</param>
/// <param name="MaxRatingAge">Making a profile: its age limit (see <see cref="AgeLimitRequest"/>). Changing one goes through /age-limit.</param>
/// <param name="IsKids">Making a profile: a child's.</param>
public sealed record ProfileRequest(string Name, string? Color, string? Pin, bool? RemovePin, IReadOnlyList<Guid>? HiddenLibraryIds = null, string? CurrentPin = null,
    string? ApiKey = null, int? MaxRatingAge = null, bool IsKids = false);

/// <param name="Pin">The chosen profile's PIN, if it asks for one.</param>
/// <param name="ParentPin">Leaving a kids profile for one that isn't: the admin PIN (choosing the main profile, <paramref name="Pin"/> does).</param>
public sealed record SelectProfileRequest(string? Pin, string? ParentPin = null);

/// <param name="AdminPin">The admin PIN, which takes a forgotten PIN off any profile.</param>
public sealed record ForgotPinRequest(string? AdminPin);

public static partial class ProfileEndpoints
{
    public static RouteGroupBuilder MapProfileEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/profiles").WithTags("Profiles");

        // A browser or device locked to kids profiles only offers them.
        group.MapGet("/", async (FlexarrDbContext db, CurrentProfile current, SignIns signIns, CancellationToken ct) =>
        {
            var locked = signIns.ById(current.SignInId)?.KidsLock == true;
            // A family guest sees only the profile its invite gave it.
            var guestProfile = current.IsGuest ? current.Id : null;
            return (await db.Profiles.AsNoTracking().Where(p => (!locked || p.IsKids) && (guestProfile == null || p.Id == guestProfile)).OrderBy(p => p.CreatedAt).ToListAsync(ct))
                .Select(p => ToDto(p, current)).ToList();
        });

        group.MapPost("/", async (ProfileRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (Validate(request) is { } problem)
            {
                return problem;
            }

            if (await ValidateAgeLimitAsync(new AgeLimitRequest(request.MaxRatingAge, request.IsKids), isMain: false, db, ct) is { } ageProblem)
            {
                return ageProblem;
            }

            var profile = new Profile
            {
                Id = Profile.NewId(), Name = request.Name.Trim(), Color = request.Color ?? "#a9c2ec", HiddenLibraryIds = request.HiddenLibraryIds?.Distinct().ToList() ?? [],
                MaxRatingAge = request.MaxRatingAge, IsKids = request.IsKids,
            };
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

            // Before 0.57 this changed the admin PIN (taking the current one or the key). A caller still doing that
            // mustn't quietly change the main profile's own PIN instead.
            if (profile.IsMain && (!string.IsNullOrEmpty(request.CurrentPin) || !string.IsNullOrEmpty(request.ApiKey)))
            {
                return Results.Problem("The admin PIN is changed with POST /admin/pin now (Settings › Security). The main profile's PIN here is its own.",
                    statusCode: 409, extensions: new Dictionary<string, object?> { ["adminPinMoved"] = true });
            }

            profile.Name = request.Name.Trim();
            profile.Color = request.Color ?? profile.Color;
            if (request.HiddenLibraryIds is { } hidden)
            {
                profile.HiddenLibraryIds = hidden.Distinct().ToList();
                LibraryVisibility.KidsSettingsChanged();
            }

            if (request.RemovePin == true)
            {
                profile.SetPin(null);
            }
            else if (!string.IsNullOrEmpty(request.Pin))
            {
                profile.SetPin(request.Pin);
            }

            // The main profile's PIN is its own, like anyone's: the admin PIN is apart (see /admin/pin).
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapPut("/{id}/age-limit", async (string id, AgeLimitRequest request, FlexarrDbContext db, CurrentProfile current, SignIns signIns,
            Trakt.TraktClient trakt, Trakt.TraktConnector connector, CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            if (await ValidateAgeLimitAsync(request, profile.IsMain, db, ct) is { } problem)
            {
                return problem;
            }

            if (profile.IsKids && !request.IsKids && await KeepLockedDevicesAsync(id, db, signIns, ct) is { } refused)
            {
                return refused;
            }

            // Kids profiles have no Trakt: one made a kids profile is disconnected, as it can't reach Disconnect itself.
            if (request.IsKids && !profile.IsKids)
            {
                await Trakt.TraktConnections.DisconnectAsync(db, trakt, connector, id, ct);
            }

            (profile.MaxRatingAge, profile.IsKids) = (request.MaxRatingAge, request.IsKids);
            await db.SaveChangesAsync(ct);
            LibraryVisibility.KidsSettingsChanged();
            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Locks this browser or device to kids profiles, choosing one: the playroom's TV, a child's tablet.
        group.MapPost("/kids-lock", async (KidsLockRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard, SignIns signIns,
            AdminUnlocks unlocks, CancellationToken ct) =>
        {
            if (current.IsGuest)
            {
                return Results.Problem("A guest's device stays on the profile it was invited to.", statusCode: 403);
            }

            if (current.SignInId is not { } signInId)
            {
                return Results.Problem("Child lock needs a browser or device that's signed in.", statusCode: 409);
            }

            var kids = await db.Profiles.AsNoTracking().Where(p => p.IsKids).OrderBy(p => p.CreatedAt).ToListAsync(ct);
            var chosen = request.ProfileId is { } wanted ? kids.FirstOrDefault(p => p.Id == wanted) : kids.FirstOrDefault(p => p.Id == current.Id) ?? kids.FirstOrDefault();
            if (chosen is null)
            {
                return request.ProfileId is null ? Results.Problem("Add a kids profile first.", statusCode: 409) : Results.NotFound();
            }

            // Locked already, moving to another kids profile goes through Who's watching, which asks for whatever PIN it takes.
            var alreadyLocked = signIns.ById(signInId)?.KidsLock == true;
            if (alreadyLocked && chosen.Id != current.Id)
            {
                return Results.Problem($"Choose {chosen.Name} in Who’s watching.", statusCode: 409);
            }

            // The main profile turns it on without its PIN (turning it off takes it); anyone else needs it, so nobody
            // can lock the household's TV to kids profiles behind the parent's back.
            if (!alreadyLocked && !current.IsMainProfile && await ParentPinAsync(http, db, current, guard, request.Pin, ct) is { } refused)
            {
                return refused;
            }

            signIns.SetKidsLock(signInId, true, chosen.Id);
            unlocks.Lock(signInId);
            // A phone handed down keeps telling of new titles for the profile it was set up as: it hears the child's now.
            await db.NotificationDevices.Where(d => d.SignInId == signInId).ExecuteUpdateAsync(u => u.SetProperty(d => d.ProfileId, chosen.Id), ct);
            return Results.Ok(new { token = current.TokenFor(chosen), profile = ToDto(chosen, current) with { IsCurrent = true } });
        });

        // A forgotten PIN, from the profile picker: the admin PIN takes it off, so the profile can be chosen and given a new
        // one. Whoever knows the admin PIN could do that in Settings anyway. Available from 0.57.
        group.MapPost("/{id}/forgot-pin", async (string id, ForgotPinRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard,
            CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            if (await db.Profiles.FirstOrDefaultAsync(p => p.Id == PlaybackState.DefaultUser, ct) is not { AdminPinHash: not null } main)
            {
                return AdminEndpoints.PinMissing();
            }

            var (callers, browser) = PinCaller(http, current);
            var (result, retryAfter) = await guard.CheckAsync(PinGuard.AdminKey, callers, () => main.CheckAdminPin(request.AdminPin), ct, mayManage: current.IsAdmin, browser);
            if (result == PinCheck.LockedOut)
            {
                return TooManyPins(http, retryAfter);
            }

            if (result == PinCheck.Wrong)
            {
                return Results.Problem("That isn't the admin PIN.", statusCode: 403, extensions: new Dictionary<string, object?> { ["parentPin"] = true });
            }

            profile.SetPin(null);
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        });

        group.MapPost("/kids-lock/release", async (KidsLockRequest request, HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard, SignIns signIns,
            CancellationToken ct) =>
        {
            if (signIns.ById(current.SignInId) is not { KidsLock: true } signIn)
            {
                return Results.NoContent();
            }

            if (await ParentPinAsync(http, db, current, guard, request.Pin, ct) is { } refused)
            {
                return refused;
            }

            signIns.SetKidsLock(signIn.Id, false);
            // Its notifications went to the kids profile with the lock: they come back to the main profile, whose PIN
            // just released it (else the parent's own phone stops hearing about devices asking to connect).
            await db.NotificationDevices.Where(d => d.SignInId == signIn.Id).ExecuteUpdateAsync(u => u.SetProperty(d => d.ProfileId, PlaybackState.DefaultUser), ct);
            return Results.NoContent();
        });

        // A profile's own choice, so anyone signed in as it may make it; the admin may make it for anyone.
        group.MapPut("/{id}/request-libraries", async (string id, RequestLibrariesRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (id != current.Id && !current.IsAdmin)
            {
                return Results.Problem("Only the main profile can choose for someone else.", statusCode: 403);
            }

            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            var errors = new Dictionary<string, string[]>();
            async Task<bool> IsAsync(Guid? libraryId, LibraryType type) =>
                libraryId is not { } chosen || await db.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Id == chosen && l.Type == type, ct);
            if (!await IsAsync(request.ShowsLibraryId, LibraryType.TvShows))
            {
                errors["showsLibraryId"] = ["That isn’t a TV library."];
            }

            if (!await IsAsync(request.FilmsLibraryId, LibraryType.Movies))
            {
                errors["filmsLibraryId"] = ["That isn’t a film library."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            profile.RequestShowsLibraryId = request.ShowsLibraryId;
            profile.RequestFilmsLibraryId = request.FilmsLibraryId;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        });

        group.MapPut("/{id}/watchlist-requests", async (string id, WatchlistRequestsRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            profile.AutoRequestWatchlist = request.AutoRequest;
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        }).RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Like the request libraries, a profile's own choice; every app's menu follows it.
        group.MapPut("/{id}/library-order", async (string id, LibraryOrderRequest request, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (id != current.Id && !current.IsAdmin)
            {
                return Results.Problem("Only the main profile can choose for someone else.", statusCode: 403);
            }

            if (request.Order is null || request.Order.Count > 500 || request.Order.Any(e => string.IsNullOrWhiteSpace(e) || e.Length > 64))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["order"] = ["List the libraries by id."] });
            }

            if (await db.Profiles.FindAsync([id], ct) is not { } profile)
            {
                return Results.NotFound();
            }

            profile.LibraryOrder = request.Order.Distinct(StringComparer.Ordinal).ToList();
            await db.SaveChangesAsync(ct);
            return Results.Ok(ToDto(profile, current));
        });

        group.MapDelete("/{id}", async (string id, FlexarrDbContext db, ProfileImages images, SignIns signIns, Trakt.TraktClient trakt,
            Trakt.TraktConnector connector, Security.GuestInvites guests, CancellationToken ct) =>
        {
            if (id == PlaybackState.DefaultUser)
            {
                return Results.Problem("The main profile can't be removed.", statusCode: 409);
            }

            // A device locked to it would be left as nobody in particular: it moves to another kids profile, if there is one.
            if (await KeepLockedDevicesAsync(id, db, signIns, ct) is { } refused)
            {
                return refused;
            }

            // Trakt forgets its token too (the row would only cascade away here, leaving the token working).
            await Trakt.TraktConnections.DisconnectAsync(db, trakt, connector, id, ct);

            // Watch history goes with the profile (cascade).
            if (await db.Profiles.Where(p => p.Id == id).ExecuteDeleteAsync(ct) == 0)
            {
                return Results.NotFound();
            }

            images.Delete(id);
            // A guest's profile: their devices are signed out and their invites cancelled, as Remove guest does.
            await guests.ForgetProfileAsync(id, ct);
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
            PinGuard guard, SignIns signIns, AdminUnlocks unlocks, CancellationToken ct) =>
        {
            if (await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) is not { } profile)
            {
                return Results.NotFound();
            }

            // A family guest keeps the profile its invite gave it.
            if (current.IsGuest && profile.Id != current.Id)
            {
                return Results.Problem("Guests stay on the profile they were invited to.", statusCode: 403);
            }

            // Child lock is what keeps a child in kids profiles: a device with it on offers nothing else, and only the admin
            // PIN turns it off. Without it, any profile may be chosen from any other, kids profiles included, with no PIN
            // but the chosen profile's own. (`parentPin`, from apps that asked for it before, is accepted and not needed.)
            if (!profile.IsKids && signIns.ById(current.SignInId)?.KidsLock == true)
            {
                return Results.Problem("Child lock is on for this device.", statusCode: 403, extensions: new Dictionary<string, object?> { ["kidsLock"] = true });
            }

            // Choosing the main profile asks for its own PIN, if it has one; the admin PIN does too (it can change the
            // main profile's anyway), and unlocks the server's settings here. Where the owner didn't sign in (a Tailscale
            // guest, a browser at home with no sign-in and kids in the house), only the admin PIN chooses it: its own PIN
            // keeps watching private, but mustn't hand out what the main profile may do to whoever learns it.
            var adminPinOnly = profile.IsMain && current.TakesAdminPinForMain;
            if (adminPinOnly && profile.AdminPinHash is null)
            {
                return Results.Problem("The main profile can only be chosen here once there's an admin PIN.", statusCode: 403);
            }

            var pinGiven = !string.IsNullOrEmpty(request.Pin);
            var mainPinGiven = false;
            var hasAdminPin = profile.IsMain && profile.AdminPinHash is not null;
            if (current.AsksPinFor(profile) || pinGiven && (profile.PinHash is not null || hasAdminPin))
            {
                var (callers, browser) = PinCaller(http, current);
                // Each PIN is counted on its own, so a right one never clears the other's wrong guesses: knowing the main
                // profile's own PIN mustn't buy fresh tries at the admin PIN. Its own first; if that's wrong, the admin
                // PIN, and when that's right the wrong guess it caused at the own PIN is taken back, so choosing the
                // main profile with the admin PIN again and again never locks anyone out.
                // Someone who may already manage the server could change the PIN anyway, so others' guesses don't hold them up.
                var result = PinCheck.Wrong;
                var retryAfter = TimeSpan.Zero;
                var ownTried = !adminPinOnly && profile.PinHash is not null;
                if (ownTried)
                {
                    (result, retryAfter) = await guard.CheckAsync(profile.Id, callers, () => profile.CheckPin(request.Pin), ct, mayManage: current.IsAdmin, browser);
                }

                if (result == PinCheck.Wrong && hasAdminPin)
                {
                    (result, retryAfter) = await guard.CheckAsync(PinGuard.AdminKey, callers, () => profile.CheckAdminPin(request.Pin), ct, mayManage: current.IsAdmin,
                        browser);
                    if (result == PinCheck.Right && ownTried)
                    {
                        guard.Forgive(profile.Id, callers, current.IsAdmin);
                    }
                }

                if (result == PinCheck.LockedOut)
                {
                    return TooManyPins(http, retryAfter);
                }

                if (result == PinCheck.Wrong)
                {
                    // `ownPin`: the PIN for this choice was the wrong one (apps from before also sent a parent's).
                    return Results.Problem("That PIN isn't right.", statusCode: 403, extensions: new Dictionary<string, object?> { ["ownPin"] = true });
                }

                // The same digits may be both PINs (as they are on a server upgraded with the PIN asked for).
                mainPinGiven = hasAdminPin && (adminPinOnly || profile.CheckAdminPin(request.Pin));
            }

            // The admin PIN was given (checked above): it unlocks the server's settings here too.
            var signInId = current.SignInId;
            if (signInId is not null)
            {
                signIns.ChooseProfile(signInId, profile.IsMain ? null : profile.Id);
            }
            else if (mainPinGiven && http.User.FindFirst(FlexarrClaims.Method)?.Value != ApiKeyAuthenticationHandler.Methods.ApiKey)
            {
                // Without a sign-in (over Tailscale, or at home with "only away from home"), the PIN signs this browser
                // in as the main profile instead, so the unlock has somewhere to live (see AdminEndpoints).
                signInId = AdminEndpoints.SignInForUnlock(http, signIns);
            }

            if (mainPinGiven && signInId is not null)
            {
                unlocks.Unlock(signInId);
            }
            else if (!profile.IsMain)
            {
                // Someone else is watching here now: managing the server takes the PIN again.
                unlocks.Lock(signInId);
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
        new(p.Id, p.Name, p.Color, p.PinHash is not null, p.IsMain, p.Id == current.Id, p.HiddenLibraryIds, current.ImageUrlFor(p), p.RequestShowsLibraryId,
            p.RequestFilmsLibraryId, p.LibraryOrder, current.AsksPinFor(p), p.AutoRequestWatchlist, p.MaxRatingAge,
            p.IsKids, current.AsksAdminPinFor(p));


    /// <summary>
    /// Moves devices locked to <paramref name="profileId"/> to another kids profile, before it goes or stops being one;
    /// null when that's done (or there were none), else why it can't be.
    /// </summary>
    private static async Task<IResult?> KeepLockedDevicesAsync(string profileId, FlexarrDbContext db, SignIns signIns, CancellationToken ct)
    {
        var locked = signIns.List().Where(s => s.KidsLock && s.ProfileId == profileId).ToList();
        if (locked.Count == 0)
        {
            return null;
        }

        var other = await db.Profiles.AsNoTracking().Where(p => p.IsKids && p.Id != profileId).OrderBy(p => p.CreatedAt).Select(p => p.Id).FirstOrDefaultAsync(ct);
        if (other is null)
        {
            return Results.Problem(locked.Count == 1
                ? $"{locked[0].Name} is locked to this profile. Unlock it in Settings › Devices first."
                : $"{locked.Count} devices are locked to this profile. Unlock them in Settings › Devices first.", statusCode: 409);
        }

        foreach (var signIn in locked)
        {
            signIns.SetKidsLock(signIn.Id, true, other);
        }

        return null;
    }

    /// <summary>
    /// Null when <paramref name="request"/> is an age limit a profile may have: a step of <see cref="RatingAges"/>, a
    /// kids profile always with one, the main profile never (it manages the server), and a kids profile only once the
    /// main profile has a PIN (it's what keeps a child in it).
    /// </summary>
    private static async Task<IResult?> ValidateAgeLimitAsync(AgeLimitRequest request, bool isMain, FlexarrDbContext db, CancellationToken ct)
    {
        if (request.MaxRatingAge is { } age && !RatingAges.Steps.Contains(age))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["maxRatingAge"] = ["An age limit is U (0), PG (8), 12, 15 or 18."] });
        }

        if (request.IsKids && request.MaxRatingAge is null)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["maxRatingAge"] = ["A kids profile needs an age limit."] });
        }

        if (isMain && (request.IsKids || request.MaxRatingAge is not null))
        {
            return Results.Problem("The main profile can't have an age limit: it manages the server.", statusCode: 409);
        }

        if (request.IsKids && await db.Profiles.AnyAsync(p => p.Id == PlaybackState.DefaultUser && p.AdminPinHash == null, ct))
        {
            return Results.Problem("Choose an admin PIN first: it's what keeps children in their profiles.", statusCode: 409);
        }

        return null;
    }

    /// <summary>
    /// Null when the request may do what takes a parent: it gives the admin PIN (or holds the API key, or the
    /// main profile has no PIN to give). The answer carries <c>parentPin</c>, so apps know which PIN to ask for.
    /// </summary>
    internal static async Task<IResult?> ParentPinAsync(HttpContext http, FlexarrDbContext db, CurrentProfile current, PinGuard guard, string? pin, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(db);
        if (current.IsAdmin || await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == PlaybackState.DefaultUser, ct) is not { AdminPinHash: not null } main)
        {
            return null;
        }

        var parent = new Dictionary<string, object?> { ["parentPin"] = true };
        if (string.IsNullOrEmpty(pin))
        {
            // Apps from before kids profiles show this as it is: they can choose the main profile and give its PIN.
            return Results.Problem("Choose the main profile and enter the admin PIN.", statusCode: 403, extensions: parent);
        }

        var (callers, browser) = PinCaller(http, current);
        var (result, retryAfter) = await guard.CheckAsync(PinGuard.AdminKey, callers, () => main.CheckAdminPin(pin), ct, mayManage: false, browser);
        return result switch
        {
            PinCheck.Right => null,
            PinCheck.LockedOut => TooManyPins(http, retryAfter),
            _ => Results.Problem("That PIN isn't right.", statusCode: 403, extensions: parent),
        };
    }

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
