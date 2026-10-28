using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming.Remux;

/// <summary>What repackaging a video needs to know about it, beyond what the library scan records.</summary>
/// <param name="Level">ffprobe's level: 40 for H.264 4.0, 150 for HEVC 5.0 (×30).</param>
/// <param name="DolbyVisionProfile">5, 7, 8…; null without Dolby Vision.</param>
/// <param name="DolbyVisionCompatibility">The base layer's: 1 HDR10, 2 SDR, 4 HLG, 0 none (profile 5).</param>
/// <param name="StartTime">When the video's first frame is, in the file's own time (ffmpeg counts from here).</param>
/// <param name="HevcConfig">HEVC only: the first 13 bytes of the file's hvcC record as hex (profile, tier, compatibility
/// flags, constraint flags, level), which the HLS CODECS attribute must agree with. Null for H.264 and for Annex-B files.</param>
public sealed record VideoFacts(
    string Codec,
    string? Profile,
    int? Level,
    int Width,
    int Height,
    bool Interlaced,
    string? ColorTransfer,
    int? DolbyVisionProfile = null,
    int? DolbyVisionLevel = null,
    int? DolbyVisionCompatibility = null,
    double StartTime = 0,
    string? HevcConfig = null,
    double? FrameRate = null);

/// <summary>A video's keyframe times (where a copied stream can be cut) and its facts.</summary>
public sealed record RemuxInfo(IReadOnlyList<double> Keyframes, VideoFacts Video);

