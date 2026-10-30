using System.Diagnostics;
using System.Globalization;
using Flexarr.Streaming.Remux;

namespace Flexarr.Core.Tests;

public sealed class MatroskaCuesTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-cues-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task WhenFfmpegIsInstalled_TheIndexMatchesEveryKeyframeFfprobeFinds()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // Keyframes every 2 seconds, plus extra ones forced at odd times (as scene cuts would be).
        var clip = Path.Combine(_dir, "clip.mkv");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "testsrc=size=320x180:rate=24", "-t", "30", "-c:v", "libx264", "-g", "48",
            "-force_key_frames", "3.5,7.25,19.9", "-pix_fmt", "yuv420p", clip);
        var expected = (await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", clip))
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(l => l.Split(',')[1].Contains('K'))
            .Select(l => double.Parse(l.Split(',')[0], CultureInfo.InvariantCulture))
            .ToList();

        var cues = MatroskaCues.Read(clip);

        Assert.NotNull(cues);
        Assert.Equal(expected.Count, cues.Count);
        Assert.All(expected.Zip(cues), pair => Assert.Equal(pair.First, pair.Second, 3));
        Assert.Contains(cues, t => Math.Abs(t - 7.25) < 0.05);
    }

    /// <summary>
    /// ffmpeg writes a CRC-32 element before the track entries. Its four bytes aren't elements, and once happened to
    /// look like a three-byte element ID with a size that ran off the end, which lost the whole index.
    /// </summary>
    [Fact]
    public void ATracksChecksumWhoseBytesLookLikeAnElement_DoesntLoseTheIndex()
    {
        static byte[] El(byte[] id, params byte[][] parts)
        {
            var body = parts.SelectMany(p => p).ToArray();
            return [.. id, (byte)(0x80 | body.Length), .. body];
        }

        var tracks = El([0x16, 0x54, 0xAE, 0x6B],
            El([0xBF], [0x2F, 0x10, 0x97, 0x2A]),                       // CRC-32 whose value starts like a 3-byte ID
            El([0xAE], El([0xD7], [0x01]), El([0x83], [0x01])));        // track 1, video
        var cues = El([0x1C, 0x53, 0xBB, 0x6B],
            El([0xBB], El([0xB3], [0x00]), El([0xB7], El([0xF7], [0x01]))),
            El([0xBB], El([0xB3], [0x03, 0xE8]), El([0xB7], El([0xF7], [0x01]))));
        byte[] file = [.. El([0x1A, 0x45, 0xDF, 0xA3]), .. El([0x18, 0x53, 0x80, 0x67], tracks, cues)];

        var times = MatroskaCues.Read(new MemoryStream(file));

        Assert.Equal([0, 1.0], times);
    }

    [Theory]
    [InlineData(new byte[0])]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0xFF })]
    [InlineData(new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x80, 0x18, 0x53, 0x80, 0x67, 0x01, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x16, 0x54, 0xAE, 0x6B, 0x8F })]
    public void NotAnIndexedMkv_IsNull(byte[] data) => Assert.Null(MatroskaCues.Read(new MemoryStream(data)));

    [Fact]
    public void Facts_ReadDolbyVisionAndInterlacing()
    {
        const string json = """
            {"streams":[{"codec_name":"hevc","profile":"Main 10","level":153,"width":3840,"height":2160,"field_order":"progressive","color_transfer":"smpte2084",
              "side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":8,"dv_level":6,"dv_bl_signal_compatibility_id":1}]}]}
            """;

        var facts = RemuxIndex.ParseFacts(json)!;

        Assert.Equal(("hevc", "Main 10", 153, 3840), (facts.Codec, facts.Profile, facts.Level, facts.Width));
        Assert.Equal((8, 6, 1), (facts.DolbyVisionProfile, facts.DolbyVisionLevel, facts.DolbyVisionCompatibility));
        Assert.False(facts.Interlaced);
        Assert.True(RemuxIndex.ParseFacts("""{"streams":[{"codec_name":"h264","field_order":"tt","width":1920,"height":1080}]}""")!.Interlaced);
    }

    [Fact]
    public void FirstPacketIsCountedFromTheFilesStart()
    {
        // 12 Years a Slave's E-AC-3 track: nothing until 21 seconds in.
        Assert.Equal(21.391, RemuxIndex.ParseFirstPacket("pts_time=21.391000\nstart_time=0.000000\n")!.Value, 3);
        Assert.Equal(0.5, RemuxIndex.ParseFirstPacket("pts_time=1.900000\nstart_time=1.400000\n")!.Value, 3);
        Assert.Null(RemuxIndex.ParseFirstPacket("start_time=0.000000\n"));
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
