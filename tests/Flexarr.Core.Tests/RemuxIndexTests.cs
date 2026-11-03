using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

public sealed class RemuxIndexTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-remux-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task FactsRememberedByAnOlderServer_AreReadAgainOnce_EvenWhenTheFileHasNoFrameRate()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Stands in for ffprobe on a file whose frame rate can't be read: it counts its runs.
        var runs = Path.Combine(_dir, "runs");
        var ffprobe = Path.Combine(_dir, "ffprobe");
        File.WriteAllText(ffprobe, $$"""
            #!/bin/sh
            echo run >> '{{runs}}'
            echo '{"streams":[{"codec_name":"h264","width":1920,"height":1080}]}'
            """);
        File.SetUnixFileMode(ffprobe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var film = Path.Combine(_dir, "film.mp4");
        File.WriteAllText(film, "x");

        // As an older server remembered it: no version, no frame rate.
        var info = new FileInfo(film);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Create(CultureInfo.InvariantCulture, $"{film}|{info.Length}|{info.LastWriteTimeUtc.Ticks}"))))[..32];
        var cache = Directory.CreateDirectory(Path.Combine(_dir, "cache", "remux-index")).FullName;
        File.WriteAllText(Path.Combine(cache, key + ".json"),
            """{"keyframes":[0,2,4],"video":{"codec":"h264","width":1920,"height":1080,"interlaced":false}}""");

        var settings = new TranscodeSettings { CacheDirectory = Path.Combine(_dir, "cache") };
        using (var index = new RemuxIndex(settings, ffprobe, NullLogger<RemuxIndex>.Instance))
        {
            Assert.NotNull(await index.GetAsync(film, CancellationToken.None));
            Assert.NotNull(await index.GetAsync(film, CancellationToken.None));
        }

        // A restart reads what was saved, and doesn't ask again either.
        using (var index = new RemuxIndex(settings, ffprobe, NullLogger<RemuxIndex>.Instance))
        {
            Assert.Equal(RemuxIndex.FactsVersion, (await index.GetAsync(film, CancellationToken.None))!.FactsVersion);
        }

        Assert.Single(File.ReadAllLines(runs));
    }
}
