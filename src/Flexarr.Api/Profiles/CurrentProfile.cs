using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Media;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <summary>
/// The profile a request acts for, worked out when it signed in (see
/// <see cref="Authentication.ApiKeyAuthenticationHandler"/>): the one chosen on that browser or device, else one
/// chosen in the profile picker (an <c>X-Flexarr-Profile</c> token, PIN checked when it was issued), else the profile
/// linked to the caller's Tailscale account, else the main profile.
/// </summary>
public sealed class CurrentProfile(IHttpContextAccessor http, IOptionsMonitor<FlexarrOptions> options)
{
    public const string Header = "X-Flexarr-Profile";

    public string Id => http.HttpContext?.User.FindFirst(FlexarrClaims.Profile)?.Value ?? PlaybackState.DefaultUser;

    /// <summary>
    /// May change settings and manage the server: the API key itself, or the main profile on a browser or device where
    /// its PIN was given lately (see <see cref="AdminUnlocks"/>). Other profiles, and the main profile until then, can
    /// browse and watch.
    /// </summary>
    public bool IsAdmin => http.HttpContext?.User.HasClaim(FlexarrClaims.Admin, "true") == true;

    /// <summary>The main profile is chosen but the server isn't unlocked: its PIN would make this request an admin.</summary>
    public bool CanUnlock => http.HttpContext?.User.HasClaim(FlexarrClaims.CanUnlock, "true") == true;

    /// <summary>The main profile, unlocked or not (or the API key): it's shown what the main profile may do, and the PIN is asked for when it does it.</summary>
    public bool IsMainProfile => IsAdmin || CanUnlock;

    /// <summary>The credential is trusted with the server, so it may switch to a main profile without a PIN.</summary>
    public bool IsTrusted => http.HttpContext?.User.HasClaim(FlexarrClaims.Trusted, "true") == true;

    /// <summary>
    /// Whether choosing <paramref name="profile"/> here takes its PIN. The main profile's guards managing the server,
    /// not watching as it, so on a browser or device the owner signed in it's only asked for if the owner wants it to be.
    /// </summary>
    public bool AsksPinFor(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return profile.PinHash is not null && (!profile.IsMain || !IsTrusted || options.CurrentValue.AskPinForMainProfile != false);
    }

    /// <summary>The browser or device sign-in this request used, if any.</summary>
    public string? SignInId => http.HttpContext?.User.FindFirst(FlexarrClaims.SignIn)?.Value;

    /// <summary>
    /// "{profileId}.{signature}": the signature binds the token to this server's API key and the profile's current
    /// PIN, so changing the PIN (or the key) retires tokens handed out before.
    /// </summary>
    public string TokenFor(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"{profile.Id}.{Sign(options.CurrentValue.ApiKey, $"profile:{profile.Id}:{profile.TokenPinHash}")}";
    }

    /// <summary>
    /// Whether a token's signature is right for <paramref name="profileId"/> and its current <paramref name="pinHash"/>.
    /// Tokens from before PINs were part of the signature also pass while <paramref name="acceptLegacy"/> (the month
    /// after upgrading; see <see cref="FlexarrOptions.LegacySignInsUntil"/>).
    /// </summary>
    public static bool Verifies(string? apiKey, string profileId, string? pinHash, string signature, bool acceptLegacy)
    {
        ArgumentNullException.ThrowIfNull(signature);
        var given = Encoding.UTF8.GetBytes(signature);
        var current = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(Sign(apiKey, $"profile:{profileId}:{pinHash}")));
        var legacy = CryptographicOperations.FixedTimeEquals(given, Encoding.UTF8.GetBytes(Sign(apiKey, $"profile:{profileId}")));
        return current || acceptLegacy && legacy;
    }

    /// <summary>
    /// A profile picture's URL. &lt;img&gt; tags and the Apple TV fetch it without signing in, so it carries a signature:
    /// only someone who was shown the profile list can work it out.
    /// </summary>
    public string? ImageUrlFor(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile.ImageUpdatedAt is not { } at)
        {
            return null;
        }

        var version = at.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"/api/v1/profiles/{profile.Id}/image?v={version}&s={ImageSignature(options.CurrentValue.ApiKey, profile.Id, version)}";
    }

    public static string ImageSignature(string? apiKey, string profileId, string version) => Sign(apiKey, $"image:{profileId}:{version}");

    private static string Sign(string? apiKey, string text) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(apiKey ?? ""), Encoding.UTF8.GetBytes(text)))[..32];
}
