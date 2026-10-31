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

/// <param name="Name">What to call the device; omitted, it's named when it joins ("Gran's iPhone").</param>
public sealed record CreateInviteRequest(string? Name, string ProfileId);

public sealed record RedeemInviteRequest(string Token);

/// <summary>A guest's device, as Settings › Remote access lists it.</summary>
/// <param name="Path">How it reaches this server right now: Direct, PeerRelay (through this server's relay), Derp
/// (Tailscale's shared relays, about 2 Mbps), or Unknown (not connected lately).</param>
/// <param name="TailscaleAdminUrl">Where the owner deletes the device from the tailnet.</param>
/// <param name="ServerMoved">This server's tailnet address or Flexarr's port has changed since the device joined, so
/// its app can't reach the server any more: it needs adding again.</param>
public sealed record GuestDeviceDto(string Id, string Name, string? ProfileId, string? ProfileName, Authentication.DeviceType? DeviceType, DateTime CreatedAt,
    DateTime LastUsedAt, string? Address, bool Online, TailnetPath Path, DateTime? LastSeen, string TailscaleAdminUrl, bool ServerMoved = false);

/// <summary>Family invites' setup, as Settings › Remote access shows it.</summary>
/// <param name="Enabled">Family invites are turned on for this server (hidden until the apps that carry Tailscale are out).</param>
/// <param name="Available">This server can do them: Tailscale is installed here (not Docker or the Mac app, yet).</param>
/// <param name="ClientId">The OAuth client's id; its secret is never sent, only <paramref name="HasClient"/>.</param>
/// <param name="Address">This server's tailnet address, which guests reach Flexarr on.</param>
/// <param name="Snippet">The lines to paste into the tailnet policy, with this server's address filled in.</param>
/// <param name="Check">The safety check's last answer; null until it has run.</param>
public sealed record RemoteGuestsDto(bool Enabled, bool Available, string? Problem, bool TailscaleRunning, string? Address, string? DnsName, int FlexarrPort,
    string Tag, string ClientId, bool HasClient, string? Snippet, GuestPolicyCheck? Check, RelayDto Relay, int Waiting, int Devices);

/// <param name="ClientSecret">Omitted (or empty) keeps the saved one while the id stays the same.</param>
public sealed record GuestClientRequest(string ClientId, string? ClientSecret);

/// <param name="Port">The UDP port to relay on; omitted keeps the saved one, else <see cref="RemoteGuestEndpoints.DefaultRelayPort"/>.</param>
public sealed record RelayRequest(bool Enabled, int? Port = null);

/// <param name="Port">The UDP port the owner forwards on the router; null when the relay is off.</param>
/// <param name="Working">This server's Tailscale is relaying on that port.</param>
public sealed record RelayDto(int? Port, bool Working, string? Problem = null);

