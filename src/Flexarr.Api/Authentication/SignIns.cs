using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

public enum SignInKind
{
    /// <summary>A web browser, holding its token in an HttpOnly cookie.</summary>
    Browser,

    /// <summary>An app (the Apple TV), sending its token as <c>X-Api-Key</c>.</summary>
    Device,
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
    public (SignIn SignIn, string Token) Issue(SignInKind kind, string name, NetworkZone zone, string? address, string? profileId = null)
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
        };

        lock (_lock)
        {
            Loaded()[signIn.TokenHash] = signIn;
            SaveLocked();
        }

        LogIssued(kind, signIn.Name, zone);
        return (signIn, token);
    }

    /// <summary>The sign-in a token belongs to, if it's still valid; notes that it was used.</summary>
    public SignIn? Find(string token, SignInKind kind)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 256)
        {
            return null;
        }

        var hash = Hash(token);
        var now = clock.GetUtcNow().UtcDateTime;
        lock (_lock)
        {
            if (!Loaded().TryGetValue(hash, out var signIn) || signIn.Kind != kind)
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
            LogRevoked(found.Kind, found.Name);
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

    /// <summary>A browser left unused for six months is signed out.</summary>
    private static bool Expired(SignIn signIn, DateTime now) => signIn.Kind == SignInKind.Browser && now - signIn.LastUsedAt > BrowserIdleLimit;

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
