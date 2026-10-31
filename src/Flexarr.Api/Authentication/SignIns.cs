using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Paths;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

public enum SignInKind
{
    /// <summary>A web browser, holding its token in an HttpOnly cookie.</summary>
    Browser,

    /// <summary>An app (an Apple TV, iPhone or Android device), sending its token as <c>X-Api-Key</c>.</summary>
    Device,
}

/// <summary>What a device is, as its app says in <c>X-Flexarr-Device</c>, so lists can name and draw it.</summary>
public enum DeviceType
{
    AppleTv,
    IPhone,
    IPad,
    AndroidPhone,
    AndroidTablet,
    AndroidTv,
}

public static class DeviceTypes
{
    public const string Header = "X-Flexarr-Device";

    /// <summary>The type the request's app gives, or null when it gives none (apps from before 0.54) or one unknown here.</summary>
    public static DeviceType? Of(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var said = request.Headers[Header].ToString();
        return Enum.TryParse<DeviceType>(said, ignoreCase: true, out var type) && Enum.IsDefined(type) && !said.Any(char.IsDigit) ? type : null;
    }
}

/// <summary>A browser or device signed in to this server. Only a hash of its token is kept.</summary>
public sealed class SignIn
{
    /// <summary>A public handle for listing and revoking; not the token.</summary>
    public required string Id { get; init; }
    public required SignInKind Kind { get; init; }
    public required string Name { get; set; }
    public required string TokenHash { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime LastUsedAt { get; set; }

    /// <summary>Where it signed in or was paired from.</summary>
    public NetworkZone Zone { get; init; }
    public string? Address { get; init; }

    /// <summary>The profile chosen on it; null is the main profile. Only the main profile may manage the server.</summary>
    public string? ProfileId { get; set; }

    /// <summary>For a device, what it is, as its app last said; null until an app that says has used it.</summary>
    public DeviceType? DeviceType { get; set; }

    /// <summary>
    /// A browser that signed in by giving the admin PIN (over Tailscale, or at home with "only away from
    /// home") rather than with the API key. It's signed out sooner when left unused, and when the admin PIN changes.
    /// </summary>
    public bool PinVerified { get; init; }

    /// <summary>
    /// For a <see cref="PinVerified"/> sign-in, the Tailscale login it was earned with: it only works for requests that
    /// come with that login, and goes when the login is unlinked. Without one, it was earned at home (the home network
    /// signing people in), and only works from home while that's so.
    /// </summary>
    public string? TailscaleLogin { get; init; }

    /// <summary>
    /// Locked to kids profiles: only they can be chosen on it, until the admin PIN releases it (there, or
    /// from Settings › Devices).
    /// </summary>
    public bool KidsLock { get; set; }

    /// <summary>
    /// A family guest's device, joined to the tailnet with an invite: locked to <see cref="ProfileId"/>, never trusted,
    /// and only valid from <see cref="GuestAddress"/>. See docs/remote-guests.md.
    /// </summary>
    public bool Guest { get; init; }

    /// <summary>For a guest, the tailnet address of the device that redeemed the invite.</summary>
    public string? GuestAddress { get; init; }
}

/// <summary>
/// Per-browser and per-device sign-ins, kept in &lt;DataDirectory&gt;/devices.json (readable only by this user). Each
/// gets its own random token, so one can be signed out without touching the others or the API key.
/// </summary>
public sealed partial class SignIns(IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<SignIns> logger)
{
    public const string FileName = "devices.json";

    /// <summary>Browsers that haven't been used for this long are signed out.</summary>
    public static readonly TimeSpan BrowserIdleLimit = TimeSpan.FromDays(180);

    /// <summary>Browsers signed in with the admin PIN are signed out after this long unused.</summary>
    public static readonly TimeSpan PinSignInIdleLimit = TimeSpan.FromDays(14);

    /// <summary>
    /// A phone, tablet or TV unused this long is signed out, so one that was lost, sold or given away stops working in
    /// time. Long enough for a TV in a holiday home.
    /// </summary>
    public static readonly TimeSpan DeviceIdleLimit = TimeSpan.FromDays(365);

    /// <summary>How stale the saved "last used" may get before it's written again (it's kept exactly in memory).</summary>
    private static readonly TimeSpan LastUsedPrecision = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly Lock _lock = new();
    private Dictionary<string, SignIn>? _byHash;
    private readonly Dictionary<string, DateTime> _savedLastUsed = new(StringComparer.Ordinal);

