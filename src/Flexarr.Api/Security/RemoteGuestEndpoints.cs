using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>A waiting invite, as Settings lists it. The link itself is only shown when it's made.</summary>
public sealed record GuestInviteDto(string Id, string Name, string ProfileId, string? ProfileName, InviteVia Via, DateTime CreatedAt, DateTime ExpiresAt);

/// <param name="Link">https://flexarr.io/join#…, to send the guest (and to draw as a QR code). Shown once.</param>
public sealed record CreatedInviteDto(GuestInviteDto Invite, string Link);

public sealed record CreateInviteRequest(string Name, string ProfileId);

public sealed record RedeemInviteRequest(string Token);

/// <summary>A guest's device, as Settings › Access lists it.</summary>
/// <param name="Path">How it reaches this server right now: Direct, PeerRelay (through this server's relay), Derp
/// (Tailscale's shared relays, about 2 Mbps), or Unknown (not connected lately).</param>
/// <param name="TailscaleAdminUrl">Where the owner deletes the device from the tailnet.</param>
public sealed record GuestDeviceDto(string Id, string Name, string? ProfileId, string? ProfileName, Authentication.DeviceType? DeviceType, DateTime CreatedAt,
    DateTime LastUsedAt, string? Address, bool Online, TailnetPath Path, DateTime? LastSeen, string TailscaleAdminUrl);

/// <param name="Port">The UDP port to relay on; omitted keeps the saved one, else <see cref="RemoteGuestEndpoints.DefaultRelayPort"/>.</param>
public sealed record RelayRequest(bool Enabled, int? Port = null);

/// <param name="Port">The UDP port the owner forwards on the router; null when the relay is off.</param>
/// <param name="Working">This server's Tailscale is relaying on that port.</param>
public sealed record RelayDto(int? Port, bool Working, string? Problem = null);

/// <param name="Code">The code the guest's TV shows (8 letters and digits; spaces and dashes are fine).</param>
public sealed record TvCodeRequest(string Code, string Name, string ProfileId);

/// <param name="ApiKey">The guest device's own sign-in, sent as X-Api-Key.</param>
public sealed record RedeemedInviteDto(string ApiKey, string ProfileId, string ServerId, string Name);

/// <summary>Family invites: guests watching from their own homes through Tailscale built into the apps (docs/remote-guests.md).</summary>
public static class RemoteGuestEndpoints
{
    /// <summary>The faster relay's UDP port unless the owner picks another.</summary>
    public const int DefaultRelayPort = 40000;

    public static RouteGroupBuilder MapRemoteGuestEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/remote/guests").WithTags("Family invites").RequireAuthorization(FlexarrClaims.AdminPolicy);

        group.MapGet("/invites", async (GuestInvites invites, FlexarrDbContext db, CancellationToken ct) =>
        {
            var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            return Results.Ok(invites.Waiting().Select(i => ToDto(i, names)).ToList());
        });

        group.MapPost("/invites", async (CreateInviteRequest request, GuestInvites invites, FlexarrDbContext db, CancellationToken ct) =>
        {
            var (profile, owner, problem) = await ProfileForInviteAsync(db, request.ProfileId, ct);
            if (problem is not null)
            {
                return problem;
            }

            try
            {
                var (invite, payload) = await invites.CreateAsync(request.Name, profile!.Id, profile.Name, owner, InviteVia.Link, ct);
                return Results.Ok(new CreatedInviteDto(ToDto(invite, new Dictionary<string, string> { [profile.Id] = profile.Name }),
                    GuestInvites.LinkBase + GuestInvites.Encode(payload)));
            }
            catch (InviteException ex)
            {
                return Results.Problem(ex.Message, statusCode: ex.Status);
            }
        });

