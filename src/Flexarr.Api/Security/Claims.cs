namespace Flexarr.Api.Security;

/// <summary>Claims Flexarr's authentication handler puts on a request's user.</summary>
public static class FlexarrClaims
{
    /// <summary>How the request proved itself: api-key (the server's key), device, session (a browser), tailscale, local.</summary>
    public const string Method = "flexarr:method";

    /// <summary>The profile the request acts for, resolved and checked when it signed in.</summary>
    public const string Profile = "flexarr:profile";

    /// <summary>Present ("true") when the request may manage the server.</summary>
    public const string Admin = "flexarr:admin";

    /// <summary>
    /// Present ("true") when the main profile is chosen but the server isn't unlocked: the admin PIN would let
    /// the request manage it (see <see cref="AdminUnlocks"/>).
    /// </summary>
    public const string CanUnlock = "flexarr:can-unlock";

    /// <summary>
    /// Present ("true") when the credential itself is trusted with the server (the API key, a browser or device the
    /// main profile signed in, the home network), so it may switch to a main profile that has no PIN. A Tailscale
    /// account linked to another profile isn't.
    /// </summary>
    public const string Trusted = "flexarr:trusted";

    /// <summary>
    /// Present ("true") on a browser at home without a sign-in of its own ("only away from home") in a household with
    /// kids profiles: a child could open one in a private window, so choosing the main profile there takes the admin PIN.
    /// </summary>
    public const string KidsHome = "flexarr:kids-home";

    /// <summary>
    /// Present ("true") when the request would be the main profile, whose own PIN was never given here, from a client
    /// that says it can ask who's watching (see <see cref="Authentication.ApiKeyAuthenticationHandler.ProfileChoiceHeader"/>):
    /// it's answered as no profile chosen until one is (see <see cref="Profiles.ProfileChoiceGate"/>).
    /// </summary>
    public const string ChooseProfile = "flexarr:choose-profile";

    /// <summary>The browser or device sign-in the request used, if any.</summary>
    public const string SignIn = "flexarr:sign-in";

    /// <summary>Present ("true") on a family guest's device: locked to its profile, never trusted (see docs/remote-guests.md).</summary>
    public const string Guest = "flexarr:guest";

    /// <summary>The Tailscale login, for display.</summary>
    public const string TailscaleLogin = "flexarr:tailscale-login";

    public const string AdminPolicy = "FlexarrAdmin";

    /// <summary>The main profile, unlocked or not: it sees what's waiting for an admin, and unlocking takes it the rest of the way.</summary>
    public const string MainProfilePolicy = "FlexarrMainProfile";
}

public static class RequestPeer
{
    private const string Key = "flexarr:peer";
    private const string HostKey = "flexarr:host";

    /// <summary>Remembers the TCP peer and Host header before forwarded headers replace them with the client's.</summary>
    public static void Capture(HttpContext http)
    {
        http.Items[Key] = http.Connection.RemoteIpAddress;
        http.Items[HostKey] = http.Request.Host;
    }

    /// <summary>The address that actually connected (a local proxy for tunnelled requests).</summary>
    public static System.Net.IPAddress? Of(HttpContext http) => http.Items[Key] as System.Net.IPAddress ?? http.Connection.RemoteIpAddress;

    /// <summary>The Host header as it arrived, which a proxy's (or a script's) X-Forwarded-Host can't change.</summary>
    public static HostString HostOf(HttpContext http) => http.Items[HostKey] is HostString host ? host : http.Request.Host;

    /// <summary>
    /// Whether the browser reached Flexarr over HTTPS, as far as anyone says: the connection itself, a trusted proxy
    /// (forwarded headers have made the request HTTPS by now), or <c>X-Forwarded-Proto: https</c> from a proxy that
    /// isn't listed in <c>TrustedProxies</c>, whose headers are otherwise left alone. Only for marking cookies Secure
    /// and sending HSTS, where a false claim costs nobody but the one making it: their own cookie stops being sent over
    /// plain HTTP, and browsers ignore HSTS that arrives over it.
    /// </summary>
    public static bool SaysHttps(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        // The first value is the scheme the browser used; later ones are hops between proxies.
        return http.Request.IsHttps
            || string.Equals(http.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim(), "https", StringComparison.OrdinalIgnoreCase);
    }
}
