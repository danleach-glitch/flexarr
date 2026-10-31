using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Notifications;
using Flexarr.Api.Profiles;
using Flexarr.Core.Paths;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <param name="StopsWhenDeclined">
/// The app says "Not allowed" itself after "Don't allow", and asks again only when someone chooses Try again. Apps from
/// before 0.56 send nothing and ask for a new code by themselves at once, which isn't asked about (see Decline).
/// </param>
public sealed record PairingStartRequest(string? DeviceName, bool StopsWhenDeclined = false);

/// <param name="Id">The device's secret handle for polling; never shown to people.</param>
/// <param name="Code">What a person approves: shown on the TV and in its QR code.</param>
public sealed record PairingStartDto(string Id, string Code, DateTime ExpiresAt);

/// <param name="Status">"waiting", "approved" (with the device's own token, given once) or "expired".</param>
/// <param name="ApiKey">The token the TV sends as X-Api-Key from now on. Not the server's API key: it can be signed out
/// on its own, from Settings.</param>
/// <param name="Declined">With "expired": it ended because the main profile chose "Don't allow". Apps from before 0.56
/// don't read it.</param>
public sealed record PairingStatusDto(string Status, string? ApiKey, bool Declined = false);

/// <param name="RequestedFrom">The address the TV asked from, so the approver can tell it's theirs.</param>
/// <param name="ExpiresAt">Moved on while the device is still showing its code (see <see cref="Pairings.Status"/>).</param>
/// <param name="DeviceType">What kind of device it says it is, when it says.</param>
/// <param name="TurnedAway">
/// The device asking again by itself just after "Don't allow": listed, so it can still be allowed, but not to be asked
/// about (no prompt, no notification).
/// </param>
public sealed record PairingDetailsDto(string Code, string DeviceName, DateTime ExpiresAt, string? RequestedFrom, NetworkZone Zone, DeviceType? DeviceType = null,
    bool TurnedAway = false);

/// <param name="DeviceName">A name to list the TV under instead of the one it gave.</param>
public sealed record PairingApprovalRequest(string? DeviceName = null);

/// <summary>
/// What the main profile's devices hear as connections come and go: a device now waiting for approval, or (with
/// <paramref name="Ended"/>, its code) one approved, declined or expired, so a prompt still showing it can close.
/// </summary>
public sealed record PairingNotice(PairingDetailsDto? Waiting, string? Ended);

public enum PairingApproval
{
    Approved,
    NotWaiting,

    /// <summary>As many TVs as may be connected in an hour have been.</summary>
    TooMany,
}