        // A guest's TV: the code it shows reaches this server through Flexarr's relay (GuestTvCodes).
        group.MapPost("/tv-code", async (TvCodeRequest request, GuestInvites invites, GuestTvCodes tvCodes, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (GuestTvCodes.Normalize(request.Code) is not { } code)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Type the 8 letters and numbers the TV shows."] });
            }

            var (profile, owner, problem) = await ProfileForInviteAsync(db, request.ProfileId, ct);
            if (problem is not null)
            {
                return problem;
            }

            GuestInvite? invite = null;
            try
            {
                if (await tvCodes.TvKeyAsync(code, ct) is not { } tvKey)
                {
                    return Results.Problem("No TV is showing that code. Check it, or ask for the new one if it changed.", statusCode: 404);
                }

                (invite, var payload) = await invites.CreateAsync(request.Name, profile!.Id, profile.Name, owner, InviteVia.TvCode, ct);
                await tvCodes.DeliverAsync(code, tvKey, GuestInvites.Encode(payload), ct);
                return Results.Ok(ToDto(invite, new Dictionary<string, string> { [profile.Id] = profile.Name }));
            }
            catch (InviteException ex)
            {
                if (invite is not null)
                {
                    await invites.CancelAsync(invite.Id, CancellationToken.None);
                }

                return Results.Problem(ex.Message, statusCode: ex.Status);
            }
            catch (HttpRequestException)
            {
                if (invite is not null)
                {
                    await invites.CancelAsync(invite.Id, CancellationToken.None);
                }

                return Results.Problem("Couldn't reach Flexarr's relay. Try again in a minute.", statusCode: 502);
            }
        });

        group.MapGet("/devices", async (SignIns signIns, ITailnet tailnet, FlexarrDbContext db, CancellationToken ct) =>
        {
            var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var snapshot = await tailnet.SnapshotAsync(ct);
            return Results.Ok(Describe(signIns.List().Where(s => s.Guest), snapshot, names));
        });

        // The faster relay: this server becomes a Tailscale peer relay, so guests whose networks can't connect
        // directly go through it rather than Tailscale's shared relays. The snippet gains the relay grant.
        group.MapPut("/relay", async (RelayRequest request, ITailnet tailnet, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var settings = options.CurrentValue;
            int? port = request.Enabled ? request.Port ?? settings.RemoteGuests.RelayPort ?? DefaultRelayPort : null;
            if (port is < 1024 or > 65535)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["port"] = ["Choose a port from 1024 to 65535."] });
            }

            if (await tailnet.SetRelayPortAsync(port, ct) is { } problem)
            {
                return Results.Problem($"Tailscale couldn't {(request.Enabled ? "start" : "stop")} the relay: {problem}", statusCode: 502);
            }

            settings.RemoteGuests.RelayPort = port;
            ServerConfigStore.Update(settings.DataDirectory, config =>
            {
                var section = config["remoteGuests"] as System.Text.Json.Nodes.JsonObject ?? [];
                section["relayPort"] = port;
                config["remoteGuests"] = section;
            });
            var snapshot = await tailnet.SnapshotAsync(ct);
            return Results.Ok(RelayStatus(port, snapshot));
        });

        group.MapDelete("/invites/{id}", async (string id, GuestInvites invites, CancellationToken ct) =>
            await invites.CancelAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return api;
    }

    /// <summary>The anonymous part: a guest device swapping its invite's token for a sign-in.</summary>
    public static RouteGroupBuilder MapGuestRedeemEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/invites/redeem", async (RedeemInviteRequest request, HttpContext http, GuestInvites invites, GuestPolicy policy, ITailnetWhois whois,
            IOptionsMonitor<FlexarrOptions> options, Notifications.GuestAlerts alerts, CancellationToken ct) =>
        {
            var settings = options.CurrentValue.RemoteGuests;
            if (!settings.Enabled)
            {
                return Results.NotFound();
            }

            // Only a guest device on the tailnet: the invite's key put it there, tagged, so it can reach nothing else.
            if (await GuestDevices.AddressOfAsync(http, whois, settings) is not { } address)
            {
                return Results.Problem("An invite can only be used by the app that joined with it.", statusCode: 403);
            }

            if (!(await policy.CheckAsync(ct)).Safe)
            {
                return Results.Problem("Family invites are paused on this server. Ask its owner to check Settings › Access.", statusCode: 503);
            }

            try
            {
                var (signIn, token, invite) = invites.Redeem(request.Token, address, DeviceTypes.Of(http.Request));
                alerts.Joined(invite.Name, invite.ProfileId);
                return Results.Ok(new RedeemedInviteDto(token, signIn.ProfileId!, options.CurrentValue.ServerId ?? "", signIn.Name));
            }
            catch (InviteException ex)
            {
                return Results.Problem(ex.Message, statusCode: ex.Status);
            }
        }).WithTags("Family invites").AllowAnonymous();

        return api;
    }

    /// <summary>The profile an invite would give, and the owner's name for the guest's "Join Dan's Flexarr?".</summary>
    internal static async Task<(Profile? Profile, string Owner, IResult? Problem)> ProfileForInviteAsync(FlexarrDbContext db, string profileId, CancellationToken ct)
    {
        var profiles = await db.Profiles.AsNoTracking().ToListAsync(ct);
        var owner = profiles.FirstOrDefault(p => p.Id == PlaybackState.DefaultUser)?.Name ?? "Flexarr";
        var profile = profiles.FirstOrDefault(p => p.Id == profileId);
        if (profile is null)
        {
            return (null, owner, Results.ValidationProblem(new Dictionary<string, string[]> { ["profileId"] = ["Choose one of your profiles."] }));
        }

        if (profile.Id == PlaybackState.DefaultUser)
        {
            return (null, owner, Results.ValidationProblem(new Dictionary<string, string[]> { ["profileId"] = ["Guests can't have the main profile. Make them a profile of their own."] }));
        }

        return (profile, owner, null);
    }

    internal static RelayDto RelayStatus(int? port, TailnetSnapshot? snapshot) =>
        new(port, port is not null && snapshot?.RelayPort == port,
            port is not null && snapshot is not null && snapshot.RelayPort != port ? "Tailscale on the server isn't relaying yet. It needs Tailscale 1.86 or newer." : null);

    /// <summary>Guests' devices with how each reaches this server now, matched to the tailnet by address.</summary>
    internal static List<GuestDeviceDto> Describe(IEnumerable<SignIn> guests, TailnetSnapshot? snapshot, IReadOnlyDictionary<string, string> profileNames)
    {
        var peers = (snapshot?.Peers ?? []).Where(p => p.Address is not null).GroupBy(p => p.Address!).ToDictionary(g => g.Key, g => g.First());
        return guests.Select(g =>
        {
            var peer = g.GuestAddress is { } address ? peers.GetValueOrDefault(address) : null;
            return new GuestDeviceDto(g.Id, g.Name, g.ProfileId, g.ProfileId is { } id ? profileNames.GetValueOrDefault(id) : null, g.DeviceType, g.CreatedAt,
                g.LastUsedAt, g.GuestAddress, peer?.Online == true, peer?.Online == true ? peer.Path : TailnetPath.Unknown, peer?.LastSeen,
                "https://login.tailscale.com/admin/machines");
        }).OrderByDescending(d => d.Online).ThenByDescending(d => d.LastUsedAt).ToList();
    }

    internal static GuestInviteDto ToDto(GuestInvite invite, IReadOnlyDictionary<string, string> profileNames) =>
        new(invite.Id, invite.Name, invite.ProfileId, profileNames.GetValueOrDefault(invite.ProfileId), invite.Via, invite.CreatedAt, invite.ExpiresAt);
}
