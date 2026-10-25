using Microsoft.Net.Http.Headers;

namespace Flexarr.Api.Podcasts;

/// <summary>
/// Passes a podcast episode's sound from its host to the player, ranges and all, so seeking works. Going through the
/// server means players never meet the plain-http hosts Apple's apps refuse, nor tell every podcast host where each
/// listener is, and it works the same over Tailscale as at home.
/// </summary>
public static class PodcastRelay
{
    /// <param name="fetcher">Fetches from the internet only.</param>
    /// <param name="url">The episode's address in its feed.</param>
    /// <param name="resolved">Where it led last time, tried first; when that fails (a signed link that's run out), <paramref name="url"/> is.</param>
    /// <param name="mimeType">The feed's type for the file, if the host's is no use.</param>
    /// <param name="http">The player's request, answered.</param>
    /// <param name="ct">The player went away.</param>
    /// <returns>Where the address led, to try first next time.</returns>
    public static async Task<string?> RelayAsync(PodcastFetcher fetcher, string url, string? resolved, string? mimeType, HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(http);
        var range = http.Request.Headers.Range.ToString();
        HttpResponseMessage? upstream = null;
        Uri? final = null;
        foreach (var candidate in resolved is null || resolved == url ? [url] : new[] { resolved, url })
        {
            try
            {
                (upstream, final, _) = await fetcher.GetAsync(new Uri(candidate), h =>
                {
                    if (range.Length > 0)
                    {
                        h.TryAddWithoutValidation("Range", range);
                    }
                }, ct);
            }
            catch (HttpRequestException) when (candidate != url)
            {
                continue;
            }
            catch (HttpRequestException)
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                return null;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                http.Response.StatusCode = StatusCodes.Status504GatewayTimeout;
                return null;
            }

            if (upstream.IsSuccessStatusCode || candidate == url)
            {
                break;
            }

            upstream.Dispose();
            upstream = null;
        }

        if (upstream is null)
        {
            http.Response.StatusCode = StatusCodes.Status502BadGateway;
            return null;
        }

        using (upstream)
        {
            var status = (int)upstream.StatusCode;
            if (status == StatusCodes.Status416RangeNotSatisfiable)
            {
                http.Response.StatusCode = status;
                if (upstream.Content.Headers.ContentRange is { } unsatisfied)
                {
                    http.Response.Headers[HeaderNames.ContentRange] = unsatisfied.ToString();
                }

                return final?.AbsoluteUri;
            }

            if (!upstream.IsSuccessStatusCode)
            {
                http.Response.StatusCode = StatusCodes.Status502BadGateway;
                return null;
            }

            var response = http.Response;
            response.StatusCode = status;
            response.ContentType = ContentType(upstream.Content.Headers.ContentType?.MediaType, mimeType, url);
            if (upstream.Content.Headers.ContentLength is { } length)
            {
                response.ContentLength = length;
            }

            if (upstream.Content.Headers.ContentRange is { } contentRange)
            {
                response.Headers[HeaderNames.ContentRange] = contentRange.ToString();
            }

            response.Headers[HeaderNames.AcceptRanges] = status == StatusCodes.Status206PartialContent || upstream.Headers.AcceptRanges.Contains("bytes") ? "bytes" : "none";
            response.Headers[HeaderNames.CacheControl] = "no-store";

            await using var body = await upstream.Content.ReadAsStreamAsync(ct);
            try
            {
                await body.CopyToAsync(response.Body, ct);
            }
            catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
            {
                http.Abort(); // the host stopped sending: end the response so the player asks again from where it got to
            }

            return final?.AbsoluteUri;
        }
    }

    /// <summary>The type to send: the host's, unless it calls the file "octet-stream", then the feed's, then the extension's.</summary>
    public static string ContentType(string? fromHost, string? fromFeed, string url)
    {
        if (fromHost is { } host && host.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return host;
        }

        if (fromFeed is { } feed && feed.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return feed;
        }

        return Path.GetExtension(new Uri(url).AbsolutePath).ToLowerInvariant() switch
        {
            ".m4a" or ".mp4" or ".aac" => "audio/mp4",
            ".ogg" or ".oga" => "audio/ogg",
            ".opus" => "audio/opus",
            ".wav" => "audio/wav",
            ".flac" => "audio/flac",
            _ => "audio/mpeg",
        };
    }
}
