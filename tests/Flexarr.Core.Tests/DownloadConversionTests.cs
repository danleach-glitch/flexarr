using System.Diagnostics;
using System.Text.Json;
using Flexarr.Core.Media;
using Flexarr.Streaming;

namespace Flexarr.Core.Tests;

/// <summary>Runs the real ffmpeg on a generated clip. Without ffmpeg installed (CI), there's nothing to check.</summary>
public sealed class DownloadConversionTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-download-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenFfmpegIsInstalled_ADownloadIsAnMp4WithH264AacAndItsSubtitles()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=1280x720:rate=24", "-f", "lavfi", "-i", "sine=frequency=440",
            "-t", "4", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "ac3", "-ac", "6", clip);
        var srt = Path.Combine(_dir, "clip.fr.srt");
        await File.WriteAllTextAsync(srt, "1\n00:00:00,500 --> 00:00:02,000\nBonjour\n");
        var file = new MediaFile
        {
            Path = clip, Container = "matroska,webm", VideoCodec = "h264", Width = 1280, Height = 720, AudioCodec = "ac3", AudioChannels = 6,
            DurationSeconds = 4, Bitrate = 3_000_000,
            Streams =
            [
                new MediaStream { Index = 0, Type = StreamType.Video, Codec = "h264", Width = 1280, Height = 720 },
                new MediaStream { Index = 1, Type = StreamType.Audio, Codec = "ac3", Channels = 6, IsDefault = true },
            ],
        };
        var subtitles = new[] { new SubtitleTrack("x0", null, srt, "subrip", "fre", null, false, false) };
        var settings = new TranscodeSettings { CacheDirectory = _dir, FfmpegPath = "ffmpeg", HardwareAcceleration = HardwareAcceleration.None, SoftwarePreset = "ultrafast" };

        // Medium: converted, stereo AAC, the subtitle file inside as a French track.
        var medium = DownloadPlanner.Plan(file, DownloadQuality.Medium, subtitles)!;
        var mediumOut = Path.Combine(_dir, "medium.mp4");
        await RunAsync("ffmpeg", [.. FfmpegCommand.Mp4(clip, medium.Convert, medium.VideoIndex, medium.AudioIndex, medium.VideoCodec, medium.Subtitles, mediumOut, settings)]);
        var streams = await StreamsAsync(mediumOut);
        Assert.Contains(streams, s => s is ("video", "h264", _, _));
        Assert.Contains(streams, s => s is ("audio", "aac", 2, _));
        Assert.Contains(streams, s => s is ("subtitle", "mov_text", _, "fre"));

        // Original: the H.264 and AC-3 copied as they are into an MP4, with the subtitle file brought in.
        var original = DownloadPlanner.Plan(file, DownloadQuality.Original, subtitles)!;
        var originalOut = Path.Combine(_dir, "original.mp4");
        await RunAsync("ffmpeg", [.. FfmpegCommand.Mp4(clip, original.Convert, original.VideoIndex, original.AudioIndex, original.VideoCodec, original.Subtitles, originalOut, settings)]);
        streams = await StreamsAsync(originalOut);
        Assert.Contains(streams, s => s is ("video", "h264", _, _));
        Assert.Contains(streams, s => s is ("audio", "ac3", 6, _));
        Assert.Contains(streams, s => s is ("subtitle", "mov_text", _, "fre"));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_ATrackThePhoneCantPlay_BecomesAnM4aOfStereoAac()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // 5.1 PCM in a Matroska file: neither the container nor the channels suit a phone.
        var clip = Path.Combine(_dir, "track.mka");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", "-ac", "6", "-c:a", "pcm_s16le", clip);
        var file = new MediaFile
        {
            Path = clip, Container = "matroska,webm", AudioCodec = "pcm_s16le", AudioChannels = 6, DurationSeconds = 2, Bitrate = 4_600_000,
            Streams = [new MediaStream { Index = 0, Type = StreamType.Audio, Codec = "pcm_s16le", Channels = 6, IsDefault = true }],
        };

        var plan = DownloadPlanner.Plan(file, DownloadQuality.Original, [])!;
        Assert.True(plan.IsAudioOnly);
        Assert.False(plan.SendAsIs);
        var output = Path.Combine(_dir, "track.m4a");
        await RunAsync("ffmpeg", [.. FfmpegCommand.M4a(clip, plan.AudioIndex!.Value, plan.Convert!.AudioChannels, output)]);

        var streams = await StreamsAsync(output);
        var audio = Assert.Single(streams);
        Assert.Equal(("audio", "aac", 2), (audio.Type, audio.Codec, audio.Channels));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_APodcastEpisodeThatsReallyAPlaylist_IsRefused_AndASoundFileIsConverted()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // A track in someone's library, and an "episode" that's a playlist naming it.
        var track = Path.Combine(_dir, "track.flac");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", track);
        var playlist = Path.Combine(_dir, "episode.part.m3u8");
        await File.WriteAllTextAsync(playlist, $"#EXTM3U\n#EXT-X-VERSION:3\n#EXT-X-TARGETDURATION:10\n#EXT-X-MEDIA-SEQUENCE:0\n#EXTINF:2.0,\n{track}\n#EXT-X-ENDLIST\n");
        var list = Path.Combine(_dir, "episode.part.ogg");
        await File.WriteAllTextAsync(list, $"ffconcat version 1.0\nfile '{track}'\n");

        foreach (var episode in new[] { playlist, list })
        {
            var stolen = Path.Combine(_dir, "stolen.m4a");
            Assert.NotEqual(0, await ExitCodeAsync("ffmpeg", [.. FfmpegCommand.M4a(episode, 0, 2, stolen, soundFileOnly: true)]));
            Assert.False(File.Exists(stolen) && new FileInfo(stolen).Length > 0);
        }

        // What podcasts really come as still converts, under a name that says nothing about it.
        foreach (var (format, codec) in new[] { ("flac", "flac"), ("wav", "pcm_s16le"), ("ogg", "flac"), ("matroska", "pcm_s16le"), ("mp4", "aac"), ("adts", "aac"), ("mp3", "mp3") })
        {
            var episode = Path.Combine(_dir, $"{format}.part.php");
            await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "sine=frequency=440", "-t", "2", "-c:a", codec, "-f", format, episode);
            var output = Path.Combine(_dir, $"{format}.m4a");
            await RunAsync("ffmpeg", [.. FfmpegCommand.M4a(episode, 0, 2, output, soundFileOnly: true)]);
            var audio = Assert.Single(await StreamsAsync(output));
            Assert.Equal(("audio", "aac"), (audio.Type, audio.Codec));
        }
    }

    private static async Task<int> ExitCodeAsync(string program, params string[] args)
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
        await Task.WhenAll(output, errors);
        return process.ExitCode;
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

    private static async Task<List<(string Type, string Codec, int Channels, string? Language)>> StreamsAsync(string path)
    {
        var json = JsonDocument.Parse(await RunAsync("ffprobe", "-v", "error", "-show_streams", "-of", "json", path));
        return json.RootElement.GetProperty("streams").EnumerateArray().Select(s => (
            s.GetProperty("codec_type").GetString()!,
            s.GetProperty("codec_name").GetString()!,
            s.TryGetProperty("channels", out var c) ? c.GetInt32() : 0,
            s.TryGetProperty("tags", out var tags) && tags.TryGetProperty("language", out var l) ? l.GetString() : null)).ToList();
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
