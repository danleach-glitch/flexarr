using System.Net.NetworkInformation;
using System.Net.Sockets;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Updates;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Security;

public sealed record RemoteAccessDto(
    NetworkZone YourConnection,
    string? YourTailscaleLogin,
    TailscaleState Tailscale,
    IReadOnlyList<TailnetLinkDto> Links,
    bool CloudflareTunnelRunning,
    bool MainProfileHasPin,
    bool InContainer,
    // The Mac app's sandbox can't run the tailscale command, so the page explains instead of offering Turn on.
    bool InMacApp = false,
    // There, the address this Mac has on a tailnet, if it's on one: plain HTTP on the server's own port.
    string? TailnetAddress = null);

public sealed record TailnetLinkDto(string Login, string Name, string? ProfileId);

public sealed record TailscaleToggle(bool Enabled);

public sealed record TailscaleLinkRequest(string Login, string? ProfileId);

/// <param name="CanUnlock">The main profile is chosen but managing the server takes its PIN first (POST /admin/unlock).</param>
/// <param name="AdminPinMissing">The main profile has no PIN yet, so the server can't be unlocked until it's given one (POST /admin/pin).</param>
/// <param name="UnlockedUntil">When managing the server takes the PIN again, on this browser or device.</param>
/// <param name="IsKids">The profile is a child's: show the kids app, and leaving it takes the admin PIN. Absent from servers before 0.56.</param>
/// <param name="MaxRatingAge">The profile's age limit (0 is U, 8 PG, then 12, 15, 18); null, none. Absent from servers before 0.56.</param>
/// <param name="KidsLock">This browser or device is locked to kids profiles. Absent from servers before 0.56.</param>
/// <param name="IsGuest">A family guest's device: it stays on its profile and has no household devices. Absent from servers before 0.57.</param>
/// <param name="Language">
/// The profile's chosen language (a BCP 47 code); null follows the device. Set with PUT /me/language. Absent from servers
/// before 0.61.
/// </param>
/// <param name="ChooseProfile">
/// No profile is chosen here yet and the main profile's own PIN is needed to be it: show who's watching. Only for
/// clients that send <c>X-Flexarr-Profile-Choice: 1</c>. Absent from servers before 0.63.
/// </param>
/// <param name="RatingSystem">
/// Whose names the server's age limits go by: "uk" (U, PG, 12, 15, 18) or "us" (G, PG, PG-13, TV-14, R), for the same
/// ages. Absent from servers before 0.64, which are the UK's.
/// </param>
public sealed record MeDto(string ProfileId, string Method, NetworkZone Zone, bool IsAdmin, string? TailscaleLogin, bool CanUnlock = false, bool AdminPinMissing = false,
    DateTime? UnlockedUntil = null, bool IsKids = false, int? MaxRatingAge = null, bool KidsLock = false, bool IsGuest = false, string? Language = null,
    bool ChooseProfile = false, string? RatingSystem = null);

/// <summary>The language a profile picked; null to follow each device's own.</summary>
public sealed record LanguageChoice(string? Language);

public static class RemoteAccessEndpoints
{
    public static RouteGroupBuilder MapRemoteAccessEndpoints(this RouteGroupBuilder api)
    {
        // Who this request is: which profile, how it signed in, and from where.
        api.MapGet("/me", async (HttpContext http, Flexarr.Api.Profiles.CurrentProfile profile, FlexarrDbContext db, AdminUnlocks unlocks, SignIns signIns,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            return new MeDto(
                profile.Id,
                http.User.FindFirst(FlexarrClaims.Method)?.Value ?? "none",
                NetworkZones.Of(http),
                profile.IsAdmin,
                http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value,
                profile.CanUnlock,
                (profile.IsAdmin || profile.CanUnlock) && await db.Profiles.AnyAsync(p => p.Id == PlaybackState.DefaultUser && p.AdminPinHash == null, ct),
                profile.IsAdmin ? unlocks.UnlockedUntil(profile.SignInId) : null,
                db.ServesKids, db.MaxRatingAge, signIns.ById(profile.SignInId)?.KidsLock == true, profile.IsGuest,
                await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id).Select(p => p.Language).FirstOrDefaultAsync(ct),
                http.User.HasClaim(FlexarrClaims.ChooseProfile, "true"),
                options.CurrentValue.Podcasts.RatingSystem.ToString().ToLowerInvariant());
        }).WithTags("Remote access");

