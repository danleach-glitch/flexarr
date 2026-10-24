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

            if (signIn.Kind == SignInKind.Browser && now - signIn.LastUsedAt > BrowserIdleLimit)
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

        var list = new List<SignIn>();
        try
        {
            if (File.Exists(FilePath))
            {
                list = JsonSerializer.Deserialize<List<SignIn>>(File.ReadAllText(FilePath), Json) ?? [];
            }
        }
        catch (JsonException ex)
        {
            // A damaged file signs everyone out (the API key still works) rather than stopping the server.
            LogUnreadable(FilePath, ex.Message);
        }

        foreach (var signIn in list)
        {
            _savedLastUsed[signIn.Id] = signIn.LastUsedAt;
        }

        return _byHash = list.ToDictionary(s => s.TokenHash, StringComparer.Ordinal);
    }

    private void SaveLocked()
    {
        var all = Loaded().Values.OrderBy(s => s.CreatedAt).ToList();
        Directory.CreateDirectory(options.CurrentValue.DataDirectory);
        ServerConfigStore.WritePrivately(FilePath, JsonSerializer.Serialize(all, Json));
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
}
