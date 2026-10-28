using System.Collections.Concurrent;
using System.Diagnostics;

namespace Flexarr.Streaming;

/// <summary>
/// Which filters an FFmpeg has, asked once per program and remembered. Builds differ: the Mac app's LGPL one and
/// Homebrew's have no zscale, the Docker image's has.
/// </summary>
public static class FfmpegFilters
{
    private static readonly ConcurrentDictionary<string, Lazy<IReadOnlySet<string>?>> Known = new(StringComparer.Ordinal);

    /// <summary>Whether <paramref name="ffmpegPath"/> has <paramref name="filter"/>; true when it can't be asked (the run will say).</summary>
    public static bool Has(string ffmpegPath, string filter) =>
        Known.GetOrAdd(ffmpegPath, path => new Lazy<IReadOnlySet<string>?>(() => Ask(path))).Value?.Contains(filter) ?? true;

    private static HashSet<string>? Ask(string ffmpegPath)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            start.ArgumentList.Add("-hide_banner");
            start.ArgumentList.Add("-filters");
            using var process = Process.Start(start)!;
            _ = process.StandardError.ReadToEndAsync();
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(10_000))
            {
                ProcessSignals.KillTree(process);
                return null;
            }

            // Lines like " .S zscale            V->V       Apply resizing, colorspace and bit depth conversion."
            return output.Result.Split('\n')
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Where(parts => parts.Length >= 3 && parts[2].Contains("->", StringComparison.Ordinal))
                .Select(parts => parts[1])
                .ToHashSet(StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
