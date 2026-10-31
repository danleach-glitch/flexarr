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

/// <param name="ApiKey">The guest device's own sign-in, sent as X-Api-Key.</param>
public sealed record RedeemedInviteDto(string ApiKey, string ProfileId, string ServerId, string Name);

/// <summary>Family invites: guests watching from their own homes through Tailscale built into the apps (docs/remote-guests.md).</summary>
public static class RemoteGuestEndpoints
{
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

    internal static GuestInviteDto ToDto(GuestInvite invite, IReadOnlyDictionary<string, string> profileNames) =>
        new(invite.Id, invite.Name, invite.ProfileId, profileNames.GetValueOrDefault(invite.ProfileId), invite.Via, invite.CreatedAt, invite.ExpiresAt);
}
