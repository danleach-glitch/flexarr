using System.Globalization;

namespace Flexarr.Metadata;

/// <summary>Picks the colour that best represents an image for ambient UI tinting.</summary>
public static class AccentColor
{
    /// <summary>
    /// <paramref name="rgb"/> is packed 8-bit RGB pixels. Pixels are bucketed by hue; the bucket with the most
    /// saturated, mid-bright weight wins and its average is returned, lifted to a usable brightness.
    /// Returns null for (near-)greyscale images.
    /// </summary>
    public static string? FromPixels(ReadOnlySpan<byte> rgb)
    {
        const int buckets = 12;
        Span<double> weight = stackalloc double[buckets];
        Span<double> r = stackalloc double[buckets];
        Span<double> g = stackalloc double[buckets];
        Span<double> b = stackalloc double[buckets];

        for (var i = 0; i + 2 < rgb.Length; i += 3)
        {
            var (h, s, v) = ToHsv(rgb[i], rgb[i + 1], rgb[i + 2]);
            if (s < 0.25 || v < 0.2 || v > 0.97)
            {
                continue;
            }

            // Favour saturated colours that are neither murky nor blown out.
            var w = s * s * (1 - Math.Abs(v - 0.65));
            var bucket = (int)(h / 360 * buckets) % buckets;
            weight[bucket] += w;
            r[bucket] += rgb[i] * w;
            g[bucket] += rgb[i + 1] * w;
            b[bucket] += rgb[i + 2] * w;
        }

        var best = 0;
        for (var i = 1; i < buckets; i++)
        {
            if (weight[i] > weight[best])
            {
                best = i;
            }
        }

        if (weight[best] < rgb.Length / 3 * 0.02)
        {
            return null;
        }

        var (hue, sat, val) = ToHsv(r[best] / weight[best], g[best] / weight[best], b[best] / weight[best]);
        // Readable against a dark room: keep the hue, clamp saturation and brightness into a pleasant band.
        var (fr, fg, fb) = FromHsv(hue, Math.Clamp(sat, 0.35, 0.75), Math.Clamp(val, 0.6, 0.85));
        return string.Create(CultureInfo.InvariantCulture, $"#{fr:x2}{fg:x2}{fb:x2}");
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        r /= 255;
        g /= 255;
        b /= 255;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            h = max == r ? 60 * (((g - b) / d) % 6)
                : max == g ? 60 * (((b - r) / d) + 2)
                : 60 * (((r - g) / d) + 4);
        }

        return (h < 0 ? h + 360 : h, max == 0 ? 0 : d / max, max);
    }

    private static (int R, int G, int B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return ((int)Math.Round((r + m) * 255), (int)Math.Round((g + m) * 255), (int)Math.Round((b + m) * 255));
    }
}
