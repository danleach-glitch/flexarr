namespace Flexarr.Metadata;

public static class ArtworkUrl
{
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
