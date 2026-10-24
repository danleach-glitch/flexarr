using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Core.Media;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <summary>
/// The profile a request acts for, from the <c>X-Flexarr-Profile</c> header (a token handed out when the profile is
/// chosen, PIN checked). Clients that don't send one act for the main profile.
/// </summary>
public sealed class CurrentProfile(IHttpContextAccessor http, IOptionsMonitor<FlexarrOptions> options)
{
    public const string Header = "X-Flexarr-Profile";

    private string? _id;

    public string Id => _id ??= Resolve();

    /// <summary>"{profileId}.{signature}": the signature binds the token to this server's API key.</summary>
    public string TokenFor(string profileId) => $"{profileId}.{Sign(profileId)}";

    private string Resolve()
    {
        var token = http.HttpContext?.Request.Headers[Header].FirstOrDefault();
        var dot = token?.LastIndexOf('.') ?? -1;
        if (token is null || dot <= 0)
        {
            return PlaybackState.DefaultUser;
        }

        var id = token[..dot];
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token[(dot + 1)..]), Encoding.UTF8.GetBytes(Sign(id)))
            ? id
            : PlaybackState.DefaultUser;
    }

    private string Sign(string profileId)
    {
        var key = Encoding.UTF8.GetBytes(options.CurrentValue.ApiKey ?? "");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"profile:{profileId}")))[..32];
    }
}
