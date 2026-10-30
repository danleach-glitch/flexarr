using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace Flexarr.Streaming;

/// <summary>Describes a file's scrubbing previews: sprite sheets of thumbnails taken every <see cref="Interval"/> seconds.</summary>
public sealed record TrickplayManifest(int Interval, int Width, int Height, int Columns, int Rows, int Count, int Sheets);

/// <summary>
/// Generates scrubbing previews with ffmpeg, decoding keyframes only (cheap even for long films) and
/// tiling thumbnails into sheets so a player loads a handful of images rather than hundreds.
/// </summary>
public static class Trickplay
{
    public const int Interval = 10;
    public const int Width = 320;
    public const int Columns = 10;
    public const int Rows = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string DirectoryFor(string cacheDirectory, Guid fileId) => Path.Combine(cacheDirectory, "trickplay", fileId.ToString("N"));

    public static TrickplayManifest? Read(string cacheDirectory, Guid fileId)
    {
        var path = Path.Combine(DirectoryFor(cacheDirectory, fileId), "manifest.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<TrickplayManifest>(File.ReadAllText(path), Json) : null;
    }

    /// <remarks>ffmpeg runs below normal priority on two threads, so it can't starve a stream being played.</remarks>
    /// <param name="tenBit">10-bit video (HDR, or HEVC Main 10): frames come off the GPU as P010, not NV12.</param>
    public static async Task<TrickplayManifest?> GenerateAsync(string input, Guid fileId, int sourceWidth, int sourceHeight, bool hdr, bool tenBit, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var directory = DirectoryFor(settings.CacheDirectory, fileId);
        var working = directory + ".partial";
        if (Directory.Exists(working))
        {
            Directory.Delete(working, recursive: true);
        }

        Directory.CreateDirectory(working);
        var height = Math.Max(2, (int)Math.Round(Width * (double)sourceHeight / Math.Max(1, sourceWidth) / 2) * 2);
        var size = $"{Width}:{height}";
        var tile = $"tile={Columns}x{Rows}";
        var output = Path.Combine(working, "sheet%03d.jpg");

        // On macOS, scale (and tone-map HDR) on the media engine; otherwise, or if that fails, decode keyframes in software.
        string[] software = ["-hide_banner", "-v", "error", "-nostdin", "-skip_frame", "nokey", "-threads", "2", .. FfmpegCommand.LocalFile, "-i", input,
            "-an", "-sn", "-vf", $"fps=1/{Interval}:round=down,scale={size},{tile}", "-fps_mode", "vfr", "-q:v", "5", output];
        var ok = false;
        if (settings.Resolved == HardwareAcceleration.VideoToolbox)
        {
            var download = tenBit || hdr ? "hwdownload,format=p010le,format=yuv420p" : "hwdownload,format=nv12";
            string[] hardware = ["-hide_banner", "-v", "error", "-nostdin", "-skip_frame", "nokey", "-hwaccel", "videotoolbox", "-hwaccel_output_format", "videotoolbox_vld", "-threads", "2", .. FfmpegCommand.LocalFile, "-i", input,
                "-an", "-sn", "-vf", $"fps=1/{Interval}:round=down,scale_vt=w={Width}:h={height}" + (hdr ? ":color_matrix=bt709:color_primaries=bt709:color_transfer=bt709" : "") + $",{download},{tile}",
                "-fps_mode", "vfr", "-q:v", "5", output];
            ok = await RunAsync(settings.FfmpegPath, hardware, ct).ConfigureAwait(false);
        }

        if (!ok)
        {
            foreach (var partial in Directory.GetFiles(working))
            {
                File.Delete(partial);
            }

            ok = await RunAsync(settings.FfmpegPath, software, ct).ConfigureAwait(false);
        }

        if (!ok)
        {
            Directory.Delete(working, recursive: true);
            return null;
        }

        var sheets = Directory.GetFiles(working, "sheet*.jpg").Length;
        if (sheets == 0)
        {
            Directory.Delete(working, recursive: true);
            return null;
        }

        var manifest = new TrickplayManifest(Interval, Width, height, Columns, Rows, sheets * Columns * Rows, sheets);
        await File.WriteAllTextAsync(Path.Combine(working, "manifest.json"), JsonSerializer.Serialize(manifest, Json), ct).ConfigureAwait(false);
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.Move(working, directory);
        return manifest;
    }

    public static string SheetPath(string cacheDirectory, Guid fileId, int sheet) =>
        Path.Combine(DirectoryFor(cacheDirectory, fileId), string.Create(CultureInfo.InvariantCulture, $"sheet{sheet + 1:D3}.jpg"));

    private static async Task<bool> RunAsync(string ffmpeg, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        try
        {
            // Previews are never urgent: a stream being played comes first.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (InvalidOperationException)
        {
            // Already finished.
        }

        try
        {
            await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessSignals.KillTree(process);
            throw;
        }

        return process.ExitCode == 0;
    }
}
