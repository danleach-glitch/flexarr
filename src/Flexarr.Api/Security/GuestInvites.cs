using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Core.Paths;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>How an invite reached its guest.</summary>
public enum InviteVia
{
    /// <summary>A link or QR code, for a phone or tablet.</summary>
    Link,

    /// <summary>A code typed in from the guest's TV, through the relay.</summary>
    TvCode,
}

/// <summary>An invite waiting to be used. Only a hash of its token is kept; the token is in the guest's link.</summary>
public sealed class GuestInvite
{
    public required string Id { get; init; }
    public required string TokenHash { get; init; }

    /// <summary>What the guest's device is called in lists ("Gran's iPad").</summary>
    public required string Name { get; init; }

    /// <summary>The one profile the guest gets.</summary>
    public required string ProfileId { get; init; }
    public InviteVia Via { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime ExpiresAt { get; init; }

    /// <summary>The Tailscale auth key's id, so a cancelled invite's key can be deleted.</summary>
    public string? KeyId { get; init; }
}

/// <summary>What the guest's app needs, carried in the invite link (base64url JSON after the #).</summary>
/// <param name="V">Format version.</param>
/// <param name="N">The owner's name, for "Join Dan's Flexarr?".</param>
/// <param name="S">The server's id, which the app checks once it's connected.</param>
/// <param name="A">The server's tailnet address and Flexarr's port.</param>
/// <param name="K">The one-use Tailscale auth key.</param>
/// <param name="T">The one-time Flexarr token, swapped for a sign-in.</param>
/// <param name="G">What the guest's device will be called.</param>
/// <param name="P">The profile's name.</param>
/// <param name="E">When the invite stops working.</param>
public sealed record InvitePayload(int V, string N, string S, string A, string K, string T, string G, string P, DateTime E);

public sealed class InviteException(string message, int status = StatusCodes.Status409Conflict) : Exception(message)
{
    public int Status { get; } = status;
}

/// <summary>
/// Family invites: made by the owner, each holding a one-use Tailscale key and a one-time token, kept in
/// &lt;DataDirectory&gt;/invites.json (readable only by this user, tokens hashed). Redeeming one from a guest device on
/// the tailnet gives that device a guest sign-in locked to the invite's profile. See docs/remote-guests.md.
/// </summary>
public sealed partial class GuestInvites(IOptionsMonitor<FlexarrOptions> options, TailscaleApi api, GuestPolicy policy, ITailnet tailnet, SignIns signIns,
    TimeProvider clock, ILogger<GuestInvites> logger)
{
    public const string FileName = "invites.json";

    /// <summary>The profiles that are guests: anyone ever invited, kept until their profile goes (see <see cref="GuestProfiles"/>).</summary>
    public const string GuestsFileName = "guests.json";

    /// <summary>How long an unused invite lasts.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(3);

    /// <summary>The most invites waiting at once.</summary>
    public const int MaxWaiting = 20;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly Lock _lock = new();
    private List<GuestInvite>? _invites;

    private string FilePath => Path.Combine(options.CurrentValue.DataDirectory, FileName);

    /// <summary>Invites still waiting, newest first.</summary>
    /// <summary>
    /// The profiles that are guests, so Settings lists one even after their last device is removed (their profile, and
    /// what they've watched, stay until the owner removes the guest). Profiles since removed are left to the caller to
    /// skip; ids of guests whose devices are still signed in are included whether or not they were recorded.
    /// </summary>
    public IReadOnlyList<string> GuestProfiles()
    {
        lock (_lock)
        {
            var ids = new HashSet<string>(LoadedGuests(), StringComparer.Ordinal);
            ids.UnionWith(Loaded().Select(i => i.ProfileId));
            ids.UnionWith(signIns.List().Where(s => s.Guest && s.ProfileId is not null).Select(s => s.ProfileId!));
            return ids.ToList();
        }
    }

    /// <summary>
    /// Everything left of a guest whose profile is being removed, wherever that's done (Settings › Profiles too):
    /// their devices are signed out, waiting invites cancelled (with their Tailscale keys) and they stop being a guest.
    /// </summary>
    public async Task ForgetProfileAsync(string profileId, CancellationToken ct)
    {
        List<GuestInvite> cancelled;
        lock (_lock)
        {
            cancelled = Loaded().Where(i => i.ProfileId == profileId).ToList();
            if (cancelled.Count > 0)
            {
                Loaded().RemoveAll(i => i.ProfileId == profileId);
                SaveLocked();
            }

            if (LoadedGuests().Remove(profileId))
            {
                SaveGuestsLocked();
            }
        }

        foreach (var signIn in signIns.List().Where(s => s.Guest && s.ProfileId == profileId))
        {
            signIns.Revoke(signIn.Id);
        }

        foreach (var invite in cancelled)
        {
            await DeleteKeyQuietlyAsync(invite.KeyId, ct);
        }
    }

    /// <summary>Notes that <paramref name="profileId"/> is a guest's.</summary>
    private void MarkGuest(string profileId)
    {
        var guests = LoadedGuests();
        if (guests.Contains(profileId))
        {
            return;
        }

        guests.Add(profileId);
        SaveGuestsLocked();
    }

    private void SaveGuestsLocked()
    {
        try
        {
            ServerConfigStore.WritePrivately(Path.Combine(options.CurrentValue.DataDirectory, GuestsFileName), JsonSerializer.Serialize(LoadedGuests(), Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnwritable(GuestsFileName, ex.Message);
        }
    }

    private List<string>? _guests;

    private List<string> LoadedGuests()
    {
        if (_guests is null)
        {
            var path = Path.Combine(options.CurrentValue.DataDirectory, GuestsFileName);
            try
            {
                _guests = File.Exists(path) ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path), Json) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogUnreadable(path, ex.Message);
                _guests = [];
            }
        }

        return _guests;
    }

    public IReadOnlyList<GuestInvite> Waiting()
    {
        lock (_lock)
        {
            return Loaded().OrderByDescending(i => i.CreatedAt).ToList();
        }
    }

    /// <summary>
    /// Makes an invite for <paramref name="profileId"/>, once the safety check passes: mints the Tailscale key, keeps
    /// the invite and returns the payload for the guest's link (or TV).
    /// </summary>
    public async Task<(GuestInvite Invite, InvitePayload Payload)> CreateAsync(string? name, string profileId, string profileName, string ownerName, InviteVia via, CancellationToken ct)
    {
        var settings = options.CurrentValue.RemoteGuests;
        if (!settings.Enabled)
        {
            throw new InviteException("Family invites aren't turned on.", StatusCodes.Status404NotFound);
        }

        lock (_lock)
        {
            if (Loaded().Count >= MaxWaiting)
            {
                throw new InviteException($"{MaxWaiting} invites are already waiting. Cancel some first.");
            }
        }

        var check = await policy.CheckAsync(ct);
        if (!check.Safe)
        {
            throw new InviteException((check.Problems.Count > 0 ? check.Problems[0] : null) ?? "The tailnet policy doesn't confine guests to Flexarr yet.");
        }

        if (await tailnet.SnapshotAsync(ct) is not { Running: true, Address: { } address })
        {
            throw new InviteException("Tailscale isn't running on the server.");
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now + Lifetime;
        TailscaleKey key;
        try
        {
            key = await api.CreateGuestKeyAsync(expires, ct);
        }
        catch (TailscaleApiException ex)
        {
            throw new InviteException(ex.Message, StatusCodes.Status502BadGateway);
        }
        catch (HttpRequestException ex)
        {
            throw new InviteException($"Couldn't reach Tailscale ({ex.Message}).", StatusCodes.Status502BadGateway);
        }

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        // Usually none: the device is named when it joins, from the profile and what it is ("Gran's iPhone").
        var trimmed = name?.Trim() is { Length: > 0 } t ? t[..Math.Min(t.Length, 60)] : "";
        var invite = new GuestInvite
        {
            Id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8)),
            TokenHash = Hash(token),
            Name = trimmed,
            ProfileId = profileId,
            Via = via,
            CreatedAt = now,
            ExpiresAt = expires, // the key was asked to expire with it
            KeyId = key.Id,
        };

        lock (_lock)
        {
            Loaded().Add(invite);
            MarkGuest(profileId);
            SaveLocked();
        }

        LogCreated(PathComparison.Printable(trimmed.Length > 0 ? trimmed : profileName), via);
        var payload = new InvitePayload(1, ownerName, options.CurrentValue.ServerId ?? "",
            $"{address}:{policy.FlexarrPort.ToString(CultureInfo.InvariantCulture)}", key.Key, token, trimmed.Length > 0 ? trimmed : profileName, profileName, invite.ExpiresAt);
        return (invite, payload);
    }

