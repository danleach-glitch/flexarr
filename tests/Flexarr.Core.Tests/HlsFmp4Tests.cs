using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Flexarr.Streaming;
using Flexarr.Streaming.Remux;

namespace Flexarr.Core.Tests;

/// <summary>Runs the real ffmpeg on generated clips. Without ffmpeg installed (CI), there's nothing to check.</summary>
public sealed class HlsFmp4Tests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-fmp4-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenFfmpegIsInstalled_A71LosslessTrackBecomes24BitFlac_AndARunStartedMidwayKeepsItsTime()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "20", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s24le", "-ac", "8", clip);
        var plan = new TranscodePlan(0, 1, 640, 360, 1_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 8, BurnSubtitle: null,
            SourceAudioCodec: "pcm_s24le", Fmp4: true, LosslessAudio: true, HighResolutionAudio: true);

        var (audio, firstSeconds) = await SegmentAsync(clip, plan, startSegment: 2);

        Assert.Equal(("flac", 8, "s32"), (audio.Codec, audio.Channels, audio.SampleFormat));
        Assert.InRange(firstSeconds, 11.9, 12.1);
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_DolbyDigitalPlusIsCopiedIntoFragmentedMp4()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "20", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "eac3", "-ac", "6", clip);
        var plan = new TranscodePlan(0, 1, 640, 360, 1_000_000, ToneMap: false, CopyAudio: true, AudioChannels: 6, BurnSubtitle: null,
            SourceAudioCodec: "eac3", Fmp4: true);

        var (audio, firstSeconds) = await SegmentAsync(clip, plan, startSegment: 1);

        Assert.Equal(("eac3", 6), (audio.Codec, audio.Channels));
        Assert.InRange(firstSeconds, 5.5, 6.1);
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_ACopiedVideosFirstFragment_StartsNoEarlierThanZero()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // AAC from the converted sound starts a priming frame before zero; ExoPlayer refuses a negative start time.
        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "10", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "pcm_s16le", clip);
        var plan = new TranscodePlan(0, 1, 640, 360, 1_000_000, ToneMap: false, CopyAudio: false, AudioChannels: 2, BurnSubtitle: null,
            SourceAudioCodec: "pcm_s16le", Fmp4: true, SourceVideoCodec: "h264")
        {
            Repackage = new Repackage([0, 5], "avc1", "avc1.64001e", null, "SDR", StripDolbyVision: false),
        };
        var output = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg", HardwareAcceleration = HardwareAcceleration.None };
        await RunAsync("ffmpeg", [.. FfmpegCommand.Hls(clip, plan, 0, output, settings)]);

        var first = await File.ReadAllBytesAsync(Path.Combine(output, "frag0.m4s"));
        Assert.Equal(0UL, Mp4Fragments.DecodeTime(first, trackId: 1));
        Assert.InRange(Mp4Fragments.DecodeTime(first, trackId: 2)!.Value, 0UL, 48_000UL);
    }

    /// <summary>Makes the segments from <paramref name="startSegment"/>, and reads the first one (behind its init segment).</summary>
    private async Task<((string Codec, int Channels, string SampleFormat) Audio, double FirstSeconds)> SegmentAsync(string clip, TranscodePlan plan, int startSegment)
    {
        var output = Directory.CreateDirectory(Path.Combine(_dir, "out")).FullName;
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg", HardwareAcceleration = HardwareAcceleration.None, SoftwarePreset = "ultrafast" };
        await RunAsync("ffmpeg", [.. FfmpegCommand.Hls(clip, plan, startSegment, output, settings)]);

        Assert.True(File.Exists(Path.Combine(output, "init.mp4")));
        var joined = Path.Combine(_dir, "joined.mp4");
        await File.WriteAllBytesAsync(joined, [.. await File.ReadAllBytesAsync(Path.Combine(output, "init.mp4")),
            .. await File.ReadAllBytesAsync(Path.Combine(output, $"seg{startSegment}.m4s"))]);

        var streams = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-show_streams", "-of", "json", joined)).RootElement.GetProperty("streams");
        var audio = streams.EnumerateArray().Single(s => s.GetProperty("codec_type").GetString() == "audio");
        var packets = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time", "-of", "json", joined))
            .RootElement.GetProperty("packets");
        var first = packets.EnumerateArray().Min(p => double.Parse(p.GetProperty("pts_time").GetString()!, CultureInfo.InvariantCulture));
        return ((audio.GetProperty("codec_name").GetString()!, audio.GetProperty("channels").GetInt32(), audio.GetProperty("sample_fmt").GetString()!), first);
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
