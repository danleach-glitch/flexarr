using System.Collections.Concurrent;
using System.Diagnostics;

namespace Flexarr.Streaming;

/// <summary>
/// Which filters and decoders an FFmpeg has, asked once per program and remembered. Builds differ: the Mac app's LGPL
/// one and Homebrew's have no zscale and no teletext, the Docker image's have both.
/// </summary>
public static class FfmpegFilters
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlySet<string>?>> Known = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="ffmpegPath"/> has <paramref name="filter"/>; true when it can't be asked (the run will say).</summary>
    public static bool Has(string ffmpegPath, string filter) =>
        Known.GetOrAdd(ffmpegPath + "|-filters", _ => new Lazy<IReadOnlySet<string>?>(() => Ask(ffmpegPath, "-filters"))).Value?.Contains(filter) ?? true;

    /// <summary>Whether <paramref name="ffmpegPath"/> has the decoder <paramref name="decoder"/>; false when it can't be asked.</summary>
    public static bool HasDecoder(string ffmpegPath, string decoder) =>
        Known.GetOrAdd(ffmpegPath + "|-decoders", _ => new Lazy<IReadOnlySet<string>?>(() => Ask(ffmpegPath, "-decoders"))).Value?.Contains(decoder) ?? false;

    private static HashSet<string>? Ask(string ffmpegPath, string list)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add(list);
            using var process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                ProcessSignals.KillTree(process);
                return null;
            }

            // Filters: " .S zscale            V->V       Apply resizing, colorspace and bit depth conversion."
            // Decoders: " S..... cc_dec               Closed Captions (EIA-608 / CEA-708)" (after a legend ending "------").
            return output.Result.Split('\n')
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3 && (list == "-decoders" ? parts[0].Length == 6 && parts[0] != "------" : parts[2].Contains("->", StringComparison.Ordinal)))
                .Select(parts => parts[1])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
