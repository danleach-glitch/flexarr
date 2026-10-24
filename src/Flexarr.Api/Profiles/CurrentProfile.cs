using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Media;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Profiles;

/// <summary>
/// The profile a request acts for: one chosen in the profile picker (an <c>X-Flexarr-Profile</c> token, PIN
/// checked when it was issued), else the profile linked to the caller's Tailscale account, else the main profile.
/// </summary>
public sealed class CurrentProfile(IHttpContextAccessor http, IOptionsMonitor<FlexarrOptions> options)
{
    public const string Header = "X-Flexarr-Profile";

    private string? _id;

    public string Id => _id ??= Resolve();

    /// <summary>
    /// May change settings and manage the server: the API key, the home-network rule, or the main profile.
    /// Other profiles can browse and watch.
    /// </summary>
    public bool IsAdmin
    {
        get
        {
            var method = http.HttpContext?.User.FindFirst(FlexarrClaims.Method)?.Value;
            return method is "api-key" or "local" || Id == PlaybackState.DefaultUser;
        }
    }

    /// <summary>"{profileId}.{signature}": the signature binds the token to this server's API key.</summary>
    public string TokenFor(string profileId) => $"{profileId}.{Sign(profileId)}";

    private string Resolve()
    {
        var context = http.HttpContext;
        var token = context?.Request.Headers[Header].FirstOrDefault();
        var dot = token?.LastIndexOf('.') ?? -1;
        if (token is not null && dot > 0)
        {
            var id = token[..dot];
            if (CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token[(dot + 1)..]), Encoding.UTF8.GetBytes(Sign(id))))
            {
                return id;
            }
        }

        return context?.User.FindFirst(FlexarrClaims.Profile)?.Value ?? PlaybackState.DefaultUser;
    }

    private string Sign(string profileId)
    {
        var key = Encoding.UTF8.GetBytes(options.CurrentValue.ApiKey ?? "");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes($"profile:{profileId}")))[..32];
    }
}