    private string FilePath => Path.Combine(options.CurrentValue.DataDirectory, FileName);

    /// <summary>Creates a sign-in and returns its token, which is shown once and never stored.</summary>
    public (SignIn SignIn, string Token) Issue(SignInKind kind, string name, NetworkZone zone, string? address, string? profileId = null, bool pinVerified = false,
        string? tailscaleLogin = null, DeviceType? deviceType = null)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var signIn = new SignIn
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Kind = kind,
            Name = name.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, 80)] : kind == SignInKind.Browser ? "A browser" : "A device",
            TokenHash = Hash(token),
            CreatedAt = now,
            LastUsedAt = now,
            Zone = zone,
            Address = address,
            ProfileId = profileId,
            PinVerified = pinVerified,
            TailscaleLogin = tailscaleLogin,
            DeviceType = deviceType,
        };

        lock (_lock)
        {
            Loaded()[signIn.TokenHash] = signIn;
            SaveLocked();
        }

        LogIssued(kind, PathComparison.Printable(signIn.Name), zone);
        FirstRun.Close(options.CurrentValue);
        return (signIn, token);
    }

    /// <summary>
    /// Signs in a family guest's device at tailnet address <paramref name="address"/>, locked to
    /// <paramref name="profileId"/>. Another guest sign-in for the same address (the device used a second invite) is
    /// replaced.
    /// </summary>
    public (SignIn SignIn, string Token) IssueGuest(string name, string profileId, string address, DeviceType? deviceType)
    {
        var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        var now = clock.GetUtcNow().UtcDateTime;
        var signIn = new SignIn
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            Kind = SignInKind.Device,
            Name = name.Trim() is { Length: > 0 } trimmed ? trimmed[..Math.Min(trimmed.Length, 80)] : "A guest",
            // Kept under a prefix an older server never looks up: rolled back to a version without family invites,
            // a guest's token matches nothing, rather than passing for a trusted household device.
            TokenHash = GuestHashPrefix + Hash(token),
            CreatedAt = now,
            LastUsedAt = now,
            Zone = NetworkZone.Tailnet,
            Address = address,
            ProfileId = profileId,
            DeviceType = deviceType,
            Guest = true,
            GuestAddress = address,
        };

        lock (_lock)
        {
            foreach (var old in Loaded().Values.Where(s => s.Guest && s.GuestAddress == address).ToList())
            {
                Loaded().Remove(old.TokenHash);
            }

            Loaded()[signIn.TokenHash] = signIn;
            SaveLocked();
        }

        LogIssued(SignInKind.Device, PathComparison.Printable(signIn.Name), NetworkZone.Tailnet);
        return (signIn, token);
    }

    /// <summary>Whether any family guest is signed in (so the guest gate has someone to keep out once removed).</summary>
    public bool HasAnyGuest
    {
        get
        {
            lock (_lock)
            {
                return Loaded().Values.Any(s => s.Guest);
            }
        }
    }

    /// <summary>Whether a family guest's device at <paramref name="address"/> is signed in (not removed or expired).</summary>
    public bool HasGuestAt(string address)
    {
        lock (_lock)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return Loaded().Values.Any(s => s.Guest && s.GuestAddress == address && !Expired(s, now));
        }
    }

    /// <summary>The sign-in a token belongs to, if it's still valid; notes that it was used.</summary>
    public SignIn? Find(string token, SignInKind kind)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256)
        {
            return null;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            // A household sign-in, or a guest's under its prefix (only ever a guest's).
            var hash = Hash(token);
            if (!Loaded().TryGetValue(hash, out var signIn) || signIn.Guest)
            {
                hash = GuestHashPrefix + hash;
                if (!Loaded().TryGetValue(hash, out signIn) || !signIn.Guest)
                {
                    return null;
                }
            }

            if (signIn.Kind != kind)
            {
                return null;
            }

            if (Expired(signIn, now))
            {
                Loaded().Remove(hash);
                SaveLocked();
                return null;
            }

            signIn.LastUsedAt = now;
            if (now - _savedLastUsed.GetValueOrDefault(signIn.Id) > LastUsedPrecision)
            {
                SaveLocked();
            }

            return signIn;
        }
    }

    /// <summary>Notes what a device says it is (an app that has started saying, or a sign-in moved to a new device).</summary>
    public void NoteDeviceType(SignIn signIn, DeviceType? type)
    {
        ArgumentNullException.ThrowIfNull(signIn);
        if (type is null || signIn.DeviceType == type)
        {
            return;
        }

        lock (_lock)
        {
            signIn.DeviceType = type;
            SaveLocked();
        }
    }

    public IReadOnlyList<SignIn> List()
    {
        lock (_lock)
        {
            // Browsers left unused too long are signed out here as well, not only when they next turn up.
            var now = clock.GetUtcNow().UtcDateTime;
            var expired = Loaded().Values.Where(s => Expired(s, now)).ToList();
            foreach (var signIn in expired)
            {
                Loaded().Remove(signIn.TokenHash);
            }

            if (expired.Count > 0)
            {
                SaveLocked();
            }

            return Loaded().Values.OrderByDescending(s => s.LastUsedAt).ToList();
        }
    }

    public bool Revoke(string id)
    {
        lock (_lock)
        {
            var found = Loaded().Values.FirstOrDefault(s => s.Id == id);
            if (found is null)
            {
                return false;
            }

            Loaded().Remove(found.TokenHash);
            SaveLocked();
            LogRevoked(found.Kind, PathComparison.Printable(found.Name));
            return true;
        }
    }

    /// <summary>Gives a browser or device a name people recognise ("Kitchen iPad"); false if it isn't signed in.</summary>
    public bool Rename(string id, string name)
    {
        lock (_lock)
        {
            if (Loaded().Values.FirstOrDefault(s => s.Id == id) is not { } signIn)
            {
                return false;
            }

            var trimmed = name.Trim();
            if (trimmed.Length > 0 && trimmed != signIn.Name)
            {
                signIn.Name = trimmed[..Math.Min(trimmed.Length, 80)];
                SaveLocked();
            }

            return true;
        }
    }

    /// <summary>Signs out every browser and device except <paramref name="keepId"/> (the one asking); returns how many.</summary>
    public int RevokeAllExcept(string? keepId)
    {
        lock (_lock)
        {
            var others = Loaded().Values.Where(s => s.Id != keepId).ToList();
            foreach (var signIn in others)
            {
                Loaded().Remove(signIn.TokenHash);
            }

            if (others.Count > 0)
            {
                SaveLocked();
                LogRevokedAll(others.Count);
            }

            return others.Count;
        }
    }

    /// <summary>Signs out every browser that signed in with the admin PIN, except <paramref name="keepId"/>.</summary>
    public int RevokePinVerifiedExcept(string? keepId)
    {
        lock (_lock)
        {
            var earned = Loaded().Values.Where(s => s.PinVerified && s.Id != keepId).ToList();
            foreach (var signIn in earned)
            {
                Loaded().Remove(signIn.TokenHash);
            }

            if (earned.Count > 0)
            {
                SaveLocked();
                LogRevokedAll(earned.Count);
            }

            return earned.Count;
        }
    }

    /// <summary>Signs out every browser that signed in with the admin PIN using the Tailscale login <paramref name="login"/>.</summary>
    public int RevokeEarnedBy(string login)
    {
        lock (_lock)
        {
            var earned = Loaded().Values.Where(s => s.PinVerified && string.Equals(s.TailscaleLogin, login, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var signIn in earned)
            {
                Loaded().Remove(signIn.TokenHash);
            }

            if (earned.Count > 0)
            {
                SaveLocked();
                LogRevokedAll(earned.Count);
            }

            return earned.Count;
        }
    }

    /// <summary>
    /// Locks a browser or device to kids profiles, choosing <paramref name="profileId"/> (a kids profile) on it, or
    /// releases it; false if it isn't signed in.
    /// </summary>
    public bool SetKidsLock(string id, bool locked, string? profileId = null)
    {
        lock (_lock)
        {
            if (Loaded().Values.FirstOrDefault(s => s.Id == id) is not { } signIn)
            {
                return false;
            }

            signIn.KidsLock = locked;
            if (profileId is not null)
            {
                signIn.ProfileId = profileId;
            }

            SaveLocked();
            return true;
        }
    }

    /// <summary>The browser or device sign-in <paramref name="id"/>, if it's still signed in.</summary>
    public SignIn? ById(string? id)
    {
        if (id is null)
        {
            return null;
        }

        lock (_lock)
        {
            return Loaded().Values.FirstOrDefault(s => s.Id == id);
        }
    }

    /// <summary>Remembers who's watching on a browser or device (null: the main profile).</summary>
    public void ChooseProfile(string id, string? profileId)
    {
        lock (_lock)
        {
            if (Loaded().Values.FirstOrDefault(s => s.Id == id) is { } signIn && signIn.ProfileId != profileId)
            {
                signIn.ProfileId = profileId;
                SaveLocked();
            }
        }
    }

    /// <summary>What a guest sign-in's token hash starts with (see <see cref="IssueGuest"/>).</summary>
    public const string GuestHashPrefix = "guest:";

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private Dictionary<string, SignIn> Loaded()
    {
        if (_byHash is not null)
        {
            return _byHash;
        }

        List<SignIn?> list = [];
        try
        {
            if (File.Exists(FilePath))
            {
                list = JsonSerializer.Deserialize<List<SignIn?>>(File.ReadAllText(FilePath), Json) ?? [];
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // A damaged or unreadable file (made root's by a sudo run, say) signs everyone out (the API key still
            // works) rather than stopping the server. It's kept aside, not overwritten, in case it can be put back.
            LogUnreadable(FilePath, ex.Message);
            _keepAsideBeforeSaving = true;
        }

        // Skip what a hand edit may have left: empty entries, and a token or id listed twice.
        var byHash = new Dictionary<string, SignIn>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var now = clock.GetUtcNow().UtcDateTime;
        var pruned = false;
        foreach (var signIn in list)
        {
            if (signIn is null || string.IsNullOrEmpty(signIn.TokenHash) || string.IsNullOrEmpty(signIn.Id) || byHash.ContainsKey(signIn.TokenHash) || !ids.Add(signIn.Id))
            {
                continue;
            }

            if (Expired(signIn, now))
            {
                pruned = true;
                continue;
            }

            signIn.Name ??= signIn.Kind == SignInKind.Browser ? "A browser" : "A device";
            byHash[signIn.TokenHash] = signIn;
            _savedLastUsed[signIn.Id] = signIn.LastUsedAt;
        }

        _byHash = byHash;
        if (pruned)
        {
            SaveLocked();
        }

        return byHash;
    }

    /// <summary>
    /// A browser left unused for six months (two weeks, if it signed in with the PIN) is signed out, and a device left
    /// unused for a year.
    /// </summary>
    private static bool Expired(SignIn signIn, DateTime now) =>
        now - signIn.LastUsedAt > (signIn.Kind != SignInKind.Browser ? DeviceIdleLimit : signIn.PinVerified ? PinSignInIdleLimit : BrowserIdleLimit);

    private bool _keepAsideBeforeSaving;

    /// <summary>
    /// Writes the list. If that fails (a full disk, a folder that isn't ours), sign-ins carry on in memory, and the
    /// failure is logged rather than failing the request.
    /// </summary>
    private void SaveLocked()
    {
        var all = Loaded().Values.OrderBy(s => s.CreatedAt).ToList();
        try
        {
            Directory.CreateDirectory(options.CurrentValue.DataDirectory);
            if (_keepAsideBeforeSaving && File.Exists(FilePath))
            {
                var aside = $"{FilePath}.unreadable-{clock.GetUtcNow():yyyyMMddHHmmss}";
                File.Move(FilePath, aside, overwrite: true);
                LogKeptAside(aside);
            }

            _keepAsideBeforeSaving = false;
            ServerConfigStore.WritePrivately(FilePath, JsonSerializer.Serialize(all, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnwritable(FilePath, ex.Message);
            return;
        }

        _savedLastUsed.Clear();
        foreach (var signIn in all)
        {
            _savedLastUsed[signIn.Id] = signIn.LastUsedAt;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed in a new {Kind}, \"{Name}\", from the {Zone} zone")]
    private partial void LogIssued(SignInKind kind, string name, NetworkZone zone);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed out the {Kind} \"{Name}\"")]
    private partial void LogRevoked(SignInKind kind, string name);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read {Path} ({Reason}); browsers and devices will need to sign in again")]
    private partial void LogUnreadable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed out {Count} browsers and devices")]
    private partial void LogRevokedAll(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Kept the unreadable sign-in list as {Path}")]
    private partial void LogKeptAside(string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save {Path} ({Reason}); sign-ins made or signed out now last until Flexarr restarts")]
    private partial void LogUnwritable(string path, string reason);
}
