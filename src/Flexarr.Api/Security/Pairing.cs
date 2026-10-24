using System.Collections.Concurrent;
using System.Security.Cryptography;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

public sealed record PairingStartRequest(string? DeviceName);

/// <param name="Id">The device's secret handle for polling; never shown to people.</param>
/// <param name="Code">What a person approves: shown on the TV and in its QR code.</param>
public sealed record PairingStartDto(string Id, string Code, DateTime ExpiresAt);

/// <param name="Status">"waiting", "approved" (with the API key, given once) or "expired".</param>
public sealed record PairingStatusDto(string Status, string? ApiKey);

public sealed record PairingDetailsDto(string Code, string DeviceName, DateTime ExpiresAt);

/// <summary>
/// Connecting a TV without typing: the TV asks for a code and shows it (and a QR code linking to it); someone signed
/// in as the main profile approves it in the web app; the TV, polling with its secret id, is handed the API key.
/// Codes last ten minutes and only a few can be waiting at once.
/// </summary>
public sealed class Pairings(IOptionsMonitor<FlexarrOptions> options, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxWaiting = 10;

    // No 0/O, 1/I/L: codes are read off a TV and typed on a phone.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    private sealed class Pending(string id, string code, string deviceName, DateTime expiresAt)
    {
        public string Id { get; } = id;
        public string Code { get; } = code;
        public string DeviceName { get; } = deviceName;
        public DateTime ExpiresAt { get; } = expiresAt;
        public bool Approved { get; set; }
    }

    public PairingStartDto? Start(string? deviceName)
    {
        Sweep();
        if (_pending.Count >= MaxWaiting)
        {
            return null;
        }

        var code = new string(Enumerable.Range(0, 6).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        var pending = new Pending(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), code,
            string.IsNullOrWhiteSpace(deviceName) ? "A TV" : deviceName.Trim()[..Math.Min(deviceName.Trim().Length, 60)],
            clock.GetUtcNow().UtcDateTime + Lifetime);
        _pending[pending.Id] = pending;
        return new PairingStartDto(pending.Id, Format(code), pending.ExpiresAt);
    }

    /// <summary>What the TV polls. The API key is handed over once, then the pairing is gone.</summary>
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

        _pending.TryRemove(id, out _);
        return new PairingStatusDto("approved", options.CurrentValue.ApiKey);
    }

    public PairingDetailsDto? Find(string code) =>
        ByCode(code) is { } p ? new PairingDetailsDto(Format(p.Code), p.DeviceName, p.ExpiresAt) : null;

    public bool Approve(string code)
    {
        if (ByCode(code) is not { } pending)
        {
            return false;
        }

        pending.Approved = true;
        return true;
    }

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
    /// <summary>The TV's side: no API key yet, so these are anonymous (the id is a 128-bit secret).</summary>
    public static RouteGroupBuilder MapPairingDeviceEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/pairing").WithTags("Pairing").AllowAnonymous();
        group.MapPost("/", (PairingStartRequest request, Pairings pairings) =>
            pairings.Start(request.DeviceName) is { } started
                ? Results.Ok(started)
                : Results.Problem("Too many devices are waiting to connect. Try again in a few minutes.", statusCode: 429));
        group.MapGet("/{id}", (string id, Pairings pairings) => pairings.Status(id));
        return api;
    }

    /// <summary>The approver's side: the main profile, signed in to the web app.</summary>
    public static RouteGroupBuilder MapPairingApprovalEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/pairing/code").WithTags("Pairing").RequireAuthorization(FlexarrClaims.AdminPolicy);
        group.MapGet("/{code}", (string code, Pairings pairings) =>
            pairings.Find(code) is { } details ? Results.Ok(details) : Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404));
        group.MapPost("/{code}/approve", (string code, Pairings pairings) =>
            pairings.Approve(code) ? Results.NoContent() : Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404));
        return api;
    }
}
