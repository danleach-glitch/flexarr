using System.Diagnostics;
using System.Net;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>
/// The Top Shelf's wide cards: the backdrop at 16:9 with the logo lower left. Runs the real ffmpeg; without it
/// installed (CI), there's nothing to check.
/// </summary>
[Collection(nameof(ImageCacheDownloadTests))]
public sealed class ImageCacheWideTests : IDisposable
{
    private const string Backdrop = "https://assets.fanart.tv/fanart/movies/1/moviebackground/a.jpg";
    private const string Logo = "https://assets.fanart.tv/fanart/movies/1/hdmovielogo/a.png";

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-wide-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task TheLogo_SitsLowerLeft_OnTheBackdropCutTo16By9()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // A wide red backdrop (2.4:1, so it has to be cut) and a white logo on a clear background.
        var images = new Dictionary<string, byte[]>
        {
            [Backdrop] = await DrawAsync("color=red:s=1200x500", "backdrop.jpg"),
            [Logo] = await DrawAsync("color=white@1:s=400x100,format=rgba", "logo.png"),
        };
        var cache = Cache(images);

        var path = await cache.GetWideAsync(Backdrop, Logo, 780, default);

        Assert.NotNull(path);
        Assert.Contains("_wide780", path, StringComparison.Ordinal);
        var (width, height) = await SizeAsync(path);
        Assert.Equal((780, 438), (width, height));
        // The logo, 42% of the width at most, lower left; the top right is still the backdrop.
        var logo = await PixelAsync(path, 60, height - 60);
        Assert.True(logo is { R: > 200, G: > 200, B: > 200 }, $"logo pixel {logo}");
        var corner = await PixelAsync(path, width - 20, 20);
        Assert.True(corner is { R: > 200, G: < 60, B: < 60 }, $"corner pixel {corner}");

        // Asked for again: the same card, from the cache.
        Assert.Equal(path, await cache.GetWideAsync(Backdrop, Logo, 780, default));
    }

    [Fact]
    public async Task WithoutALogo_ItsTheBackdrop()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var cache = Cache(new Dictionary<string, byte[]> { [Backdrop] = await DrawAsync("color=red:s=1200x500", "backdrop.jpg") });

        var backdrop = await cache.GetAsync(Backdrop, 780, default);
        Assert.NotNull(backdrop);
        Assert.Equal(backdrop, await cache.GetWideAsync(Backdrop, null, 780, default));
        Assert.Equal(backdrop, await cache.GetWideAsync(Backdrop, Logo, 780, default)); // logo missing
    }

    private ImageCache Cache(Dictionary<string, byte[]> images) =>
        new(new HttpClient(new Artwork(images)), new Settings(Path.Combine(_dir, "cache")), NullLogger<ImageCache>.Instance);

    private async Task<byte[]> DrawAsync(string source, string name)
    {
        var path = Path.Combine(_dir, name);
        await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", source, "-frames:v", "1", path);
        return await File.ReadAllBytesAsync(path);
    }

    private static async Task<(int Width, int Height)> SizeAsync(string path)
    {
        var size = (await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=p=0", path)).Trim().Split(',');
        return (int.Parse(size[0], System.Globalization.CultureInfo.InvariantCulture), int.Parse(size[1], System.Globalization.CultureInfo.InvariantCulture));
    }

    private async Task<(int R, int G, int B)> PixelAsync(string path, int x, int y)
    {
        var raw = Path.Combine(_dir, $"pixel-{Guid.NewGuid():N}.rgb");
        await RunAsync("ffmpeg", "-v", "error", "-y", "-i", path, "-vf", $"format=rgb24,crop=1:1:{x}:{y}", "-f", "rawvideo", "-pix_fmt", "rgb24", raw);
        var bytes = await File.ReadAllBytesAsync(raw);
        return (bytes[0], bytes[1], bytes[2]);
    }

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task<string> RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{program} failed: {await errors}");
        return await output;
    }

    private sealed class Settings(string cache) : IMetadataSettings
    {
        public IReadOnlyList<ArrInstance> ArrInstances { get; } = [];
        public IReadOnlyList<PathMapping> PathMappings { get; } = [];
        public string CacheDirectory => cache;
        public string FfmpegPath => "ffmpeg";
    }

    /// <summary>An artwork host with these images; anything else is missing.</summary>
    private sealed class Artwork(Dictionary<string, byte[]> images) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(images.TryGetValue(request.RequestUri!.ToString(), out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}