        // Each profile's own, like its playback preferences: anyone signed in as it may change it, kids included.
        api.MapPut("/me/language", async (LanguageChoice choice, FlexarrDbContext db, Flexarr.Api.Profiles.CurrentProfile profile,
            Localization.ProfileLanguages languages, CancellationToken ct) =>
        {
            var language = string.IsNullOrWhiteSpace(choice.Language) ? null : choice.Language.Trim();
            if (language is not null && !Flexarr.Core.Localization.LanguageCode.IsValid(language))
            {
                return Localization.Problems.Field("That isn’t a language code.", field: "language");
            }

            if (await db.Profiles.FindAsync([profile.Id], ct) is not { } found)
            {
                return Results.NotFound();
            }

            found.Language = language;
            await db.SaveChangesAsync(ct);
            languages.Changed(profile.Id, language);
            return Results.Ok(new LanguageChoice(language));
        }).WithTags("Languages");

        var group = api.MapGroup("/remote").WithTags("Remote access").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct) =>
            await DescribeAsync(http, tailscale, db, ct));

        group.MapPost("/tailscale", async (TailscaleToggle toggle, HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct) =>
        {
            var port = http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383;
            var error = toggle.Enabled ? await tailscale.EnableAsync(port, ct) : await tailscale.DisableAsync(ct);
            if (error is not null)
            {
                return Problems.Of("Tailscale said: {error}", new { error }, status: 502);
            }

            // First time on: your own Tailscale account signs in as the main profile.
            if (toggle.Enabled && (await tailscale.StateAsync(port, ct)).OwnerLogin is { } owner
                && !await db.Profiles.AnyAsync(p => p.TailscaleLogin != null, ct))
            {
                await db.Profiles.Where(p => p.Id == PlaybackState.DefaultUser)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, owner), ct);
            }

            return Results.Ok(await DescribeAsync(http, tailscale, db, ct));
        });

        group.MapPost("/tailscale/links", async (TailscaleLinkRequest request, HttpContext http, TailscaleService tailscale, FlexarrDbContext db, SignIns signIns,
            CancellationToken ct) =>
        {
            // Tagged devices (family guests among them) all share this login: linking it would give every one a profile.
            if (string.Equals(request.Login.Trim(), "tagged-devices", StringComparison.OrdinalIgnoreCase))
            {
                return Problems.Field("Tagged devices can’t be linked to a profile. Invite guests from Family invites instead.", field: "login");
            }

            // One profile per Tailscale account: unlink it wherever it was first.
            var before = await db.Profiles.AsNoTracking().Where(p => p.TailscaleLogin == request.Login).Select(p => p.Id).FirstOrDefaultAsync(ct);
            await db.Profiles.Where(p => p.TailscaleLogin == request.Login).ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, (string?)null), ct);
            if (request.ProfileId is { } id)
            {
                if (await db.Profiles.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, request.Login), ct) == 0)
                {
                    // It's unlinked now (as if it had been chosen for nobody), so what it signed in goes too.
                    signIns.RevokeEarnedBy(request.Login);
                    return Results.NotFound();
                }
            }

            // Browsers that signed in as the main profile with its PIN over this login go when it's unlinked or moved.
            if (request.ProfileId != before)
            {
                signIns.RevokeEarnedBy(request.Login);
            }

            return Results.Ok(await DescribeAsync(http, tailscale, db, ct));
        });

        return api;
    }

    private static async Task<RemoteAccessDto> DescribeAsync(HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct)
    {
        var port = http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383;
        var macApp = http.RequestServices.GetRequiredService<IInstallEnvironment>().Kind == InstallKind.MacApp;
        var state = await tailscale.StateAsync(port, ct);
        var linked = await db.Profiles.AsNoTracking().Where(p => p.TailscaleLogin != null).ToDictionaryAsync(p => p.TailscaleLogin!, p => p.Id, ct);
        var links = state.People.Select(p => new TailnetLinkDto(p.Login, p.Name, linked.GetValueOrDefault(p.Login))).ToList();
        var mainPin = await db.Profiles.AsNoTracking().AnyAsync(p => p.Id == PlaybackState.DefaultUser && p.AdminPinHash != null, ct);
        return new RemoteAccessDto(
            NetworkZones.Of(http),
            http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value,
            state,
            links,
            CloudflareRunning(),
            mainPin,
            // A container can't see the host's tailscale command, so Turn on can't work there: the page says so.
            Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
            macApp,
            macApp && TailnetIp() is { } ip ? $"http://{ip}:{port}" : null);
    }

    /// <summary>This computer's address in Tailscale's range (100.64.0.0/10), read from its network interfaces.</summary>
    internal static string? TailnetIp()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && a.GetAddressBytes() is [100, var b, ..] && b is >= 64 and < 128)
                ?.ToString();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }

    private static bool CloudflareRunning()
    {
        try
        {
            return System.Diagnostics.Process.GetProcessesByName("cloudflared").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