/// <summary>
/// Connecting a TV without typing: the TV, on the home network or tailnet, asks for a code and shows it (and a QR code
/// linking to it); someone signed in as the main profile approves it in the web app; the TV, polling with its secret
/// id, is handed a token of its own. Codes last ten minutes and only a few can be waiting at once, from any one address
/// or altogether. At most ten are approved in an hour: each is a sign-in of its own, so a script that got into the
/// approver's page mustn't be able to make them by the thousand. The main profile's phones are told as codes start
/// (see <see cref="Listen"/> and <see cref="PairingAlerts"/>), so approving is a tap on one of them.
/// </summary>
public sealed class Pairings(SignIns signIns, TimeProvider clock)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private const int MaxWaiting = 10;
    private const int MaxWaitingPerAddress = 3;
    private const int MaxApprovedPerHour = 10;
    private static readonly TimeSpan ApprovalWindow = TimeSpan.FromHours(1);

    /// <summary>With this little left, a code still being shown is given longer (see <see cref="Status"/>).</summary>
    private static readonly TimeSpan RenewWithin = TimeSpan.FromMinutes(2);

    /// <summary>How soon after "Don't allow" the device's next code counts as its own retry, not asked about.</summary>
    private static readonly TimeSpan DeclinedQuiet = TimeSpan.FromMinutes(1);

    // No 0/O, 1/I/L: codes are read off a TV and typed on a phone.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private readonly ConcurrentDictionary<string, Pending> _pending = new(StringComparer.Ordinal);
    private readonly Queue<DateTime> _approvedAt = new();
    private readonly ConcurrentDictionary<Listener, byte> _listeners = new();

    /// <summary>Addresses just turned away, and until when the next code from each is its own retry.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _declined = new(StringComparer.Ordinal);

    /// <summary>Pairings turned away, by id, and until when the device polling one is told so.</summary>
    private readonly ConcurrentDictionary<string, DateTime> _declinedIds = new(StringComparer.Ordinal);

    /// <summary>
    /// The code each device turned away was showing (by address, name and kind), and until when Try again gets it back:
    /// the code on the phones stays the one on the device's screen, rather than changing with every try.
    /// </summary>
    private readonly ConcurrentDictionary<string, (string Code, DateTime Until)> _declinedCodes = new(StringComparer.Ordinal);

    /// <summary>One open command stream of the main profile's. Disposing it (the request ended) stops its notices.</summary>
    public sealed class Listener : IDisposable
    {
        private readonly Pairings _owner;

        internal Listener(Pairings owner) => _owner = owner;

        internal Channel<PairingNotice> Channel { get; } = System.Threading.Channels.Channel.CreateUnbounded<PairingNotice>(new UnboundedChannelOptions { SingleReader = true });

        public ChannelReader<PairingNotice> Notices => Channel.Reader;

        public void Dispose() => _owner._listeners.TryRemove(this, out _);
    }

    /// <summary>Starts hearing about pairings, beginning with every device already waiting.</summary>
    public Listener Listen()
    {
        var listener = new Listener(this);
        _listeners[listener] = 0;
        foreach (var waiting in Waiting())
        {
            listener.Channel.Writer.TryWrite(new PairingNotice(waiting, null));
        }

        return listener;
    }

    /// <summary>Every device waiting for approval, the soonest to expire first.</summary>
    public IReadOnlyList<PairingDetailsDto> Waiting()
    {
        Sweep();
        return [.. _pending.Values.Where(p => !p.Approved).OrderBy(p => p.ExpiresAt).Select(Details)];
    }

    private void Tell(PairingNotice notice)
    {
        foreach (var listener in _listeners.Keys)
        {
            listener.Channel.Writer.TryWrite(notice);
        }
    }

    private sealed class Pending(string id, string code, string deviceName, DateTime expiresAt, NetworkZone zone, string? address, DeviceType? deviceType, bool quiet)
    {
        public DeviceType? DeviceType { get; } = deviceType;
        public string Id { get; } = id;
        public string Code { get; } = code;
        public string DeviceName { get; set; } = deviceName;
        public DateTime ExpiresAt { get; set; } = expiresAt;

        /// <summary>A device's own retry just after "Don't allow": listed, but not asked about.</summary>
        public bool Quiet { get; } = quiet;
        public NetworkZone Zone { get; } = zone;
        public string? Address { get; } = address;
        public bool Approved { get; set; }
    }

    /// <summary>
    /// A device asking for a code. One it asked for before, still waiting, is dropped: a device shows one code at a time,
    /// and asking again (Back, then choosing the server again) mustn't use up its address's places.
    /// </summary>
    /// <param name="announced">The details to notify the main profile's phones of, or null for a device's own retry just after "Don't allow".</param>
    /// <param name="stopsWhenDeclined">From an app that says "Not allowed" itself: never its own retry, so always asked about.</param>
    public PairingStartDto? Start(string? deviceName, NetworkZone zone, string? address, out PairingDetailsDto? announced, DeviceType? deviceType = null,
        bool stopsWhenDeclined = false)
    {
        Sweep();
        announced = null;
        var name = Name(deviceName) ?? "A TV";
        foreach (var earlier in _pending.Values.Where(p => !p.Approved && p.Address == address && p.DeviceName == name && p.DeviceType == deviceType))
        {
            if (_pending.TryRemove(earlier.Id, out _))
            {
                Tell(new PairingNotice(null, Format(earlier.Code)));
            }
        }

        if (_pending.Count >= MaxWaiting || _pending.Values.Count(p => p.Address == address) >= MaxWaitingPerAddress)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var code = address is not null && _declinedCodes.TryRemove(DeviceKey(address, name, deviceType), out var declined) && declined.Until > now
            && !_pending.Values.Any(p => p.Code == declined.Code)
                ? declined.Code
                : new string(Enumerable.Range(0, 6).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        // Only the one code straight after "Don't allow" is quiet: asking again after that is someone trying again.
        var quiet = address is not null && _declined.TryRemove(address, out var until) && until > now && !stopsWhenDeclined;
        var pending = new Pending(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)), code, name,
            now + Lifetime, zone, address, deviceType, quiet);
        _pending[pending.Id] = pending;
        Tell(new PairingNotice(Details(pending), null));
        if (!quiet)
        {
            announced = Details(pending);
        }

        return new PairingStartDto(pending.Id, Format(code), pending.ExpiresAt);
    }

    /// <summary>
    /// What the TV polls. Its token is made and handed over once, then the pairing is gone. A device still polling
    /// near the end of its code's ten minutes is given ten more on the same code, so a TV left on its connect screen
    /// keeps one code (and one notification on the phones) rather than a new one every ten minutes; a code nobody's
    /// showing any more still runs out.
    /// </summary>
    public PairingStatusDto Status(string id)
    {
        Sweep();
        if (!_pending.TryGetValue(id, out var pending))
        {
            return new PairingStatusDto("expired", null, _declinedIds.ContainsKey(id));
        }

        if (!pending.Approved)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (pending.ExpiresAt - now < RenewWithin)
            {
                pending.ExpiresAt = now + Lifetime;
                Tell(new PairingNotice(Details(pending), null));
            }

            return new PairingStatusDto("waiting", null);
        }

        if (!_pending.TryRemove(id, out _))
        {
            return new PairingStatusDto("expired", null);
        }

        var (_, token) = signIns.Issue(SignInKind.Device, pending.DeviceName, pending.Zone, pending.Address, deviceType: pending.DeviceType);
        return new PairingStatusDto("approved", token);
    }

    public PairingDetailsDto? Find(string code) => ByCode(code) is { } p ? Details(p) : null;

    private static PairingDetailsDto Details(Pending p) => new(Format(p.Code), p.DeviceName, p.ExpiresAt, p.Address, p.Zone, p.DeviceType, p.Quiet);

    /// <param name="retryAfter">With <see cref="PairingApproval.TooMany"/>, how long until another may be approved.</param>
    public PairingApproval Approve(string code, string? deviceName, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (ByCode(code) is not { } pending)
        {
            return PairingApproval.NotWaiting;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        lock (_approvedAt)
        {
            while (_approvedAt.TryPeek(out var oldest) && now - oldest >= ApprovalWindow)
            {
                _approvedAt.Dequeue();
            }

            if (_approvedAt.Count >= MaxApprovedPerHour)
            {
                retryAfter = _approvedAt.Peek() + ApprovalWindow - now;
                return PairingApproval.TooMany;
            }

            if (pending.Approved)
            {
                return PairingApproval.NotWaiting; // approved alongside, by another request
            }

            pending.DeviceName = Name(deviceName) ?? pending.DeviceName;
            pending.Approved = true;
            _approvedAt.Enqueue(now);
        }

        Tell(new PairingNotice(null, Format(pending.Code)));
        return PairingApproval.Approved;
    }

    /// <summary>
    /// A name with a line break, a bidi control or another invisible character, which could forge a log line or
    /// disguise what the list of devices shows.
    /// </summary>
    public static bool UnfitName(string? name) => name is not null && PathComparison.Printable(name) != name;

    /// <summary>
    /// "That's not my TV", or not now: the code is dropped, and the device is told it was turned away. Apps from 0.56
    /// say "Not allowed" and ask again only on Try again. Older ones show a new code by themselves at once: that one is
    /// listed as turned away rather than asked about, or "Don't allow" would only bring the question straight back; it
    /// can still be allowed from the list.
    /// </summary>
    public bool Decline(string code)
    {
        if (ByCode(code) is not { } pending || !_pending.TryRemove(pending.Id, out _))
        {
            return false;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        if (pending.Address is { } address)
        {
            _declined[address] = now + DeclinedQuiet;
            _declinedCodes[DeviceKey(address, pending.DeviceName, pending.DeviceType)] = (pending.Code, now + Lifetime);
        }

        _declinedIds[pending.Id] = now + Lifetime;

        Tell(new PairingNotice(null, Format(pending.Code)));
        return true;
    }

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
            if (_pending.TryRemove(expired.Id, out _) && !expired.Approved)
            {
                Tell(new PairingNotice(null, Format(expired.Code)));
            }
        }

        foreach (var (address, until) in _declined.Where(d => d.Value < now))
        {
            _declined.TryRemove(new KeyValuePair<string, DateTime>(address, until));
        }

        foreach (var (id, until) in _declinedIds.Where(d => d.Value < now))
        {
            _declinedIds.TryRemove(new KeyValuePair<string, DateTime>(id, until));
        }

        foreach (var (key, declined) in _declinedCodes.Where(d => d.Value.Until < now))
        {
            _declinedCodes.TryRemove(new KeyValuePair<string, (string, DateTime)>(key, declined));
        }
    }

    private static string DeviceKey(string address, string name, DeviceType? deviceType) => $"{address}\n{name}\n{deviceType}";

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
        group.MapPost("/", (PairingStartRequest request, HttpContext http, Pairings pairings, PairingAlerts alerts, IOptionsMonitor<FlexarrOptions> options) =>
        {
            var zone = NetworkZones.Of(http);
            if (zone == NetworkZone.Internet || !NetworkZones.AddressedToKnownHost(http, options.CurrentValue.KnownHosts))
            {
                return Results.Problem("A device can only connect with a code from your home network or over Tailscale. Use the API key instead.", statusCode: 403);
            }

            if (Pairings.UnfitName(request.DeviceName))
            {
                return Results.Problem(UnfitNameMessage, statusCode: 400);
            }

            if (pairings.Start(request.DeviceName, zone, http.Connection.RemoteIpAddress?.ToString(), out var announced, DeviceTypes.Of(http.Request),
                    request.StopsWhenDeclined) is not { } started)
            {
                return Results.Problem("Too many devices are waiting to connect. Try again in a few minutes.", statusCode: 429);
            }

            if (announced is not null)
            {
                alerts.Send(announced);
            }

            return Results.Ok(started);
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
        // What's waiting, for the web app's prompt (the apps hear it on their command stream; see DeviceEndpoints).
        // The main profile sees it before it's unlocked; approving takes the unlock.
        api.MapGet("/pairing/waiting", (Pairings pairings) => Results.Ok(pairings.Waiting()))
            .WithTags("Pairing").RequireAuthorization(FlexarrClaims.MainProfilePolicy);
        api.MapGet("/pairing/code/{code}", (string code, Pairings pairings) =>
            pairings.Find(code) is { } details ? Results.Ok(details) : Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404))
            .WithTags("Pairing").RequireAuthorization(FlexarrClaims.MainProfilePolicy);
        // Declining is harmless either way, so a missing code isn't an error.
        api.MapPost("/pairing/code/{code}/decline", (string code, Pairings pairings) =>
        {
            pairings.Decline(code);
            return Results.NoContent();
        }).WithTags("Pairing").RequireAuthorization(FlexarrClaims.MainProfilePolicy);
        var group = api.MapGroup("/pairing/code").WithTags("Pairing").RequireAuthorization(FlexarrClaims.AdminPolicy);
        // Approval takes a JSON body, which a form on another website can't send.
        group.MapPost("/{code}/approve", (string code, PairingApprovalRequest approval, HttpContext http, Pairings pairings) =>
        {
            if (Pairings.UnfitName(approval.DeviceName))
            {
                return Results.Problem(UnfitNameMessage, statusCode: 400);
            }

            return pairings.Approve(code, approval.DeviceName, out var retryAfter) switch
            {
                PairingApproval.Approved => Results.NoContent(),
                PairingApproval.TooMany => ProfileEndpoints.TryAgainLater(http, retryAfter,
                    when => $"Ten TVs have been connected in the last hour, which is as many as Flexarr allows. Try again in {when}."),
                _ => Results.Problem("That code isn't waiting to connect. It may have expired; the TV shows a new one.", statusCode: 404),
            };
        });
        return api;
    }

    private const string UnfitNameMessage = "That name has a line break or a hidden character in it. Use letters, numbers and spaces.";
}