    /// <summary>Cancels a waiting invite and deletes its Tailscale key; false if there's no such invite.</summary>
    public async Task<bool> CancelAsync(string id, CancellationToken ct)
    {
        GuestInvite? found;
        lock (_lock)
        {
            found = Loaded().FirstOrDefault(i => i.Id == id);
            if (found is null)
            {
                return false;
            }

            Loaded().Remove(found);
            SaveLocked();
        }

        await DeleteKeyQuietlyAsync(found.KeyId, ct);
        return true;
    }

    /// <summary>
    /// Swaps an invite's token for a guest sign-in on the device at <paramref name="address"/> (a guest device on the
    /// tailnet, checked by the caller). Works once.
    /// </summary>
    public (SignIn SignIn, string Token, GuestInvite Invite) Redeem(string token, string address, DeviceType? deviceType,
        Func<string, string?>? profileNameFor = null, Func<string, bool>? profileExists = null)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            throw new InviteException("That invite isn't valid.", StatusCodes.Status400BadRequest);
        }

        var hash = Hash(token);
        GuestInvite? invite;
        lock (_lock)
        {
            invite = Loaded().FirstOrDefault(i => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(i.TokenHash), Encoding.ASCII.GetBytes(hash)));
            if (invite is null)
            {
                throw new InviteException("This invite has been used, cancelled or has expired. Ask for a new one.", StatusCodes.Status410Gone);
            }

            Loaded().Remove(invite);
            SaveLocked();
        }

        // The guest's profile was removed while the invite waited (ForgetProfileAsync cancels them, but not one
        // redeemed in the same moment): nothing to sign in as.
        if (profileExists is not null && !profileExists(invite.ProfileId))
        {
            throw new InviteException("This invite has been used, cancelled or has expired. Ask for a new one.", StatusCodes.Status410Gone);
        }

        var name = invite.Name.Length > 0 ? invite.Name : DeviceName(profileNameFor?.Invoke(invite.ProfileId), deviceType);
        var (signIn, signInToken) = signIns.IssueGuest(name, invite.ProfileId, address, deviceType);
        LogRedeemed(PathComparison.Printable(name), address);
        return (signIn, signInToken, invite);
    }

    /// <summary>"Gran's iPhone": a guest's device as lists show it, from its profile and what its app says it is.</summary>
    public static string DeviceName(string? profileName, DeviceType? type)
    {
        var what = type switch
        {
            DeviceType.AppleTv => "Apple TV",
            DeviceType.IPhone => "iPhone",
            DeviceType.IPad => "iPad",
            DeviceType.AndroidPhone => "Android phone",
            DeviceType.AndroidTablet => "Android tablet",
            DeviceType.AndroidTv => "Android TV",
            _ => "device",
        };
        return profileName is { Length: > 0 } who ? $"{who}’s {what}" : $"A guest’s {what}";
    }

    /// <summary>Encodes a payload for the invite link: https://flexarr.io/join#&lt;this&gt;.</summary>
    public static string Encode(InvitePayload payload) => Base64Url(JsonSerializer.SerializeToUtf8Bytes(payload, PayloadJson));

    public static InvitePayload? Decode(string encoded)
    {
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            return JsonSerializer.Deserialize<InvitePayload>(Convert.FromBase64String(padded), PayloadJson);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return null;
        }
    }

    public const string LinkBase = "https://flexarr.io/join#";

    private async Task DeleteKeyQuietlyAsync(string? keyId, CancellationToken ct)
    {
        if (keyId is null)
        {
            return;
        }

        try
        {
            await api.DeleteKeyAsync(keyId, ct);
        }
        catch (Exception ex) when (ex is TailscaleApiException or HttpRequestException)
        {
            // The key expires by itself; nothing waits on it.
            LogKeyNotDeleted(ex.Message);
        }
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private List<GuestInvite> Loaded()
    {
        if (_invites is null)
        {
            try
            {
                _invites = File.Exists(FilePath) ? JsonSerializer.Deserialize<List<GuestInvite>>(File.ReadAllText(FilePath), Json) ?? [] : [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LogUnreadable(FilePath, ex.Message);
                _invites = [];
            }
        }

        // Expired invites go; their keys have expired with them.
        var now = clock.GetUtcNow().UtcDateTime;
        if (_invites.RemoveAll(i => i.ExpiresAt <= now) > 0)
        {
            SaveLocked();
        }

        return _invites;
    }

    private void SaveLocked()
    {
        try
        {
            Directory.CreateDirectory(options.CurrentValue.DataDirectory);
            ServerConfigStore.WritePrivately(FilePath, JsonSerializer.Serialize(_invites ?? [], Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogUnwritable(FilePath, ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Made a family invite for \"{Name}\" ({Via})")]
    private partial void LogCreated(string name, InviteVia via);

    [LoggerMessage(Level = LogLevel.Information, Message = "\"{Name}\" used their invite from {Address}")]
    private partial void LogRedeemed(string name, string address);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't delete a cancelled invite's Tailscale key ({Reason}); it expires by itself")]
    private partial void LogKeyNotDeleted(string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't read {Path} ({Reason}); waiting invites are forgotten")]
    private partial void LogUnreadable(string path, string reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't save {Path} ({Reason})")]
    private partial void LogUnwritable(string path, string reason);
}
