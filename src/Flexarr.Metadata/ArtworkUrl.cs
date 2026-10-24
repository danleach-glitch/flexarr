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
    };

    /// <summary>
    /// A known artwork host's image, as a plain https URL with any query string or fragment dropped (so one image
    /// is one cache entry, however the URL is decorated); null for anything else.
    /// </summary>
    public static string? TrustedRemote(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort
            || !string.IsNullOrEmpty(uri.UserInfo) || !TrustedHosts.TryGetValue(uri.Host, out var prefix))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        return path.StartsWith(prefix, StringComparison.Ordinal) && ImagePath().IsMatch(path) && !path.Contains("..", StringComparison.Ordinal)
            ? $"https://{uri.Host.ToLowerInvariant()}{path}"
            : null;
    }

    /// <summary>Artwork from a metadata source (Sonarr, Radarr, TVmaze, Seerr): only ever fetched over https.</summary>
    public static bool IsHttps(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    /// <summary>Artwork the library scanner found on disk: a folder's cover.jpg, or art embedded in a track.</summary>
    public static bool IsLocal(string? url) =>
        url is not null && (url.StartsWith("file://", StringComparison.Ordinal) || url.StartsWith("embedded://", StringComparison.Ordinal));

    [GeneratedRegex(@"^/[A-Za-z0-9_\-./]+\.(?:jpe?g|png|webp)$", RegexOptions.IgnoreCase)]
    private static partial Regex ImagePath();

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
