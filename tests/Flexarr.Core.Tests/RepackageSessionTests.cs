using System.Diagnostics;
using System.Globalization;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>Repackaging on the real ffmpeg, with a generated MKV. Without ffmpeg installed (CI), there's nothing to check.</summary>
public sealed class RepackageSessionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-repackage-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenFfmpegIsInstalled_SegmentsAreTheOriginalVideo_Unbroken_AndASecondSessionStartsToo()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "180", "-c:v", "libx264", "-g", "48", "-keyint_min", "48", "-sc_threshold", "0", "-pix_fmt", "yuv420p", "-c:a", "aac", clip);
        var keyframes = MatroskaCues.Read(clip);
        Assert.NotNull(keyframes);
        Assert.True(keyframes.Count > 10, $"the clip's keyframe index wasn't read ({keyframes.Count} keyframes, {new FileInfo(clip).Length} bytes)");
        var facts = RemuxIndex.ParseFacts(await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", clip));
        Assert.NotNull(facts);
        var file = new MediaFile
        {
            Path = clip, Container = "mkv", VideoCodec = "h264", Width = 640, Height = 360, DurationSeconds = 180, Bitrate = 1_000_000,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264", Width = 640, Height = 360 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "aac", Channels = 2, IsDefault = true },
            ],
        };
        var profile = new DeviceProfile { Containers = ["mp4"], VideoCodecs = ["h264", "hevc"], AudioCodecs = ["aac"], Hdr = true, MaxAudioChannels = 8, Fmp4 = true };
        var converting = PlaybackDecider.Decide(file, profile, [], new PlaybackOptions()).Transcode!;
        var plan = PlaybackDecider.Repackaged(converting, file, profile, new PlaybackOptions(), new RemuxInfo(keyframes, facts))!;
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg", HardwareAcceleration = HardwareAcceleration.None };

        await using var first = new TranscodeSession("a", clip, 180, plan, settings, NullLogger.Instance) { StartSeconds = 30 };
        var started = Stopwatch.StartNew();
        Assert.NotNull(await first.GetInitAsync(CancellationToken.None));
        var segment = first.SegmentAt(30);
        var joined = new List<byte>(await File.ReadAllBytesAsync(Path.Combine(first.Directory, "init.mp4")));
        for (var i = segment; i < segment + 3; i++)
        {
            joined.AddRange(await File.ReadAllBytesAsync((await first.GetSegmentAsync(i, CancellationToken.None))!));
        }

        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"first session took {started.Elapsed}");
        var (codec, times) = await FramesAsync([.. joined]);
        Assert.Equal("h264", codec);
        Assert.Equal(plan.Repackage!.SegmentStarts[segment], times[0], 2);
        Assert.All(times.Zip(times.Skip(1)), pair => Assert.InRange(pair.Second - pair.First, 0.01, 0.05)); // every frame, no gaps

        // Another session (another screen, or the same one after a jump) while the first is still open.
        await using var second = new TranscodeSession("b", clip, 180, plan, settings, NullLogger.Instance) { StartSeconds = 150 };
        started.Restart();
        Assert.NotNull(await second.GetInitAsync(CancellationToken.None));
        Assert.NotNull(await second.GetSegmentAsync(second.SegmentAt(150), CancellationToken.None));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(20), $"second session took {started.Elapsed}");
    }

    private async Task<(string Codec, List<double> Times)> FramesAsync(byte[] mp4)
    {
        var path = Path.Combine(_dir, "joined.mp4");
        await File.WriteAllBytesAsync(path, mp4);
        var codec = (await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", path)).Trim();
        var times = (await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "csv=p=0", path))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => double.Parse(l.Split(',')[0], CultureInfo.InvariantCulture))
            .Order()
            .ToList();
        return (codec, times);
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
}
