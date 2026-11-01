using System.Text.RegularExpressions;

namespace Flexarr.Metadata;

public static partial class ArtworkUrl
{
    /// <summary>Artwork hosts the anonymous remote-image endpoint fetches from, and the paths their images live under.</summary>
    private static readonly Dictionary<string, string> TrustedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image.tmdb.org"] = "/t/p/",
        ["artworks.thetvdb.com"] = "/banners/",
        ["thetvdb.com"] = "/banners/",
        ["static.tvmaze.com"] = "/uploads/images/",
        ["assets.fanart.tv"] = "/fanart/",
        // Apple's podcast directory (search and charts).
        ["is1-ssl.mzstatic.com"] = "/image/thumb/",
        ["is2-ssl.mzstatic.com"] = "/image/thumb/",
        ["is3-ssl.mzstatic.com"] = "/image/thumb/",
        ["is4-ssl.mzstatic.com"] = "/image/thumb/",
        ["is5-ssl.mzstatic.com"] = "/image/thumb/",
        // Book covers: Audible's (through Audnexus and its catalogue) and Open Library's (Fix match, book requests).
        ["m.media-amazon.com"] = "/images/I/",
        ["images-na.ssl-images-amazon.com"] = "/images/I/",
        ["covers.openlibrary.org"] = "/b/",
    };

    /// <summary>
    /// A known artwork host's image, as a plain https URL with any query string or fragment dropped, and TMDB's size
    /// folder made /original/ (the cache picks the size from the width asked for), so one image is one cache entry
    /// however the URL is decorated; null for anything else.
    /// </summary>
    public static string? TrustedRemote(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !TrustedHosts.TryGetValue(uri.Host, out var prefix))
        {
            return null;
        }

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath;
        if (!path.StartsWith(prefix, StringComparison.Ordinal) || !ImagePath().IsMatch(path) || path.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        if (host == "image.tmdb.org")
        {
            var sized = TmdbSized().Match(path);
            return sized.Success ? $"https://{host}/t/p/original/{sized.Groups[1].Value}" : null;
        }

        return $"https://{host}{path}";
    }

    /// <summary>Artwork from a metadata source (Sonarr, Radarr, TVmaze, Seerr): only ever fetched over https.</summary>
    public static bool IsHttps(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>
    /// Artwork Flexarr found on disk: a folder's cover.jpg or poster.jpg, art embedded in a track, or a frame of a home
    /// video (see <see cref="Frame"/>).
    /// </summary>
    public static bool IsLocal(string? url) =>
        url is not null && (url.StartsWith("file://", StringComparison.Ordinal) || url.StartsWith("embedded://", StringComparison.Ordinal)
            || url.StartsWith(FrameScheme, StringComparison.Ordinal));

    /// <summary>The file a local artwork URL reads (the image, the track, or the video); null for any other URL.</summary>
    public static string? LocalPath(string? url) =>
        TryParseFrame(url, out var video, out _) ? video
        : url?.StartsWith("file://", StringComparison.Ordinal) == true ? url["file://".Length..]
        : url?.StartsWith("embedded://", StringComparison.Ordinal) == true ? url["embedded://".Length..]
        : null;

    private const string FrameScheme = "frame://";

    /// <summary>A still from a video, <paramref name="seconds"/> in: <c>frame://{path}#{seconds}</c>.</summary>
    public static string Frame(string path, double seconds)
    {
        ArgumentNullException.ThrowIfNull(path);
        return $"{FrameScheme}{path}#{Math.Max(0, seconds).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}";
    }

    public static bool TryParseFrame(string? url, out string path, out double seconds)
    {
        path = "";
        seconds = 0;
        var hash = url?.LastIndexOf('#') ?? -1;
        if (url is null || !url.StartsWith(FrameScheme, StringComparison.Ordinal) || hash < FrameScheme.Length
            || !double.TryParse(url.AsSpan(hash + 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            return false;
        }

        path = url[FrameScheme.Length..hash];
        return path.Length > 0;
    }

    private const string ArrScheme = "arr://";

    /// <summary>
    /// Artwork held by a configured *arr (Lidarr's artist and album images, which have no public URL):
    /// <c>arr://{instance name}/{path under /api/v1/}</c>. The image cache fetches it with the instance's API key, which
    /// never leaves the server: clients only ever see /api/v1/images/items/… links.
    /// </summary>
    public static string Arr(string instanceName, string apiPath)
    {
        ArgumentNullException.ThrowIfNull(instanceName);
        ArgumentNullException.ThrowIfNull(apiPath);
        return $"{ArrScheme}{Uri.EscapeDataString(instanceName)}/{apiPath.TrimStart('/')}";
    }

    public static bool IsArr(string? url) => url is not null && url.StartsWith(ArrScheme, StringComparison.Ordinal);

    /// <summary>Splits an <see cref="Arr"/> URL into the instance's name and the API path (query string included).</summary>
    public static bool TryParseArr(string? url, out string instanceName, out string apiPath)
    {
        instanceName = apiPath = string.Empty;
        if (!IsArr(url))
        {
            return false;
        }

        var rest = url![ArrScheme.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        instanceName = Uri.UnescapeDataString(rest[..slash]);
        apiPath = rest[(slash + 1)..];
        // Only a path under the API: nothing that could climb out of it or carry a second scheme.
        return !apiPath.Contains("..", StringComparison.Ordinal) && !apiPath.Contains("://", StringComparison.Ordinal);
    }

    // "+" too: Amazon's cover names have them (51ef+s6y42L._SL500_.jpg).
    [GeneratedRegex(@"^/[A-Za-z0-9_\-./+]+\.(?:jpe?g|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePath();

    /// <summary>/t/p/{size}/{file}: TMDB keeps every image under a size folder (w92 … original).</summary>
    [GeneratedRegex(@"^/t/p/[A-Za-z0-9_]+/([^/]+)$")]
    private static partial Regex TmdbSized();

    /// <summary>Widths the image cache produces; requests snap up to the nearest one so the cache stays small.</summary>
    public static readonly int[] Widths = [92, 185, 342, 500, 780, 1280, 1920];

    public static int Snap(int? width) => Widths.FirstOrDefault(w => w >= (width ?? 500), Widths[^1]);

    /// <summary>TMDB serves pre-sized images (/t/p/w780/…); ask for the smallest one that is big enough.</summary>
    public static string ForWidth(string url, int width)
    {
        ArgumentNullException.ThrowIfNull(url);

        const string marker = "image.tmdb.org/t/p/original/";
        var index = url.IndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return url;
        }

        var size = width switch
        {
            <= 92 => "w92",
            <= 185 => "w185",
            <= 342 => "w342",
            <= 500 => "w500",
            <= 780 => "w780",
            <= 1280 => "w1280",
            _ => "original",
        };
        return string.Concat(url.AsSpan(0, index), $"image.tmdb.org/t/p/{size}/", url.AsSpan(index + marker.Length));
    }
}
