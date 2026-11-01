using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Flexarr.Integrations.Arr;
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

    /// <summary>Downloads and resizes under way or waiting at once; past this, a new image is refused until some finish.</summary>
    internal const int MaxJobs = 256;

    /// <summary>Downloads at once: a fast scroll past hundreds of new posters shouldn't open hundreds of sockets.</summary>
    internal const int MaxDownloads = 8;

    /// <summary>A file still being written that's older than this was left by a crash or a kill, and goes.</summary>
    private static readonly TimeSpan StalePartial = TimeSpan.FromHours(1);

    /// <summary>
    /// The download and resize under way for each image and width (locked on itself). Static: as a typed HttpClient
    /// this class is created per request, and the work must be shared by all of them.
    /// </summary>
    private static readonly Dictionary<string, Job> InFlight = new(StringComparer.Ordinal);

    /// <summary>A page of new posters shouldn't start dozens of ffmpegs at once.</summary>
    private static readonly SemaphoreSlim FfmpegSlots = new(Environment.ProcessorCount, Environment.ProcessorCount);

    private static readonly SemaphoreSlim DownloadSlots = new(MaxDownloads, MaxDownloads);

    /// <summary>
    /// Images that couldn't be fetched, and when to try again (<see cref="Environment.TickCount64"/>), so a page asking
    /// for them again gets its answer at once rather than another trip to the artwork host. Locked with <see cref="InFlight"/>.
    /// </summary>
    private static readonly Dictionary<string, long> Failed = new(StringComparer.Ordinal);

    /// <summary>Remembered failures past this are thrown away (expired ones first), so the list can't grow without end.</summary>
    private const int MaxFailed = 4096;

    private static long _lastTrim = long.MinValue / 2; // so the first write checks
    private static int _trimming;

    private string Directory => Path.Combine(settings.CacheDirectory, "images");

    /// <summary>
    /// The whole download, however slowly the host sends it: HttpClient.Timeout stops counting once the headers are in.
    /// </summary>
    internal TimeSpan DownloadTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Download and resize together, including any wait for an ffmpeg slot.</summary>
    internal TimeSpan WorkTimeout { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long work nobody is waiting for any more carries on, in case it's asked for again: an image scrolled past
    /// on a TV and straight back isn't fetched twice, but one scrolled far past stops taking a download slot.
    /// </summary>
    internal TimeSpan AbandonAfter { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long an image the host says is gone, or a host that can't be reached at all, isn't asked for again.</summary>
    internal TimeSpan GoneFor { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>How long after a failure that may well pass (a server error, a timeout, a dropped connection).</summary>
    internal TimeSpan UnavailableFor { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Returns the path of a cached copy at least <paramref name="width"/> wide, or null if it can't be fetched.</summary>
    /// <param name="remoteUrl">An https URL, an arr:// one (see <see cref="ArtworkUrl.Arr"/>), or (from the library scanner only) a file:// or embedded:// one.</param>
    /// <param name="width">The width wanted; snapped up to one of <see cref="ArtworkUrl.Widths"/>.</param>
    /// <param name="ct">
    /// Stops waiting. The download and resize carry on (on their own timeout) for <see cref="AbandonAfter"/>, for
    /// whoever asks next, and then stop if nobody has.
    /// </param>
    /// <exception cref="ImageCacheBusyException"><see cref="MaxJobs"/> other images are being fetched; try again shortly.</exception>
    public async Task<string?> GetAsync(string remoteUrl, int? width, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(remoteUrl);

        var snapped = ArtworkUrl.Snap(width);
        var key = Hash(Versioned(remoteUrl));
        var target = Path.Combine(Directory, key[..2], $"{key}_w{snapped}.jpg");
        if (Cached(target) is { } hit)
        {
            Touch(hit);
            return hit;
        }

        Job? job;
        lock (InFlight)
        {
            if (Failed.TryGetValue(target, out var retryAt))
            {
                if (Environment.TickCount64 < retryAt)
                {
                    return null;
                }

                Failed.Remove(target);
            }

            if (!InFlight.TryGetValue(target, out job))
            {
                if (InFlight.Count >= MaxJobs)
                {
                    throw new ImageCacheBusyException();
                }

                var started = job = new Job(target);
                InFlight.Add(target, job);
                job.Work = Task.Run(() => ShareAsync(remoteUrl, snapped, started));
            }

            job.Waiters++;
            job.Version++; // so a pending abandon no longer applies
        }

        try
        {
            return await job.Work.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            Leave(job);
        }
    }

    /// <summary>One download and resize, shared by everyone who asks for this image at this width while it runs.</summary>
    private async Task<string?> ShareAsync(string remoteUrl, int snapped, Job job)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(job.Abandoned.Token);
            timeout.CancelAfter(WorkTimeout);
            return await FetchAsync(remoteUrl, snapped, job, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!job.Abandoned.IsCancellationRequested)
            {
                LogDownloadFailed(remoteUrl, 0); // timed out
                job.RetryAfter = UnavailableFor;
            }

            return null;
        }
        finally
        {
            // Done with: the next request finds the file or, if this failed, the failure (for a while; then it's
            // tried again). Abandoned work was taken out already, and newer work for the same image may be there in
            // its place.
            lock (InFlight)
            {
                if (InFlight.TryGetValue(job.Target, out var current) && current == job)
                {
                    InFlight.Remove(job.Target);
                    if (job.RetryAfter > TimeSpan.Zero)
                    {
                        Remember(job.Target, job.RetryAfter);
                    }
                }
            }
        }
    }

    /// <summary>Notes a failed image until <paramref name="retryAfter"/> from now. Called under the <see cref="InFlight"/> lock.</summary>
    private static void Remember(string target, TimeSpan retryAfter)
    {
        var now = Environment.TickCount64;
        if (Failed.Count >= MaxFailed)
        {
            foreach (var expired in Failed.Where(f => f.Value <= now).Select(f => f.Key).ToList())
            {
                Failed.Remove(expired);
            }

            if (Failed.Count >= MaxFailed)
            {
                Failed.Clear();
            }
        }

        Failed[target] = now + (long)retryAfter.TotalMilliseconds;
    }

    /// <summary>How long after a download the host answered with this HTTP status it isn't tried again.</summary>
    private TimeSpan RetryAfterStatus(int status) =>
        status is >= 500 or 408 or 429 ? UnavailableFor : GoneFor;

    /// <summary>How long after a request that got no answer: a host that doesn't exist or refuses is gone for a while.</summary>
    private TimeSpan RetryAfterError(HttpRequestException ex) =>
        ex.StatusCode is { } status ? RetryAfterStatus((int)status)
        : ex.HttpRequestError is HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError ? GoneFor
        : UnavailableFor;

    /// <summary>A waiter has its answer or gave up. Once the last has gone, the work stops after <see cref="AbandonAfter"/>.</summary>
    private void Leave(Job job)
    {
        int version;
        lock (InFlight)
        {
            if (--job.Waiters > 0 || job.Work.IsCompleted)
            {
                return;
            }

            version = job.Version;
        }

        _ = Task.Delay(AbandonAfter).ContinueWith(_ => Abandon(job, version), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private static void Abandon(Job job, int version)
    {
        lock (InFlight)
        {
            // Asked for again meanwhile, or over already.
            if (job.Waiters > 0 || job.Version != version || !InFlight.TryGetValue(job.Target, out var current) || current != job)
            {
                return;
            }

            // Out of the map first, so nobody can join work that's about to stop.
            InFlight.Remove(job.Target);
        }

        job.Abandoned.Cancel();
    }

    /// <summary>A shared download and resize, and how many are waiting for it. Everything but the token and <see cref="RetryAfter"/> is changed under the <see cref="InFlight"/> lock.</summary>
    private sealed class Job(string target)
    {
        public string Target { get; } = target;
        public Task<string?> Work { get; set; } = Task.FromResult<string?>(null);
        public int Waiters { get; set; }
        public int Version { get; set; }

        /// <summary>Set by the work itself when it fails: how long the failure is remembered (zero: not at all).</summary>
        public TimeSpan RetryAfter { get; set; }

        /// <summary>Not disposed: it has no timer to free, and a late Cancel mustn't throw.</summary>
        public CancellationTokenSource Abandoned { get; } = new();
    }

    private async Task<string?> FetchAsync(string remoteUrl, int snapped, Job job, CancellationToken ct)
    {
        var target = job.Target;
        if (Cached(target) is { } raced)
        {
            return raced;
        }

        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        // A still from a home video, taken where the URL says.
        if (ArtworkUrl.TryParseFrame(remoteUrl, out var video, out var seconds))
        {
            return File.Exists(video) && await ResizeAsync(video, null, embedded: true, snapped, target, ct, seconds).ConfigureAwait(false) ? Written(target) : null;
        }

        // Local artwork: cover.jpg or poster.jpg next to the files, or art embedded in a track.
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

        // Artwork a configured *arr holds (Lidarr's): fetched from it with its API key. An instance that's been removed
        // from the settings is remembered as gone for a while, like a host that doesn't answer.
        ArrInstance? arr = null;
        if (ArtworkUrl.TryParseArr(remoteUrl, out var instanceName, out var apiPath))
        {
            arr = settings.ArrInstances.FirstOrDefault(a => string.Equals(a.Name, instanceName, StringComparison.OrdinalIgnoreCase));
            if (arr is null)
            {
                job.RetryAfter = GoneFor;
                return null;
            }
        }

        Uri? uri = null;
        if (arr is null && (!Uri.TryCreate(ArtworkUrl.ForWidth(remoteUrl, snapped), UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var source = $"{target}.{Guid.NewGuid():N}.download";
        try
        {
            await DownloadSlots.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var saved = arr is not null
                    ? await DownloadArrAsync(arr, apiPath, remoteUrl, source, job, ct).ConfigureAwait(false)
                    : await DownloadAsync(uri!, remoteUrl, source, job, ct).ConfigureAwait(false);
                if (!saved)
                {
                    return null;
                }
            }
            finally
            {
                DownloadSlots.Release();
            }

            var downloaded = InputFormat(await HeaderAsync(source, ct).ConfigureAwait(false));
            if (downloaded is null)
            {
                LogNotAnImage(remoteUrl);
                job.RetryAfter = GoneFor;
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
            job.RetryAfter = RetryAfterError(ex);
            return null;
        }
        catch (IOException ex)
        {
            // The host closing the connection mid-image (HttpIOException), or the disk: this image has failed, not
            // whatever asked for it (a whole metadata refresh, say).
            LogFetchFailed(remoteUrl, ex);
            job.RetryAfter = UnavailableFor;
            return null;
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>
    /// The backdrop cut to 16:9 with the title's logo on it, lower left over a soft shade: the Apple TV's Top Shelf
    /// shows these as wide cards. Without a logo (or if it can't be fetched) it's the backdrop alone.
    /// </summary>
    /// <returns>The cached image's path, or null if the backdrop can't be fetched.</returns>
    /// <exception cref="ImageCacheBusyException">As <see cref="GetAsync"/>.</exception>
    public async Task<string?> GetWideAsync(string backdropUrl, string? logoUrl, int? width, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(backdropUrl);

        var snapped = ArtworkUrl.Snap(width);
        var backdrop = await GetAsync(backdropUrl, snapped, ct).ConfigureAwait(false);
        var logo = backdrop is null || logoUrl is null ? null : await GetAsync(logoUrl, 780, ct).ConfigureAwait(false);
        if (backdrop is null || logo is null)
        {
            return backdrop;
        }

        var key = Hash($"{backdropUrl}\n{logoUrl}");
        var target = Path.Combine(Directory, key[..2], $"{key}_wide{snapped}.jpg");
        if (Cached(target) is { } hit)
        {
            Touch(hit);
            return hit;
        }

        var backdropFormat = InputFormat(await HeaderAsync(backdrop, ct).ConfigureAwait(false));
        var logoFormat = InputFormat(await HeaderAsync(logo, ct).ConfigureAwait(false));
        if (backdropFormat is null || logoFormat is null)
        {
            return backdrop;
        }

        // Two requests for the same card at once both draw it; the second move just replaces the first.
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var w = snapped;
        var h = w * 9 / 16 / 2 * 2;
        var partial = Path.Combine(Path.GetDirectoryName(target)!, $"{key}_wide{snapped}.{Guid.NewGuid():N}.tmp.jpg");
        try
        {
            // The shade is drawn tiny and scaled up, which is quick and smooth: darkest in the lower left, where the
            // logo goes, clear across the top and right. The logo fits a box 42% wide and 30% high, keeping its shape.
            string[] args =
            [
                "-v", "error", "-y", "-protocol_whitelist", "file",
                "-f", backdropFormat, "-i", backdrop, "-f", logoFormat, "-i", logo,
                "-filter_complex",
                $"[0:v]scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h},setsar=1[bg];"
                + "color=black:s=64x36:d=1,format=rgba,geq=r=0:g=0:b=0:a='200*pow(clip((Y/H-0.25)/0.75\\,0\\,1)\\,1.4)*clip(1.2-X/W\\,0\\,1)'"
                + $",scale={w}:{h}[shade];"
                + $"[1:v]scale={w * 42 / 100}:{h * 30 / 100}:force_original_aspect_ratio=decrease[logo];"
                + "[bg][shade]overlay[dim];[dim][logo]overlay=x=main_w*0.06:y=main_h-overlay_h-main_h*0.09:format=auto",
                "-frames:v", "1", "-update", "1", "-q:v", "3", partial,
            ];
            if (await RunFfmpegAsync(args, captureOutput: false, ct).ConfigureAwait(false) is null || !File.Exists(partial))
            {
                return backdrop;
            }

            File.Move(partial, target, overwrite: true);
            return Written(target);
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Ambient colour for an image (see <see cref="AccentColor"/>), sampled from a tiny resize.</summary>
    public async Task<string?> AccentColorAsync(string remoteUrl, CancellationToken ct)
    {
        string? path;
        try
        {
            path = await GetAsync(remoteUrl, 92, ct).ConfigureAwait(false);
        }
        catch (ImageCacheBusyException)
        {
            return null; // left unset, so the next refresh tries again
        }

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
    /// (thetvdb.com to artworks.thetvdb.com, say), or where a known host keeps its pictures (<see cref="KnownRedirects"/>),
    /// so an artwork host can't point the server anywhere else.
    /// </summary>
    private async Task<bool> DownloadAsync(Uri uri, string remoteUrl, string path, Job job, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(DownloadTimeout);
        var ct = timeout.Token;
        for (var hops = 0; ; hops++)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
            {
                var next = location.IsAbsoluteUri ? location : new Uri(uri, location);
                if (hops >= MaxRedirects || !MayFollow(uri, next))
                {
                    LogRedirectRefused(remoteUrl, next.ToString());
                    job.RetryAfter = GoneFor;
                    return false;
                }

                uri = next;
                continue;
            }

            return await SaveAsync(response, remoteUrl, path, job, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Fetches artwork from a configured *arr's API with its key. No redirects: the instance answers the API itself, and
    /// a bounce (to a sign-in page, say) means the key wasn't accepted.
    /// </summary>
    private async Task<bool> DownloadArrAsync(ArrInstance instance, string apiPath, string remoteUrl, string path, Job job, CancellationToken cancel)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(DownloadTimeout);
        using var request = ArrClient.CreateRequest(instance, HttpMethod.Get, apiPath);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        return await SaveAsync(response, remoteUrl, path, job, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Writes a successful answer's body to <paramref name="path"/>, up to <see cref="MaxDownloadBytes"/>.</summary>
    private async Task<bool> SaveAsync(HttpResponseMessage response, string remoteUrl, string path, Job job, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxDownloadBytes)
        {
            LogDownloadFailed(remoteUrl, (int)response.StatusCode);
            job.RetryAfter = response.IsSuccessStatusCode ? GoneFor : RetryAfterStatus((int)response.StatusCode);
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
                job.RetryAfter = GoneFor;
                return false;
            }

            await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }

        return true;
    }

    internal static bool SameSite(Uri from, Uri to) =>
        PlainHttps(to) && string.Equals(Site(from.Host), Site(to.Host), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Artwork hosts that hand every picture to another site: Open Library's covers (books' and authors') live on the
    /// Internet Archive, and covers.openlibrary.org only redirects there.
    /// </summary>
    private static readonly (string From, string ToSite)[] KnownRedirects = [("covers.openlibrary.org", "archive.org")];

    /// <summary>Whether a redirect is followed: to https on the same site, or from a known host to where it keeps its pictures.</summary>
    internal static bool MayFollow(Uri from, Uri to) =>
        SameSite(from, to)
        || (PlainHttps(to) && KnownRedirects.Any(r => string.Equals(from.Host, r.From, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Site(to.Host), r.ToSite, StringComparison.OrdinalIgnoreCase)));

    private static bool PlainHttps(Uri to) => to.Scheme == Uri.UriSchemeHttps && to.HostNameType == UriHostNameType.Dns && to.IsDefaultPort;

    /// <summary>The last two labels of a host name: "artworks.thetvdb.com" is on "thetvdb.com".</summary>
    private static string Site(string host)
    {
        var labels = host.TrimEnd('.').Split('.');
        return labels.Length <= 2 ? host : $"{labels[^2]}.{labels[^1]}";
    }

    /// <summary>Resizes into a temporary file and moves it into place, so a half-written image is never served.</summary>
    /// <param name="input">An image, or a media file (<paramref name="embedded"/>) whose first picture is taken.</param>
    /// <param name="format">The image's format, when it's an image.</param>
    /// <param name="embedded">Take the media file's picture: a track's cover art, or a video's frame.</param>
    /// <param name="width">The widest it may be.</param>
    /// <param name="target">Where the result goes.</param>
    /// <param name="ct">Stops ffmpeg.</param>
    /// <param name="seek">For a video, how many seconds in the frame is.</param>
    private async Task<bool> ResizeAsync(string input, string? format, bool embedded, int width, string target, CancellationToken ct, double? seek = null)
    {
        var png = target.EndsWith(".png", StringComparison.Ordinal);
        var partial = Path.Combine(Path.GetDirectoryName(target)!, $"{Path.GetFileNameWithoutExtension(target)}.{Guid.NewGuid():N}.tmp{Path.GetExtension(target)}");
        try
        {
            string[] args =
            [
                "-v", "error", "-y", "-protocol_whitelist", "file", .. format is null ? Array.Empty<string>() : ["-f", format],
                .. seek is { } at ? new[] { "-ss", at.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) } : [], "-i", input,
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
    /// over. Files still being written (downloads and resizes) count towards the size but are left alone, unless
    /// they're so old that whatever was writing them is gone. Returns how many were deleted.
    /// </summary>
    internal static int Trim(string directory, long maxBytes)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return 0;
        }

        var stale = DateTime.UtcNow - StalePartial;
        var removed = 0;
        long total = 0;
        var images = new List<FileInfo>();
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*", SearchOption.AllDirectories))
        {
            if (file.Extension == ".download" || file.Name.Contains(".tmp.", StringComparison.Ordinal))
            {
                if (file.LastWriteTimeUtc < stale && TryDelete(file))
                {
                    removed++;
                }
                else
                {
                    total += file.Length;
                }
            }
            else if (file.Extension is ".jpg" or ".png")
            {
                images.Add(file);
                total += file.Length;
            }
        }

        if (total <= maxBytes)
        {
            return removed;
        }

        foreach (var file in images.OrderBy(f => f.LastWriteTimeUtc))
        {
            if (total <= maxBytes / 10 * 9)
            {
                break;
            }

            var length = file.Length;
            if (TryDelete(file))
            {
                total -= length;
                removed++;
            }
        }

        return removed;
    }

    private static bool TryDelete(FileInfo file)
    {
        try
        {
            file.Delete();
            return true;
        }
        catch (IOException)
        {
            return false; // In use; try the next.
        }
    }

    private static async Task<byte[]> HeaderAsync(string path, CancellationToken ct)
    {
        var header = new byte[12];
        await using var file = File.OpenRead(path);
        var read = await file.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        return header[..read];
    }

    /// <summary>
    /// The cache's name for <paramref name="remoteUrl"/>: a local cover or track also by when it last changed and its
    /// size, so a replaced cover.jpg (or a retagged file) is read again rather than served from the copy of the old
    /// one, even when the new file kept an older date.
    /// </summary>
    private static string Versioned(string remoteUrl)
    {
        if (!ArtworkUrl.IsLocal(remoteUrl))
        {
            return remoteUrl;
        }

        var local = remoteUrl.StartsWith("embedded://", StringComparison.Ordinal) ? remoteUrl["embedded://".Length..] : remoteUrl["file://".Length..];
        var file = new FileInfo(local);
        if (!file.Exists)
        {
            return remoteUrl; // the read then finds nothing either way
        }

        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        return $"{remoteUrl}\n{file.LastWriteTimeUtc.Ticks.ToString(invariant)}\n{file.Length.ToString(invariant)}";
    }

    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not download artwork {Url} (HTTP {Status})")]
    private partial void LogDownloadFailed(string url, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not fetch artwork {Url}")]
    private partial void LogFetchFailed(string url, Exception exception);

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

/// <summary>Too many other images are being fetched right now; asking again in a moment will likely work.</summary>
public sealed class ImageCacheBusyException : Exception
{
    public ImageCacheBusyException()
        : base("The image cache is busy fetching other images.")
    {
    }

    public ImageCacheBusyException(string message)
        : base(message)
    {
    }

    public ImageCacheBusyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
