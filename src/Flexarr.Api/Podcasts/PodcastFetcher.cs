using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Flexarr.Core.Podcasts;

namespace Flexarr.Api.Podcasts;

/// <summary>What fetching a feed came to.</summary>
/// <param name="Feed">The feed, unless it hadn't changed (<paramref name="NotModified"/>) or couldn't be read.</param>
/// <param name="MovedTo">Where the feed now lives, when every redirect on the way was permanent.</param>
/// <param name="Error">Why it couldn't be read, in words for the viewer.</param>
public sealed record FeedFetch(ParsedFeed? Feed, bool NotModified, string? ETag, string? LastModified, string? MovedTo, string? Error);

/// <summary>
/// Fetches podcast feeds and episodes from the internet, and only the internet: anyone signed in can add a feed, and a
/// feed names the addresses its episodes come from, so neither may point the server at something on the home network
/// or the machine itself (a router's admin page, another app's API). Every connection, redirects included, is checked
/// against the address it actually goes to (see <see cref="IsPublic"/>).
/// </summary>
public sealed class PodcastFetcher(HttpClient http)
{
    public const int MaxRedirects = 8;

    /// <summary>Feeds of a few thousand episodes run to tens of megabytes; anything past this isn't a feed.</summary>
    public const long MaxFeedBytes = 64L * 1024 * 1024;

    public static readonly string UserAgent =
        $"Flexarr/{typeof(PodcastFetcher).Assembly.GetName().Version?.ToString(3) ?? "1.0.0"} (+https://github.com/danleach-glitch/flexarr)";