/// <summary>
/// Finds and remembers each file's <see cref="RemuxInfo"/>. MKVs index their keyframes, which is read in milliseconds;
/// other files need every packet scanned, which is done in the background (one at a time) for next time.
/// </summary>
public sealed partial class RemuxIndex(TranscodeSettings settings, string ffprobePath, ILogger<RemuxIndex> logger) : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, RemuxInfo> _known = new();
    private readonly ConcurrentDictionary<string, bool> _scanning = new();
    private readonly SemaphoreSlim _scan = new(1, 1);

    public void Dispose() => _scan.Dispose();

    /// <summary>The file's info, or null when it isn't known yet (a scan is then started for next time).</summary>
    public async Task<RemuxInfo?> GetAsync(string path, CancellationToken ct)
    {
        var key = Key(path);
        if (key is null)
        {
            return null;
        }

        if (_known.TryGetValue(key, out var known) || TryLoad(key) is { } loaded && _known.TryAdd(key, known = loaded))
        {
            // Indexed before the hvcC record was kept: probe the facts again (the keyframes still stand).
            if ((known.Video.FrameRate is null || known.Video.Codec == "hevc" && known.Video.HevcConfig is null)
                && await ProbeAsync(path, ct).ConfigureAwait(false) is { } refreshed)
            {
                known = Remember(key, known with { Video = refreshed });
            }

            return known;
        }

        var keyframes = await Task.Run(() => MatroskaCues.Read(path), ct).ConfigureAwait(false);
        if (keyframes is null)
        {
            ScanLater(path, key);
            return null;
        }

        var facts = await ProbeAsync(path, ct).ConfigureAwait(false);
        return facts is null ? null : Remember(key, new RemuxInfo(keyframes, facts));
    }

    private void ScanLater(string path, string key)
    {
        if (!_scanning.TryAdd(key, true))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            await _scan.WaitAsync().ConfigureAwait(false);
            try
            {
                var started = Stopwatch.StartNew();
                if (await ScanKeyframesAsync(path).ConfigureAwait(false) is { Count: > 1 } keyframes
                    && await ProbeAsync(path, CancellationToken.None).ConfigureAwait(false) is { } facts)
                {
                    Remember(key, new RemuxInfo(keyframes, facts));
                    LogScanned(Path.GetFileName(path), keyframes.Count, started.Elapsed.TotalSeconds);
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                LogScanFailed(Path.GetFileName(path), ex.Message);
            }
            finally
            {
                _scan.Release();
                _scanning.TryRemove(key, out _);
            }
        });
    }

    private RemuxInfo Remember(string key, RemuxInfo info)
    {
        _known[key] = info;
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            File.WriteAllText(Path.Combine(CacheDirectory, key + ".json"), JsonSerializer.Serialize(info, Json));
        }
        catch (IOException)
        {
            // Kept in memory regardless; the disk copy only saves reading the index again after a restart.
        }

        return info;
    }

    private RemuxInfo? TryLoad(string key)
    {
        try
        {
            var file = Path.Combine(CacheDirectory, key + ".json");
            return File.Exists(file) ? JsonSerializer.Deserialize<RemuxInfo>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }
    }

    private string CacheDirectory => Path.Combine(settings.CacheDirectory, "remux-index");

    /// <summary>The file as it is now: a changed or replaced file gets a new key.</summary>
    private static string? Key(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists)
        {
            return null;
        }

        var text = string.Create(CultureInfo.InvariantCulture, $"{path}|{file.Length}|{file.LastWriteTimeUtc.Ticks}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    }

    private async Task<VideoFacts?> ProbeAsync(string path, CancellationToken ct)
    {
        var output = await RunAsync(["-v", "error", .. FfmpegCommand.LocalFile, "-select_streams", "v:0", "-show_streams", "-show_data", "-of", "json", path], ct).ConfigureAwait(false);
        return output is null ? null : ParseFacts(output);
    }

    /// <summary>Reads ffprobe's JSON for the first video stream.</summary>
    public static VideoFacts? ParseFacts(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return null;
        }

        var stream = streams[0];
        int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
        string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        int? dvProfile = null, dvLevel = null, dvCompat = null;
        if (stream.TryGetProperty("side_data_list", out var sideData))
        {
            foreach (var side in sideData.EnumerateArray())
            {
                if (Str(side, "side_data_type")?.Contains("DOVI", StringComparison.OrdinalIgnoreCase) == true)
                {
                    (dvProfile, dvLevel, dvCompat) = (Int(side, "dv_profile"), Int(side, "dv_level"), Int(side, "dv_bl_signal_compatibility_id"));
                }
            }
        }

        var fieldOrder = Str(stream, "field_order");
        var codec = Str(stream, "codec_name") ?? "";
        return new VideoFacts(
            Str(stream, "codec_name") ?? "",
            Str(stream, "profile"),
            Int(stream, "level"),
            Int(stream, "width") ?? 0,
            Int(stream, "height") ?? 0,
            Interlaced: fieldOrder is not (null or "progressive" or "unknown"),
            Str(stream, "color_transfer"),
            dvProfile,
            dvLevel,
            dvCompat,
            double.TryParse(Str(stream, "start_time"), NumberStyles.Float, CultureInfo.InvariantCulture, out var startTime) ? startTime : 0,
            codec == "hevc" ? HevcConfigFrom(Str(stream, "extradata")) : null,
            FrameRateFrom(Str(stream, "avg_frame_rate")) ?? FrameRateFrom(Str(stream, "r_frame_rate")));
    }

    /// <summary>ffprobe's "24000/1001" as 23.976; null for "0/0" and the like.</summary>
    public static double? FrameRateFrom(string? ratio)
    {
        if (ratio is null)
        {
            return null;
        }

        var parts = ratio.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 && n > 0)
        {
            return n / d;
        }

        return double.TryParse(ratio, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : null;
    }

    /// <summary>
    /// The hvcC header out of ffprobe's extradata dump ("00000000: 0102 2000 0000 9000 ..." lines): its first 13 bytes
    /// as hex, or null when the extradata isn't an hvcC record (Annex-B start codes, say).
    /// </summary>
    public static string? HevcConfigFrom(string? extradataDump)
    {
        if (string.IsNullOrEmpty(extradataDump))
        {
            return null;
        }

        var bytes = new List<byte>();
        foreach (var line in extradataDump.Split('\n'))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 0)
            {
                continue;
            }

            // "0102 2000 0000 9000 0000 0000 99f0 00fc  .. ............." : hex pairs, then the ASCII column.
            var hex = line[(colon + 1)..];
            var ascii = hex.IndexOf("  ", StringComparison.Ordinal);
            if (ascii >= 0)
            {
                hex = hex[..ascii];
            }

            foreach (var group in hex.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                for (var i = 0; i + 1 < group.Length; i += 2)
                {
                    if (byte.TryParse(group.AsSpan(i, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b))
                    {
                        bytes.Add(b);
                    }
                }
            }

            if (bytes.Count >= 13)
            {
                break;
            }
        }

        return bytes.Count >= 13 && bytes[0] == 1 ? Convert.ToHexStringLower(bytes.Take(13).ToArray()) : null;
    }

    /// <summary>Every video packet's flags, for files without an index: the keyframes' times.</summary>
    private async Task<List<double>?> ScanKeyframesAsync(string path)
    {
        var output = await RunAsync(["-v", "error", .. FfmpegCommand.LocalFile, "-select_streams", "v:0", "-show_entries", "packet=pts_time,flags", "-of", "csv=p=0", path],
            CancellationToken.None, lowPriority: true).ConfigureAwait(false);
        if (output is null)
        {
            return null;
        }

        var times = new SortedSet<double>();
        foreach (var line in output.Split('\n'))
        {
            var comma = line.IndexOf(',', StringComparison.Ordinal);
            if (comma > 0 && line.AsSpan(comma + 1).Contains('K')
                && double.TryParse(line.AsSpan(0, comma), NumberStyles.Float, CultureInfo.InvariantCulture, out var time))
            {
                times.Add(Math.Round(time, 6));
            }
        }

        return [.. times];
    }

    private async Task<string?> RunAsync(IReadOnlyList<string> args, CancellationToken ct, bool lowPriority = false)
    {
        var start = new ProcessStartInfo(ffprobePath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException("ffprobe didn't start");
        if (lowPriority)
        {
            try
            {
                process.PriorityClass = ProcessPriorityClass.BelowNormal;
            }
            catch (InvalidOperationException)
            {
                // Already finished.
            }
        }

        var output = process.StandardOutput.ReadToEndAsync(ct);
        _ = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Indexed keyframes of {File}: {Count} in {Seconds:0.0}s")]
    private partial void LogScanned(string file, int count, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't index keyframes of {File}: {Reason}")]
    private partial void LogScanFailed(string file, string reason);
}
