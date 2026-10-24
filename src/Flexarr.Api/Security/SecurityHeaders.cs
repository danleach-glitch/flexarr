namespace Flexarr.Api.Security;

/// <summary>Browser protections for a server that may be reachable from the internet.</summary>
public static class SecurityHeaders
{
    // Inline styles are used for per-title colours; trailers embed youtube-nocookie; hls.js runs a blob worker.
    private const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data: blob:; media-src 'self' blob:; style-src 'self' 'unsafe-inline'; " +
        "script-src 'self'; worker-src 'self' blob:; connect-src 'self'; font-src 'self' data:; " +
        "frame-src https://www.youtube-nocookie.com; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";

    public static IApplicationBuilder UseFlexarrSecurityHeaders(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var headers = http.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "same-origin";
        headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        if (http.Request.IsHttps)
        {
            headers.StrictTransportSecurity = "max-age=31536000";
        }

        await next(http);
    });
}
