using Flexarr.Api.Authentication;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Security;

public sealed record RemoteAccessDto(
    NetworkZone YourConnection,
    string? YourTailscaleLogin,
    TailscaleState Tailscale,
    IReadOnlyList<TailnetLinkDto> Links,
    bool CloudflareTunnelRunning,
    bool MainProfileHasPin);

public sealed record TailnetLinkDto(string Login, string Name, string? ProfileId);

public sealed record TailscaleToggle(bool Enabled);

public sealed record TailscaleLinkRequest(string Login, string? ProfileId);

public sealed record MeDto(string ProfileId, string Method, NetworkZone Zone, bool IsAdmin, string? TailscaleLogin);

public static class RemoteAccessEndpoints
{
    public static RouteGroupBuilder MapRemoteAccessEndpoints(this RouteGroupBuilder api)
    {
        // Who this request is: which profile, how it signed in, and from where.
        api.MapGet("/me", (HttpContext http, Flexarr.Api.Profiles.CurrentProfile profile) => new MeDto(
            profile.Id,
            http.User.FindFirst(FlexarrClaims.Method)?.Value ?? "none",
            NetworkZones.Of(http),
            profile.IsAdmin,
            http.User.FindFirst(FlexarrClaims.TailscaleLogin)?.Value)).WithTags("Remote access");

        var group = api.MapGroup("/remote").WithTags("Remote access").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/", async (HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct) =>
            await DescribeAsync(http, tailscale, db, ct));

        group.MapPost("/tailscale", async (TailscaleToggle toggle, HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct) =>
        {
            var port = http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383;
            var error = toggle.Enabled ? await tailscale.EnableAsync(port, ct) : await tailscale.DisableAsync(ct);
            if (error is not null)
            {
                return Results.Problem($"Tailscale said: {error}", statusCode: 502);
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

        group.MapPost("/tailscale/links", async (TailscaleLinkRequest request, HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct) =>
        {
            // One profile per Tailscale account: unlink it wherever it was first.
            await db.Profiles.Where(p => p.TailscaleLogin == request.Login).ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, (string?)null), ct);
            if (request.ProfileId is { } id)
            {
                if (await db.Profiles.Where(p => p.Id == id).ExecuteUpdateAsync(u => u.SetProperty(p => p.TailscaleLogin, request.Login), ct) == 0)
                {
                    return Results.NotFound();
                }
            }

            return Results.Ok(await DescribeAsync(http, tailscale, db, ct));
        });

        return api;
    }

    private static async Task<RemoteAccessDto> DescribeAsync(HttpContext http, TailscaleService tailscale, FlexarrDbContext db, CancellationToken ct)
    {
        var port = http.Connection.LocalPort > 0 ? http.Connection.LocalPort : 8383;
        var state = await tailscale.StateAsync(port, ct);
        var linked = await db.Profiles.AsNoTracking().Where(p => p.TailscaleLogin != null).ToDictionaryAsync(p => p.TailscaleLogin!, p => p.Id, ct);
        var links = state.People.Select(p => new TailnetLinkDto(p.Login, p.Name, linked.GetValueOrDefault(p.Login))).ToList();
        var mainPin = await db.Profiles.AsNoTracking().AnyAsync(p => p.Id == PlaybackState.DefaultUser && p.PinHash != null, ct);
        return new RemoteAccessDto(
            NetworkZones.Of(http),
            ApiKeyAuthenticationHandler.TrustedTailscaleLogin(http),
            state,
            links,
            CloudflareRunning(),
            mainPin);
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