/// <param name="Code">The code the guest's TV shows (8 letters and digits; spaces and dashes are fine).</param>
public sealed record TvCodeRequest(string Code, string? Name, string ProfileId);

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

        group.MapGet("/", async (HttpContext http, GuestPolicy policy, ITailnet tailnet, GuestInvites invites, SignIns signIns,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) => Results.Ok(await DescribeAsync(http, policy, tailnet, invites, signIns, options, ct)));

        // The owner's Tailscale OAuth client. Saving it checks it straight away.
        group.MapPut("/client", async (GuestClientRequest request, HttpContext http, TailscaleApi api, GuestPolicy policy, ITailnet tailnet, GuestInvites invites,
            SignIns signIns, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var settings = options.CurrentValue;
            var id = request.ClientId.Trim();
            var secret = request.ClientSecret?.Trim() is { Length: > 0 } given ? given
                : id == settings.RemoteGuests.ClientId ? settings.RemoteGuests.ClientSecret : "";
            if (id.Length > 0 && secret.Length == 0)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["clientSecret"] = ["Paste the client's secret too."] });
            }

            // Tried before it's kept: a client Tailscale refuses, or one that can do more than Flexarr needs, never stays.
            var (oldId, oldSecret) = (settings.RemoteGuests.ClientId, settings.RemoteGuests.ClientSecret);
            settings.RemoteGuests.ClientId = id;
            settings.RemoteGuests.ClientSecret = id.Length == 0 ? "" : secret;
            api.Forget();
            policy.Forget();
            if (id.Length > 0 && await api.CheckClientAsync(ct) is { } refused)
            {
                (settings.RemoteGuests.ClientId, settings.RemoteGuests.ClientSecret) = (oldId, oldSecret);
                api.Forget();
                return Results.Problem(refused, statusCode: 400);
            }

            ServerConfigStore.Update(settings.DataDirectory, config =>
            {
                var section = config["remoteGuests"] as System.Text.Json.Nodes.JsonObject ?? [];
                section["clientId"] = settings.RemoteGuests.ClientId;
                section["clientSecret"] = settings.RemoteGuests.ClientSecret;
                config["remoteGuests"] = section;
            });
            if (id.Length > 0)
            {
                await policy.CheckAsync(ct, fresh: true);
            }

            return Results.Ok(await DescribeAsync(http, policy, tailnet, invites, signIns, options, ct));
        });

        // Checks the policy again now (after pasting the snippet).
        group.MapPost("/check", async (HttpContext http, GuestPolicy policy, ITailnet tailnet, GuestInvites invites, SignIns signIns,
            IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            await policy.CheckAsync(ct, fresh: true);
            return Results.Ok(await DescribeAsync(http, policy, tailnet, invites, signIns, options, ct));
        });

        // Removing a guest's device signs it out, and the gate then refuses everything from it (GuestGate). It stays
        // on the tailnet until the owner deletes it in Tailscale's admin page (the device list links there).
        group.MapDelete("/devices/{id}", (string id, SignIns signIns) =>
            signIns.ById(id) is { Guest: true } && signIns.Revoke(id) ? Results.NoContent() : Results.NotFound());

        // Every guest, devices or not: their profile stays (with what they've watched) until the guest is removed.
        group.MapGet("/people", async (GuestInvites invites, FlexarrDbContext db, CancellationToken ct) =>
        {
            var ids = invites.GuestProfiles();
            var present = await db.Profiles.AsNoTracking().Where(p => ids.Contains(p.Id) && p.Id != PlaybackState.DefaultUser).Select(p => p.Id).ToListAsync(ct);
            return Results.Ok(present);
        });

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
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["code"] = ["Type the 8 letters and numbers their device shows."] });
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
                    return Results.Problem("That code isn’t showing on any device. Codes change every 10 minutes: check the one on their screen now.", statusCode: 404);
                }

                (invite, var payload) = await invites.CreateAsync(request.Name, profile!.Id, profile.Name, owner, InviteVia.TvCode, ct);
                try
                {
                    await tvCodes.DeliverAsync(code, tvKey, GuestInvites.Encode(payload), ct);
                }
                catch (Exception ex) when (ex is GuestTvCodes.MaybeDeliveredException || ex is TaskCanceledException && !ct.IsCancellationRequested)
                {
                    // It may have reached the relay (and the TV): kept, so its key still works; it goes by itself after
                    // GuestInvites.CodeLifetime if nobody uses it.
                    return Results.Problem("Flexarr's relay was slow to answer. If their screen asks to join, that's it; if not, try again.",
                        statusCode: 502);
                }

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
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Anything else on the way (the relay timing out or answering nonsense, a TV key that won't import):
                // the invite never reached the TV, so it and its Tailscale key go rather than wait three days.
                if (invite is not null)
                {
                    await invites.CancelAsync(invite.Id, CancellationToken.None);
                }

                return Results.Problem("Couldn't reach Flexarr's relay. Try again in a minute.", statusCode: 502);
            }
            catch (OperationCanceledException) when (invite is not null)
            {
                // The owner closed the page part way: the TV may never get it.
                await invites.CancelAsync(invite.Id, CancellationToken.None);
                throw;
            }
        });

        group.MapGet("/devices", async (SignIns signIns, ITailnet tailnet, GuestPolicy policy, FlexarrDbContext db, CancellationToken ct) =>
        {
            var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
            var snapshot = await tailnet.SnapshotAsync(ct);
            return Results.Ok(Describe(signIns.List().Where(s => s.Guest), snapshot, names, policy.FlexarrPort));
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
            IOptionsMonitor<FlexarrOptions> options, Notifications.GuestAlerts alerts, FlexarrDbContext db, CancellationToken ct) =>
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
                return Results.Problem("Family invites are paused on this server. Ask its owner to check Settings › Remote access.", statusCode: 503);
            }

            try
            {
                var names = await db.Profiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
                var (signIn, token, invite, retry) = invites.Redeem(request.Token, address, DeviceTypes.Of(http.Request), names.GetValueOrDefault, names.ContainsKey);
                if (!retry)
                {
                    alerts.Joined(signIn.Name, invite.ProfileId);
                    // In the background: the device is waiting for its sign-in, not for this.
                    _ = Task.Run(() => invites.DeleteUsedKeyAsync(invite, CancellationToken.None), CancellationToken.None);
                }

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

    internal static async Task<RemoteGuestsDto> DescribeAsync(HttpContext http, GuestPolicy policy, ITailnet tailnet, GuestInvites invites, SignIns signIns,
        IOptionsMonitor<FlexarrOptions> options, CancellationToken ct)
    {
        var settings = options.CurrentValue.RemoteGuests;
        var kind = http.RequestServices.GetRequiredService<Updates.IInstallEnvironment>().Kind;
        var snapshot = await tailnet.SnapshotAsync(ct);
        var problem = kind switch
        {
            Updates.InstallKind.Docker => "Family invites need Tailscale on the server, which Flexarr in Docker can't use yet.",
            Updates.InstallKind.MacApp => "Family invites aren't in the Mac app yet.",
            _ when snapshot is null => "Install Tailscale on the server and sign in to it.",
            _ when !snapshot.Running => "Tailscale is installed but not connected. Sign in to Tailscale on the server.",
            _ => null,
        };
        var snippet = snapshot?.Address is { } address ? GuestPolicy.Snippet(settings.Tag, address, policy.FlexarrPort, settings.RelayPort is not null) : null;
        return new RemoteGuestsDto(settings.Enabled, kind is not (Updates.InstallKind.Docker or Updates.InstallKind.MacApp), problem, snapshot?.Running == true,
            snapshot?.Address, snapshot?.DnsName, policy.FlexarrPort, settings.Tag, settings.ClientId, settings.HasClient, snippet, policy.Last,
            RelayStatus(settings.RelayPort, snapshot), invites.Waiting().Count, signIns.List().Count(s => s.Guest));
    }

    internal static RelayDto RelayStatus(int? port, TailnetSnapshot? snapshot) =>
        new(port, port is not null && snapshot?.RelayPort == port,
            port is not null && snapshot is not null && snapshot.RelayPort != port ? "Tailscale on the server isn't relaying yet. It needs Tailscale 1.86 or newer." : null);

    /// <summary>Guests' devices with how each reaches this server now, matched to the tailnet by address.</summary>
    internal static List<GuestDeviceDto> Describe(IEnumerable<SignIn> guests, TailnetSnapshot? snapshot, IReadOnlyDictionary<string, string> profileNames, int flexarrPort = 0)
    {
        var here = snapshot?.Address is { } self && flexarrPort > 0 ? $"{self}:{flexarrPort.ToString(System.Globalization.CultureInfo.InvariantCulture)}" : null;
        var peers = (snapshot?.Peers ?? []).Where(p => p.Address is not null).GroupBy(p => p.Address!).ToDictionary(g => g.Key, g => g.First());
        return guests.Select(g =>
        {
            var peer = g.GuestAddress is { } address ? peers.GetValueOrDefault(address) : null;
            return new GuestDeviceDto(g.Id, g.Name, g.ProfileId, g.ProfileId is { } id ? profileNames.GetValueOrDefault(id) : null, g.DeviceType, g.CreatedAt,
                g.LastUsedAt, g.GuestAddress, peer?.Online == true, peer?.Online == true ? peer.Path : TailnetPath.Unknown, peer?.LastSeen,
                "https://login.tailscale.com/admin/machines", here is not null && g.GuestServer is { } joined && joined != here);
        }).OrderByDescending(d => d.Online).ThenByDescending(d => d.LastUsedAt).ToList();
    }

    internal static GuestInviteDto ToDto(GuestInvite invite, IReadOnlyDictionary<string, string> profileNames) =>
        new(invite.Id, invite.Name, invite.ProfileId, profileNames.GetValueOrDefault(invite.ProfileId), invite.Via, invite.CreatedAt, invite.ExpiresAt);
}
