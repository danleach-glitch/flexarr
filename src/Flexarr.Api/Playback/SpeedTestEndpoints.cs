using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Playback;

/// <summary>
/// The apps' speed test (docs/remote-streaming.md): bytes that can't be compressed, timed by the app from the first to
/// the last, so the first play away from home has a reading to go by.
/// </summary>
public static class SpeedTestEndpoints
{
    /// <summary>What's sent when the request doesn't say: about two seconds' worth on an 8 Mbps link.</summary>
    public const int DefaultBytes = 2_000_000;

    /// <summary>The most one test sends, so the endpoint can't be used to keep the server's upload busy.</summary>
    public const int MaxBytes = 4 << 20;

    private const int ChunkBytes = 64 << 10;

    /// <summary>For any signed-in device (the group's own sign-in), not anonymous: an open endpoint would hand anyone the upload.</summary>
    public static RouteGroupBuilder MapSpeedTestEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/speedtest", async (int? bytes, HttpContext http, CancellationToken ct) =>
        {
            var length = Math.Clamp(bytes ?? DefaultBytes, 1, MaxBytes);
            var response = http.Response;
            // Never kept along the way (a cached copy would time the cache), and sent as bytes nothing compresses:
            // octet-stream isn't among the types the server compresses, and the bytes are random anyway.
            response.Headers[HeaderNames.CacheControl] = "no-store";
            response.ContentType = "application/octet-stream";
            response.ContentLength = length;
            var chunk = new byte[Math.Min(ChunkBytes, length)];
            for (var sent = 0; sent < length;)
            {
                var size = Math.Min(chunk.Length, length - sent);
                Random.Shared.NextBytes(chunk.AsSpan(0, size));
                await response.Body.WriteAsync(chunk.AsMemory(0, size), ct);
                sent += size;
            }

            await response.Body.FlushAsync(ct);
        }).WithTags("Playback");

        return api;
    }
}
