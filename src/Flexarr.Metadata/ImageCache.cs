using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Flexarr.Metadata;

/// <summary>
/// Downloads remote artwork once and serves resized JPEG copies from &lt;cache&gt;/images.
/// Resizing uses ffmpeg, which the server needs anyway, so there's no image library dependency.
/// </summary>
public sealed partial class ImageCache(HttpClient http, IMetadataSettings settings, ILogger<ImageCache> logger)
{
    // Static: as a typed HttpClient this class is created per request, and the locks must be shared by all of them.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    private string Directory => Path.Combine(settings.CacheDirectory, "images");

    /// <summary>Returns the path of a cached copy at least <paramref name="width"/> wide, or null if it can't be fetched.</summary>
    public async Task<string?> GetAsync(string remoteUrl, int? width, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remoteUrl);

        var snapped = ArtworkUrl.Snap(width);
        var key = Hash(remoteUrl);
        var target = Path.Combine(Directory, key[..2], $"{key}_w{snapped}.jpg");
        if (Cached(target) is { } hit)
        {
            return hit;
        }

        var gate = Locks.GetOrAdd(target, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cached(target) is { } raced)
            {
                return raced;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var source = $"{target}.{Guid.NewGuid():N}.download";
            try
            {
                using (var response = await http.GetAsync(new Uri(ArtworkUrl.ForWidth(remoteUrl, snapped)), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
                {
                    if (!response.IsSuccessStatusCode)
                    {
                        LogDownloadFailed(remoteUrl, (int)response.StatusCode);
                        return null;
                    }

                    await using var file = File.Create(source);
                    await response.Content.CopyToAsync(file, ct).ConfigureAwait(false);
                }

                // Scale down only (never up), keep aspect ratio; PNG logos keep transparency by staying PNG.
                var isPng = await IsPngAsync(source, ct).ConfigureAwait(false);
                if (isPng)
                {
                    target = Path.ChangeExtension(target, ".png");
                }

                string[] args = ["-v", "error", "-y", "-i", source, "-vf", $"scale='min({snapped},iw)':-2", "-frames:v", "1", "-update", "1"];
                var ok = await RunFfmpegAsync(ct, isPng ? [.. args, target] : [.. args, "-q:v", "3", target]).ConfigureAwait(false);
                if (!ok)
                {
                    File.Move(source, target, overwrite: true); // Serve the original rather than nothing.
                }

                return target;
            }
            catch (HttpRequestException ex)
            {
                LogDownloadFailed(remoteUrl, (int?)ex.StatusCode ?? 0);
                return null;
            }
            finally
            {
                File.Delete(source);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Ambient colour for an image (see <see cref="AccentColor"/>), sampled from a tiny resize.</summary>
    public async Task<string?> AccentColorAsync(string remoteUrl, CancellationToken ct)
    {
        var path = await GetAsync(remoteUrl, 92, ct).ConfigureAwait(false);
        if (path is null)
        {
            return null;
        }

        var start = new ProcessStartInfo(settings.FfmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-i", path, "-vf", "scale=32:32", "-f", "rawvideo", "-pix_fmt", "rgb24", "-" })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        using var pixels = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(pixels, ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? AccentColor.FromPixels(pixels.ToArray()) : null;
    }

    private async Task<bool> RunFfmpegAsync(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(settings.FfmpegPath) { RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            LogResizeFailed(stderr.Trim());
        }

        return process.ExitCode == 0;
    }

    /// <summary>Logos with transparency are cached as .png, everything else as .jpg.</summary>
    private static string? Cached(string jpgPath) =>
        File.Exists(jpgPath) ? jpgPath
        : File.Exists(Path.ChangeExtension(jpgPath, ".png")) ? Path.ChangeExtension(jpgPath, ".png")
        : null;

    private static async Task<bool> IsPngAsync(string path, CancellationToken ct)
    {
        var header = new byte[4];
        await using var file = File.OpenRead(path);
        return await file.ReadAsync(header, ct).ConfigureAwait(false) == 4 && header is [0x89, 0x50, 0x4E, 0x47];
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download artwork {Url} (HTTP {Status})")]
    private partial void LogDownloadFailed(string url, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resize artwork: {Error}")]
    private partial void LogResizeFailed(string error);
}
