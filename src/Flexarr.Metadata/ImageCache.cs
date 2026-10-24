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
    /// <summary>Artwork is a few hundred KB; anything much bigger isn't a poster.</summary>
    internal const long MaxDownloadBytes = 25 * 1024 * 1024;

    /// <summary>When the cache passes this, the least recently used images go (they're fetched again if wanted).</summary>
    internal const long MaxCacheBytes = 4L * 1024 * 1024 * 1024;

    private const int MaxRedirects = 3;
    private static readonly TimeSpan TrimInterval = TimeSpan.FromMinutes(10);

    // Static: as a typed HttpClient this class is created per request, and the locks must be shared by all of them.
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.Ordinal);

    /// <summary>A page of new posters shouldn't start dozens of ffmpegs at once.</summary>
    private static readonly SemaphoreSlim FfmpegSlots = new(Environment.ProcessorCount, Environment.ProcessorCount);

    private static long _lastTrim = long.MinValue / 2; // so the first write checks
    private static int _trimming;

    private string Directory => Path.Combine(settings.CacheDirectory, "images");

    /// <summary>Returns the path of a cached copy at least <paramref name="width"/> wide, or null if it can't be fetched.</summary>
    /// <param name="remoteUrl">An https URL, or (from the library scanner only) a file:// or embedded:// one.</param>
    public async Task<string?> GetAsync(string remoteUrl, int? width, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remoteUrl);

        var snapped = ArtworkUrl.Snap(width);
        var key = Hash(remoteUrl);
        var target = Path.Combine(Directory, key[..2], $"{key}_w{snapped}.jpg");
        if (Cached(target) is { } hit)
        {
            Touch(hit);
            return hit;
        }

        var lockKey = target;
        var gate = Locks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cached(target) is { } raced)
            {
                return raced;
            }

            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            // Local artwork from music folders: cover.jpg next to the files, or art embedded in a track.
            if (ArtworkUrl.IsLocal(remoteUrl))
            {
                var embedded = remoteUrl.StartsWith("embedded://", StringComparison.Ordinal);
                var local = embedded ? remoteUrl["embedded://".Length..] : remoteUrl["file://".Length..];
                if (!File.Exists(local))
                {
                    return null;
                }

                // A cover is read as the image it is; a track is a media file, and only its picture is taken.
                var format = embedded ? null : InputFormat(await HeaderAsync(local, ct).ConfigureAwait(false));
                if (!embedded && format is null)
                {
                    return null;
                }

                return await ResizeAsync(local, format, embedded, snapped, target, ct).ConfigureAwait(false) ? Written(target) : null;
            }

            if (!Uri.TryCreate(ArtworkUrl.ForWidth(remoteUrl, snapped), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                return null;
            }

            var source = $"{target}.{Guid.NewGuid():N}.download";
            try
            {
                if (!await DownloadAsync(uri, remoteUrl, source, ct).ConfigureAwait(false))
                {
                    return null;
                }

                var downloaded = InputFormat(await HeaderAsync(source, ct).ConfigureAwait(false));
                if (downloaded is null)
                {
                    LogNotAnImage(remoteUrl);
                    return null;
                }

                // Scale down only (never up), keep aspect ratio; PNG logos keep transparency by staying PNG.
                if (downloaded == "png_pipe")
                {
                    target = Path.ChangeExtension(target, ".png");
                }

                if (!await ResizeAsync(source, downloaded, false, snapped, target, ct).ConfigureAwait(false))
                {
                    File.Move(source, target, overwrite: true); // Serve the original rather than nothing.
                }

                return Written(target);
            }
            catch (HttpRequestException ex)
            {
                LogDownloadFailed(remoteUrl, (int?)ex.StatusCode ?? 0);
                return null;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                LogDownloadFailed(remoteUrl, 0); // timed out
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
            // Done with: drop it, unless someone else replaced it meanwhile. A request already waiting on this one
            // still gets it; one arriving now makes a new one, and at worst repeats the work (writes are atomic).
            Locks.TryRemove(new KeyValuePair<string, SemaphoreSlim>(lockKey, gate));
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

        var format = path.EndsWith(".png", StringComparison.Ordinal) ? "png_pipe" : InputFormat(await HeaderAsync(path, ct).ConfigureAwait(false));
        var pixels = await RunFfmpegAsync(
            ["-v", "error", "-protocol_whitelist", "file", .. format is null ? Array.Empty<string>() : ["-f", format], "-i", path,
                "-vf", "scale=32:32", "-f", "rawvideo", "-pix_fmt", "rgb24", "-"], captureOutput: true, ct).ConfigureAwait(false);
        return pixels is null ? null : AccentColor.FromPixels(pixels);
    }

    /// <summary>
    /// The ffmpeg demuxer for an image, from its first bytes; null if it isn't a still image. Forcing it means a
    /// file can't pass itself off as something else (a playlist that reads other files, say) whatever its name.
    /// </summary>
    public static string? InputFormat(ReadOnlySpan<byte> header) => header switch
    {
        [0xFF, 0xD8, 0xFF, ..] => "jpeg_pipe",
        [0x89, 0x50, 0x4E, 0x47, ..] => "png_pipe",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => "webp_pipe",
        [(byte)'G', (byte)'I', (byte)'F', (byte)'8', ..] => "gif",
        [(byte)'B', (byte)'M', ..] => "bmp_pipe",
        [(byte)'I', (byte)'I', 0x2A, 0x00, ..] or [(byte)'M', (byte)'M', 0x00, 0x2A, ..] => "tiff_pipe",
        _ => null,
    };

    /// <summary>
    /// Fetches at most <see cref="MaxDownloadBytes"/>, following redirects by hand: only to https on the same site
    /// (thetvdb.com to artworks.thetvdb.com, say), so an artwork host can't point the server anywhere else.
    /// </summary>
    private async Task<bool> DownloadAsync(Uri uri, string remoteUrl, string path, CancellationToken ct)
    {
        for (var hops = 0; ; hops++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (hops >= MaxRedirects || !SameSite(uri, next))
                {
                    LogRedirectRefused(remoteUrl, next.ToString());
                    return false;
                }

                uri = next;
                continue;
            }

            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                LogDownloadFailed(remoteUrl, (int)response.StatusCode);
                return false;
            }

            await using var file = File.Create(path);
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var buffer = new byte[81920];
            long total = 0;
            for (int read; (read = await body.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0;)
            {
                total += read;
                if (total > MaxDownloadBytes)
                {
                    LogDownloadFailed(remoteUrl, (int)response.StatusCode);
                    return false;
                }

                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }

            return true;
        }
    }

    internal static bool SameSite(Uri from, Uri to) =>
        to.Scheme == Uri.UriSchemeHttps && to.HostNameType == UriHostNameType.Dns && to.IsDefaultPort
        && string.Equals(Site(from.Host), Site(to.Host), StringComparison.OrdinalIgnoreCase);

    /// <summary>The last two labels of a host name: "artworks.thetvdb.com" is on "thetvdb.com".</summary>
    private static string Site(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        return labels.Length <= 2 ? host : $"{labels[^2]}.{labels[^1]}";
    }

    /// <summary>Resizes into a temporary file and moves it into place, so a half-written image is never served.</summary>
    private async Task<bool> ResizeAsync(string input, string? format, bool embedded, int width, string target, CancellationToken ct)
    {
        var png = target.EndsWith(".png", StringComparison.Ordinal);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(target)}");
        try
        {
            string[] args =
            [
                "-v", "error", "-y", "-protocol_whitelist", "file", .. format is null ? Array.Empty<string>() : ["-f", format], "-i", input,
                .. embedded ? new[] { "-map", "0:v:0", "-an" } : [],
                "-vf", $"scale='min({width},iw)':-2", "-frames:v", "1", "-update", "1", .. png ? Array.Empty<string>() : ["-q:v", "3"], partial,
            ];
            if (await RunFfmpegAsync(args, captureOutput: false, ct).ConfigureAwait(false) is null || !File.Exists(partial))
            {
                return false;
            }

            File.Move(partial, target, overwrite: true);
            return true;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Runs ffmpeg (a few at a time); its stdout if asked for, else empty, or null if it failed.</summary>
    private async Task<byte[]?> RunFfmpegAsync(string[] args, bool captureOutput, CancellationToken ct)
    {
        var start = new ProcessStartInfo(settings.FfmpegPath) { RedirectStandardOutput = captureOutput, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        await FfmpegSlots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var process = Process.Start(start)!;
            try
            {
                // Both pipes at once, so neither can fill up and stall ffmpeg.
                using var output = new MemoryStream();
                var stdout = captureOutput ? process.StandardOutput.BaseStream.CopyToAsync(output, ct) : Task.CompletedTask;
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
                if (process.ExitCode != 0)
                {
                    LogResizeFailed((await stderr.ConfigureAwait(false)).Trim());
                    return null;
                }

                return output.ToArray();
            }
            catch (OperationCanceledException)
            {
                // Disposing the Process doesn't stop it; a cancelled request shouldn't leave ffmpeg running.
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // Already exited.
                }

                throw;
            }
        }
        finally
        {
            FfmpegSlots.Release();
        }
    }

    /// <summary>Logos with transparency are cached as .png, everything else as .jpg.</summary>
    private static string? Cached(string jpgPath) =>
        File.Exists(jpgPath) ? jpgPath
        : File.Exists(Path.ChangeExtension(jpgPath, ".png")) ? Path.ChangeExtension(jpgPath, ".png")
        : null;

    /// <summary>Marks a cached image as used (at most daily), so trimming keeps what's still being looked at.</summary>
    private static void Touch(string path)
    {
        try
        {
            var now = DateTime.UtcNow;
            if (File.GetLastWriteTimeUtc(path) < now.AddDays(-1))
            {
                File.SetLastWriteTimeUtc(path, now);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Being trimmed right now, or read-only: it's only a hint.
        }
    }

    /// <summary>Just written: now and then, check the cache's size in the background.</summary>
    private string Written(string path)
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _lastTrim) >= (long)TrimInterval.TotalMilliseconds && Interlocked.CompareExchange(ref _trimming, 1, 0) == 0)
        {
            Interlocked.Exchange(ref _lastTrim, now);
            var directory = Directory;
            _ = Task.Run(() =>
            {
                try
                {
                    var removed = Trim(directory, MaxCacheBytes);
                    if (removed > 0)
                    {
                        LogTrimmed(removed);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogTrimFailed(ex);
                }
                finally
                {
                    Volatile.Write(ref _trimming, 0);
                }
            });
        }

        return path;
    }

    /// <summary>
    /// Deletes the least recently used images until the cache is under 90% of <paramref name="maxBytes"/>, if it's
    /// over. Files still being written are left alone. Returns how many were deleted.
    /// </summary>
    internal static int Trim(string directory, long maxBytes)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        var files = new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(f => f.Extension is ".jpg" or ".png" && !f.Name.Contains(".tmp.", StringComparison.Ordinal))
            .ToList();
        var total = files.Sum(f => f.Length);
        if (total <= maxBytes)
        {
            return 0;
        }

        var removed = 0;
        foreach (var file in files.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes / 10 * 9)
            {
                break;
            }

            try
            {
                var length = file.Length;
                file.Delete();
                total -= length;
                removed++;
            }
            catch (IOException)
            {
                // In use; try the next.
            }
        }

        return removed;
    }

    private static async Task<byte[]> HeaderAsync(string path, CancellationToken ct)
    {
        var header = new byte[12];
        await using var file = File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return header[..read];
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download artwork {Url} (HTTP {Status})")]
    private partial void LogDownloadFailed(string url, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Artwork {Url} redirected to {Location}, which isn't followed")]
    private partial void LogRedirectRefused(string url, string location);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Artwork {Url} isn't an image")]
    private partial void LogNotAnImage(string url);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not resize artwork: {Error}")]
    private partial void LogResizeFailed(string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Image cache trimmed: {Count} least recently used images removed")]
    private partial void LogTrimmed(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not trim the image cache")]
    private partial void LogTrimFailed(Exception exception);
}