    /// <summary>The handler every podcast request goes through: redirects are followed by hand, connections only to the internet.</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectCallback = ConnectPubliclyAsync,
    };

    private static async ValueTask<Stream> ConnectPubliclyAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host, out var literal) ? [literal] : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        var allowed = addresses.Where(IsPublic).ToArray();
        if (allowed.Length == 0)
        {
            throw new HttpRequestException($"{host} isn't on the internet, so Flexarr won't fetch from it.");
        }

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static readonly IPNetwork[] NotInternet =
    [
        IPNetwork.Parse("0.0.0.0/8"), IPNetwork.Parse("10.0.0.0/8"), IPNetwork.Parse("100.64.0.0/10"), IPNetwork.Parse("127.0.0.0/8"),
        IPNetwork.Parse("169.254.0.0/16"), IPNetwork.Parse("172.16.0.0/12"), IPNetwork.Parse("192.0.0.0/24"), IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("198.18.0.0/15"), IPNetwork.Parse("224.0.0.0/3"),
        IPNetwork.Parse("::/127"), IPNetwork.Parse("fc00::/7"), IPNetwork.Parse("fe80::/10"), IPNetwork.Parse("ff00::/8"),
        IPNetwork.Parse("64:ff9b:1::/48"), IPNetwork.Parse("2001:db8::/32"),
        // Teredo hides the address it tunnels to; nothing a podcast needs.
        IPNetwork.Parse("2001::/32"),
    ];

    /// <summary>IPv6 ranges with an IPv4 address inside, which is checked in its place: NAT64, 6to4, IPv4-compatible.</summary>
    private static readonly (IPNetwork Range, int Offset)[] CarryIPv4 =
    [
        (IPNetwork.Parse("64:ff9b::/96"), 12), (IPNetwork.Parse("2002::/16"), 2), (IPNetwork.Parse("::/96"), 12),
    ];

    /// <summary>
    /// An address on the internet: not this machine, the home network, a tailnet (100.64.0.0/10), link-local (where
    /// cloud metadata services live), multicast or a reserved range.
    /// </summary>
    public static bool IsPublic(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        // On an IPv6-only network with NAT64, 64:ff9b::c0a8:101 is 192.168.1.1.
        foreach (var (range, offset) in CarryIPv4)
        {
            if (range.Contains(address))
            {
                return IsPublic(new IPAddress(address.GetAddressBytes().AsSpan(offset, 4)));
            }
        }

        return !NotInternet.Any(n => n.Contains(address));
    }

    /// <summary>
    /// Sends a GET, following redirects (each hop checked like the first). <paramref name="permanent"/> says whether
    /// every one was a permanent move (301 or 308), when the address should be updated.
    /// </summary>
    public async Task<(HttpResponseMessage Response, Uri FinalUrl, bool Permanent)> GetAsync(Uri url, Action<HttpRequestHeaders>? headers, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(url);
        var permanent = true;
        for (var hop = 0; ; hop++)
        {
            if (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            {
                throw new HttpRequestException("The feed sent Flexarr to an address that isn't on the web.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            headers?.Invoke(request.Headers);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location is not { } location)
            {
                return (response, url, permanent && hop > 0);
            }

            response.Dispose();
            if (hop >= MaxRedirects)
            {
                throw new HttpRequestException("The feed redirected too many times.");
            }

            permanent &= status is 301 or 308;
            url = location.IsAbsoluteUri ? location : new Uri(url, location);
        }
    }

    /// <summary>Fetches and reads a feed, sending back what the last fetch answered so an unchanged one costs nothing.</summary>
    public async Task<FeedFetch> FetchFeedAsync(string url, string? etag, string? lastModified, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return Failed("That isn't a web address.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        try
        {
            var (response, final, permanent) = await GetAsync(uri, h =>
            {
                h.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.1");
                if (etag is not null)
                {
                    h.TryAddWithoutValidation("If-None-Match", etag);
                }

                if (lastModified is not null)
                {
                    h.TryAddWithoutValidation("If-Modified-Since", lastModified);
                }
            }, token).ConfigureAwait(false);
            using (response)
            {
                var movedTo = permanent ? final.AbsoluteUri : null;
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return new FeedFetch(null, true, etag, lastModified, movedTo, null);
                }

                if (!response.IsSuccessStatusCode)
                {
                    return Failed(response.StatusCode switch
                    {
                        HttpStatusCode.NotFound or HttpStatusCode.Gone => "The feed isn't there any more.",
                        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "The feed's host refused to send it.",
                        _ => $"The feed's host answered {(int)response.StatusCode}.",
                    });
                }

                if (response.Content.Headers.ContentLength > MaxFeedBytes)
                {
                    return Failed("The feed is too big to be a podcast.");
                }

                await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await CopyLimitedAsync(body, buffer, MaxFeedBytes, token).ConfigureAwait(false);
                buffer.Position = 0;
                var feed = FeedParser.Parse(buffer);
                return new FeedFetch(feed, false, response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified?.ToString("R", System.Globalization.CultureInfo.InvariantCulture), movedTo, null);
            }
        }
        catch (FormatException ex)
        {
            return Failed(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Failed(ex.HttpRequestError == HttpRequestError.NameResolutionError ? "The feed's host couldn't be found." : ex.Message);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Failed("The feed's host didn't answer in time.");
        }

        static FeedFetch Failed(string error) => new(null, false, null, null, null, error);
    }

    /// <summary>A small text file a feed points to (a chapters file), or null when it can't be read in time or is too big.</summary>
    public async Task<string?> FetchTextAsync(string url, long limit, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var (response, _, _) = await GetAsync(uri, null, timeout.Token).ConfigureAwait(false);
            using (response)
            {
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > limit)
                {
                    return null;
                }

                await using var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                await CopyLimitedAsync(body, buffer, limit, timeout.Token).ConfigureAwait(false);
                return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or FormatException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>Copies at most <paramref name="limit"/> bytes.</summary>
    /// <exception cref="FormatException">There was more.</exception>
    public static async Task CopyLimitedAsync(Stream from, Stream to, long limit, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await from.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > limit)
            {
                throw new FormatException("The feed is too big to be a podcast.");
            }

            await to.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
