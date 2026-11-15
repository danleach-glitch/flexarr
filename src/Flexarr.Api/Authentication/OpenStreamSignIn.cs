using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

/// <summary>
/// The sign-in a long-lived stream (<c>/events</c>, <c>/logs/stream</c>) was opened with, checked again as it runs:
/// signing out, choosing another profile there, a new API key or, for the server's logs, Lock (or the unlock lapsing)
/// ends the stream at its next heartbeat rather than leaving it open for as long as the connection lasts.
/// </summary>
public sealed class OpenStreamSignIn
{
    private readonly HttpContext _http;
    private readonly bool _admin;
    private readonly string? _method;
    private readonly string? _signInId;
    private readonly string? _profileAtOpen;

    private OpenStreamSignIn(HttpContext http, bool admin)
    {
        _http = http;
        _admin = admin;
        _method = http.User.FindFirst(FlexarrClaims.Method)?.Value;
        _signInId = http.User.FindFirst(FlexarrClaims.SignIn)?.Value;
        _profileAtOpen = Services.GetRequiredService<SignIns>().ById(_signInId)?.ProfileId;
    }

    /// <param name="admin">The stream is for managing the server: it also needs the admin unlock to last.</param>
    public static OpenStreamSignIn Of(HttpContext http, bool admin = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        return new OpenStreamSignIn(http, admin);
    }

    private IServiceProvider Services => _http.RequestServices;

    /// <summary>Whether the stream may go on.</summary>
    public bool Holds()
    {
        var options = Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;
        if (_method == ApiKeyAuthenticationHandler.Methods.ApiKey)
        {
            // The key it gave is still the server's key.
            var given = _http.Request.Headers[ApiKeyAuthenticationHandler.HeaderName].FirstOrDefault()
                ?? _http.Request.Query[ApiKeyAuthenticationHandler.QueryName].FirstOrDefault();
            return !string.IsNullOrEmpty(given) && !string.IsNullOrEmpty(options.ApiKey)
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(options.ApiKey));
        }

        if (_signInId is not null)
        {
            // Still signed in, as the same profile; and for the logs, still unlocked.
            return Services.GetRequiredService<SignIns>().ById(_signInId) is { } signIn && signIn.ProfileId == _profileAtOpen
                && (!_admin || Services.GetRequiredService<AdminUnlocks>().IsUnlocked(_signInId));
        }

        // Let in by the home network: only while it still lets people in.
        if (_method == ApiKeyAuthenticationHandler.Methods.Local)
        {
            return !_admin && options.AuthenticationRequired == AuthenticationRequired.DisabledForLocalAddresses;
        }

        return !_admin;
    }
}
