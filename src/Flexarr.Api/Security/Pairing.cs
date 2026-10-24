using System.Collections.Concurrent;
using System.Security.Cryptography;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

public sealed record PairingStartRequest(string? DeviceName);

/// <param name="Id">The device's secret handle for polling; never shown to people.</param>
/// <param name="Code">What a person approves: shown on the TV and in its QR code.</param>
public sealed record PairingStartDto(string Id, string Code, DateTime ExpiresAt);

/// <param name="Status">"waiting", "approved" (with the device's own token, given once) or "expired".</param>
/// <param name="ApiKey">The token the TV sends as X-Api-Key from now on. Not the server's API key: it can be signed out
/// on its own, from Settings.</param>
public sealed record PairingStatusDto(string Status, string? ApiKey);

/// <param name="RequestedFrom">The address the TV asked from, so the approver can tell it's theirs.</param>
public sealed record PairingDetailsDto(string Code, string DeviceName, DateTime ExpiresAt, string? RequestedFrom, NetworkZone Zone);

/// <param name="DeviceName">A name to list the TV under instead of the one it gave.</param>
public sealed record PairingApprovalRequest(string? DeviceName = null);

/// <summary>
/// Connecting a TV without typing: the TV, on the home network or tailnet, asks for a code and shows it (and a QR code
/// linking to it); someone signed in as the main profile approves it in the web app; the TV, polling with its secret
/// id, is handed a token of its own. Codes last ten minutes and only a few can be waiting at once, from any one address
/// or altogether.
/// </summary>
public sealed class Pairings(SignIns signIns, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxWaiting = 10;
    private const int MaxWaitingPerAddress = 3;

    // No 0/O, 1/I/L: codes are read off a TV and typed on a phone.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed class Pending(string id, string code, string deviceName, DateTime expiresAt, NetworkZone zone, string? address)
    {
        public string Id { get; } = id;
        public string Code { get; } = code;
        public string DeviceName { get; set; } = deviceName;
        public DateTime ExpiresAt { get; } = expiresAt;
        public NetworkZone Zone { get; } = zone;
        public string? Address { get; } = address;
        public bool Approved { get; set; }
    }

    public PairingStartDto? Start(string? deviceName, NetworkZone zone, string? address)
    {
        Sweep();
        if (_pending.Count >= MaxWaiting || _pending.Values.Count(p => p.Address == address) >= MaxWaitingPerAddress)
        {
            return null;
        }

        var code = new string(Enumerable.Range(0, 6).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var pending = new Pending(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), code, Name(deviceName) ?? "A TV",
            clock.GetUtcNow().UtcDateTime + Lifetime, zone, address);
        _pending[pending.Id] = pending;
        return new PairingStartDto(pending.Id, Format(code), pending.ExpiresAt);
    }

    /// <summary>What the TV polls. Its token is made and handed over once, then the pairing is gone.</summary>
    public PairingStatusDto Status(string id)
    {
        Sweep();
        if (!_pending.TryGetValue(id, out var pending))
        {
            return new PairingStatusDto("expired", null);
        }

        if (!pending.Approved)
        {
            return new PairingStatusDto("waiting", null);
        }

        if (!_pending.TryRemove(id, out _))
        {
            return new PairingStatusDto("expired", null);
        }

        var (_, token) = signIns.Issue(SignInKind.Device, pending.DeviceName, pending.Zone, pending.Address);
        return new PairingStatusDto("approved", token);
    }

    public PairingDetailsDto? Find(string code) =>
        ByCode(code) is { } p ? new PairingDetailsDto(Format(p.Code), p.DeviceName, p.ExpiresAt, p.Address, p.Zone) : null;

    public bool Approve(string code, string? deviceName = null)
    {
        if (ByCode(code) is not { } pending)
        {
            return false;
        }

        pending.DeviceName = Name(deviceName) ?? pending.DeviceName;
        pending.Approved = true;
        return true;
    }

    /// <summary>"That's not my TV", or not now: the code is dropped, so the TV gives up on it and shows a new one.</summary>
    public bool Decline(string code) => ByCode(code) is { } pending && _pending.TryRemove(pending.Id, out _);

    private static string? Name(string? name) => string.IsNullOrWhiteSpace(name) ? null : name.Trim()[..Math.Min(name.Trim().Length, 60)];

    private Pending? ByCode(string code)
    {
        Sweep();
        var normalized = new string(code.ToUpperInvariant().Where(char.IsAsciiLetterOrDigit).ToArray());
        return _pending.Values.FirstOrDefault(p => !p.Approved && p.Code == normalized);
    }

    private void Sweep()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        foreach (var expired in _pending.Values.Where(p => p.ExpiresAt < now))
        {
            _pending.TryRemove(expired.Id, out _);
        }
    }

    private static string Format(string code) => $"{code[..3]}-{code[3..]}";
}

public static class PairingEndpoints
{
    /// <summary>
    /// The TV's side: no key yet, so these are anonymous (the id is a 128-bit secret). Only from the home network or
    /// tailnet: a stranger on the internet could otherwise name their device "Living room TV" and send you its link.
    /// And only addressed to one of this server's names, so a web page open on your network can't use your browser to
    /// ask (DNS rebinding), then read the token once you approve it.
    /// </summary>
    public static RouteGroupBuilder MapPairingDeviceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/pairing").WithTags("Pairing").AllowAnonymous();
        group.MapPost("/", (PairingStartRequest request, HttpContext http, Pairings pairings, IOptionsMonitor<FlexarrOptions> options) =>
        {
            var zone = NetworkZones.Of(http);
            if (zone == NetworkZone.Internet || !NetworkZones.AddressedToKnownHost(http, options.CurrentValue.KnownHosts))
            {
                return Results.Problem("A TV can only connect this way from your home network or over Tailscale. Type the API key on the TV instead.", statusCode: 403);
            }

            return pairings.Start(request.DeviceName, zone, http.Connection.RemoteIpAddress?.ToString()) is { } started
                ? Results.Ok(started)
                : Results.Problem("Too many devices are waiting to connect. Try again in a few minutes.", statusCode: 429);
        });
        group.MapGet("/{id}", (string id, HttpContext http, Pairings pairings, IOptionsMonitor<FlexarrOptions> options) =>
            NetworkZones.AddressedToKnownHost(http, options.CurrentValue.KnownHosts)
                ? Results.Ok(pairings.Status(id))
                : Results.Problem("Connect to this server at its own address.", statusCode: 403));
        return api;
    }

    /// <summary>The approver's side: the main profile, signed in to the web app.</summary>
    public static RouteGroupBuilder MapPairingApprovalEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/pairing/code").WithTags("Pairing").RequireAuthorization(FlexarrClaims.AdminPolicy);
        group.MapGet("/{code}", (string code, Pairings pairings) =>
            pairings.Find(code) is { } details ? Results.Ok(details) : Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404));
        // Approval takes a JSON body, which a form on another website can't send.
        group.MapPost("/{code}/approve", (string code, PairingApprovalRequest approval, Pairings pairings) =>
            pairings.Approve(code, approval.DeviceName) ? Results.NoContent() : Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404));
        // Declining is harmless either way, so a missing code isn't an error.
        group.MapPost("/{code}/decline", (string code, Pairings pairings) =>
        {
            pairings.Decline(code);
            return Results.NoContent();
        });
        return api;
    }
}
