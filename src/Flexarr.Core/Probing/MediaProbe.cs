using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Flexarr.Core.Media;

namespace Flexarr.Core.Probing;

public sealed record ProbeResult(
    string? Container,
    double? DurationSeconds,
    long? Bitrate,
    VideoRange VideoRange,
    IReadOnlyList<MediaStream> Streams)
{
    /// <summary>Container and first audio stream tags (artist, album, track…), case-insensitive.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The file carries embedded artwork (e.g. album art in an MP3).</summary>
    public bool HasCoverArt { get; init; }
}

public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);
}

public sealed class MediaProbeException(string message) : Exception(message);

/// <summary>Reads container and stream details with <c>ffprobe -show_format -show_streams</c>.</summary>
public sealed class FfprobeMediaProbe(string ffprobePath = "ffprobe") : IMediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -i, so a file named like an option is still read as a file; local files only, whatever the file points at.
        foreach (var arg in new[] { "-v", "error", "-protocol_whitelist", "file", "-print_format", "json", "-show_format", "-show_streams", "-i", path })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new MediaProbeException($"Could not start {ffprobePath}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                throw new MediaProbeException((await stderr.ConfigureAwait(false)).Trim() is { Length: > 0 } error ? error : $"ffprobe exited with {process.ExitCode}");
            }

            return Parse(await stdout.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new MediaProbeException($"ffprobe timed out after {Timeout.TotalSeconds}s");
        }
    }

    internal static ProbeResult Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? container = null;
        double? duration = null;
        long? bitrate = null;
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var coverArt = false;
        if (root.TryGetProperty("format", out var format))
        {
            container = NormalizeContainer(Str(format, "format_name"));
            duration = Double(format, "duration");
            bitrate = Long(format, "bit_rate");
            AddTags(tags, format);
        }

        var streams = new List<MediaStream>();
        var range = VideoRange.Sdr;
        if (root.TryGetProperty("streams", out var streamArray))
        {
            foreach (var s in streamArray.EnumerateArray())
            {
                StreamType? type = Str(s, "codec_type") switch
                {
                    "video" => StreamType.Video,
                    "audio" => StreamType.Audio,
                    "subtitle" => StreamType.Subtitle,
                    _ => null,
                };

                // Cover art is stored as a video stream with the attached_pic disposition.
                if (type == StreamType.Video && Disposition(s, "attached_pic"))
                {
                    coverArt = true;
                    continue;
                }

                if (type is null)
                {
                    continue;
                }

                if (type == StreamType.Audio && !streams.Any(x => x.Type == StreamType.Audio))
                {
                    AddTags(tags, s); // Ogg and FLAC keep their tags on the stream.
                }

                var streamTags = s.TryGetProperty("tags", out var t) ? t : default;
                streams.Add(new MediaStream
                {
                    Index = s.GetProperty("index").GetInt32(),
                    Type = type.Value,
                    Codec = Str(s, "codec_name") ?? "unknown",
                    Profile = Str(s, "profile"),
                    Language = streamTags.ValueKind == JsonValueKind.Object ? Str(streamTags, "language") : null,
                    Title = streamTags.ValueKind == JsonValueKind.Object ? Str(streamTags, "title") : null,
                    IsDefault = Disposition(s, "default"),
                    IsForced = Disposition(s, "forced"),
                    Width = Int(s, "width"),
                    Height = Int(s, "height"),
                    Channels = Int(s, "channels"),
                });

                if (type == StreamType.Video && streams.Count(x => x.Type == StreamType.Video) == 1)
                {
                    range = DetectRange(s);
                }
            }
        }

        return new ProbeResult(container, duration, bitrate, range, streams) { Tags = tags, HasCoverArt = coverArt };
    }

    private static void AddTags(Dictionary<string, string> into, JsonElement element)
    {
        if (!element.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var tag in tags.EnumerateObject())
        {
            if (tag.Value.ValueKind == JsonValueKind.String && tag.Value.GetString() is { Length: > 0 } value)
            {
                into.TryAdd(tag.Name, value.Trim());
            }
        }
    }

    /// <summary>ffprobe reports demuxer families ("mov,mp4,m4a,…", "matroska,webm"); clients want file formats.</summary>
    private static string? NormalizeContainer(string? formatName) => formatName?.Split(',') switch
    {
        null => null,
        var names when names.Contains("mp4") => "mp4",
        var names when names.Contains("matroska") => "mkv",
        var names when names.Contains("ogg") => "ogg",
        var names => names[0],
    };

    private static VideoRange DetectRange(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sideData)
            && sideData.EnumerateArray().Any(d => Str(d, "side_data_type")?.Contains("DOVI", StringComparison.OrdinalIgnoreCase) == true))
        {
            return VideoRange.DolbyVision;
        }

        return Str(stream, "color_transfer") switch
        {
            "smpte2084" => VideoRange.Hdr10,
            "arib-std-b67" => VideoRange.Hlg,
            _ => VideoRange.Sdr,
        };
    }

    private static bool Disposition(JsonElement stream, string name) =>
        stream.TryGetProperty("disposition", out var d) && d.TryGetProperty(name, out var v) && v.GetInt32() == 1;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;

    private static double? Double(JsonElement e, string name) =>
        double.TryParse(Str(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static long? Long(JsonElement e, string name) =>
        long.TryParse(Str(e, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
}
