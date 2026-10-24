namespace Flexarr.Api.Security;

/// <summary>Claims Flexarr's authentication handler puts on a request's user.</summary>
public static class FlexarrClaims
{
    /// <summary>How the request proved itself: api-key, tailscale, local.</summary>
    public const string Method = "flexarr:method";

    /// <summary>The profile the credential belongs to (Tailscale identities), if any.</summary>
    public const string Profile = "flexarr:profile";

    /// <summary>The Tailscale login, for display.</summary>
    public const string TailscaleLogin = "flexarr:tailscale-login";

    public const string AdminPolicy = "FlexarrAdmin";
}

public static class RequestPeer
{
    private const string Key = "flexarr:peer";

    /// <summary>Remembers the TCP peer before forwarded headers replace it with the client's address.</summary>
    public static void Capture(HttpContext http) => http.Items[Key] = http.Connection.RemoteIpAddress;

    /// <summary>The address that actually connected (a local proxy for tunnelled requests).</summary>
    public static System.Net.IPAddress? Of(HttpContext http) => http.Items[Key] as System.Net.IPAddress ?? http.Connection.RemoteIpAddress;
}
