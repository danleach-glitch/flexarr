using System.Diagnostics;
using System.Globalization;
using Flexarr.Streaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>
/// The ladder made for real (docs/remote-streaming.md): a 1080p clip converted at 1080p, 720p and 480p by VideoToolbox
/// through the server's own ffmpeg arguments, every segment of every quality starting at the same moment, a run started
/// part way through included. Only on a Mac with ffmpeg; elsewhere there's nothing to check.
/// </summary>
public sealed class LadderEncodeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-ladder-encode-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenVideoToolboxIsThere_EveryQualityCutsAtTheSameMoments()
    {
        if (!OperatingSystem.IsMacOS() || !await HasVideoToolboxAsync())
        {
            return;
        }

        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc2=size=1920x1080:rate=24000/1001", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "40", "-c:v", "libx264", "-preset", "ultrafast", "-g", "250", "-pix_fmt", "yuv420p", "-c:a", "aac", "-ac", "2", clip);
        var top = new TranscodePlan(0, 1, 1920, 1080, 7_744_000, ToneMap: false, CopyAudio: true, AudioChannels: 2, BurnSubtitle: null, "aac", Fmp4: true, "h264");
        await using var manager = new TranscodeManager(
            () => new TranscodeSettings { CacheDirectory = _dir, HardwareAcceleration = HardwareAcceleration.VideoToolbox, SoftwareFallback = false }, NullLoggerFactory.Instance);
        var lead = await manager.CreateAsync("main", clip, 40, top, openingSeconds: FfmpegCommand.RemoteOpeningSegmentSeconds, ladder:
        [
            top with { TargetWidth = 854, TargetHeight = 480, VideoBitrate = 1_244_000 },
            top with { TargetWidth = 1280, TargetHeight = 720, VideoBitrate = 3_744_000 },
            top,
        ]);
        var qualities = lead.Ladder!.Variants;
        const int Segments = 8; // three 1-second ones, then 6 seconds each: 0 to 33 s

        // 480p and 720p from the start; 1080p first asked for at segment 5 (15 s in), as when a player moves up there.
        var starts = new Dictionary<(int Quality, int Segment), double>();
        foreach (var (quality, from) in new[] { (0, 0), (1, 0), (2, 5) })
        {
            for (var n = from; n < Segments; n++)
            {
                var segment = await qualities[quality].GetSegmentAsync(n, CancellationToken.None);
                Assert.NotNull(segment);
                starts[(quality, n)] = await FirstFrameSecondsAsync(qualities[quality].Directory, segment!);
            }
        }

        for (var n = 0; n < Segments; n++)
        {
            var expected = FfmpegCommand.SegmentStartSeconds(n, FfmpegCommand.RemoteOpeningSegmentSeconds);
            foreach (var quality in Enumerable.Range(0, 3).Where(q => starts.ContainsKey((q, n))))
            {
                // Within a frame of the grid (1/24 s), and the same in every quality to a millisecond, a run started
                // part way through included.
                Assert.InRange(starts[(quality, n)], expected - 0.001, expected + 0.042);
                Assert.InRange(starts[(quality, n)] - starts[(0, n)], -0.001, 0.001);
            }
        }
    }

    /// <summary>When a segment's first picture is shown, read from it behind its quality's init segment.</summary>
    private async Task<double> FirstFrameSecondsAsync(string directory, string segment)
    {
        var joined = Path.Combine(_dir, $"probe-{Guid.NewGuid():N}.mp4");
        await File.WriteAllBytesAsync(joined, [.. await File.ReadAllBytesAsync(Path.Combine(directory, "init.mp4")), .. await File.ReadAllBytesAsync(segment)]);
        var output = await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", "-read_intervals", "%+#1", joined);
        File.Delete(joined);
        return double.Parse(output.Split('\n', StringSplitOptions.RemoveEmptyEntries)[0].Trim().TrimEnd(','), CultureInfo.InvariantCulture);
    }

    private static async Task<bool> HasVideoToolboxAsync()
    {
        try
        {
            return (await RunAsync("ffmpeg", "-hide_banner", "-encoders")).Contains("h264_videotoolbox", StringComparison.Ordinal);
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
