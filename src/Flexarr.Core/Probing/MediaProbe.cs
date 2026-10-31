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

    /// <summary>The video carries HDR10+ scene-by-scene metadata (SMPTE ST 2094-40) alongside its HDR10.</summary>
    public bool Hdr10Plus { get; init; }

    /// <summary>The video carries closed captions (CEA-608/708) in its frames; null when the video can't carry them.</summary>
    public bool? ClosedCaptions { get; init; }
}

/// <summary>A chapter inside one file, on that file's own timeline.</summary>
public sealed record ProbedChapter(double StartSeconds, double EndSeconds, string? Title);

/// <summary>What a book's file says about the book: its length, tags, chapters and whether it carries a cover.</summary>
public sealed record BookFileProbe(double? DurationSeconds, IReadOnlyDictionary<string, string> Tags, IReadOnlyList<ProbedChapter> Chapters, bool HasCoverArt);

public interface IMediaProbe
{
    Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken);

    /// <summary>
    /// Reads an audiobook file's tags and chapters. Only books need chapters, so only they pay for reading them. Probes
    /// that can't read chapters give none.
    /// </summary>
    async Task<BookFileProbe> ProbeBookFileAsync(string path, CancellationToken cancellationToken)
    {
        var result = await ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        return new BookFileProbe(result.DurationSeconds, result.Tags, [], result.HasCoverArt);
    }
}

public sealed class MediaProbeException(string message) : Exception(message);

/// <summary>
/// Reads container and stream details with <c>ffprobe -show_format -show_streams</c>, and HDR10+ and closed captions
/// from the first frames.
/// </summary>
public sealed class FfprobeMediaProbe(string ffprobePath = "ffprobe") : IMediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);
    private static readonly string[] Output = ["-v", "error", "-protocol_whitelist", "file", "-print_format", "json"];
    private static readonly string[] Streams = ["-show_format", "-show_streams"];
    private static readonly string[] BookFile = ["-show_format", "-show_streams", "-show_chapters"];
    private static readonly string[] FirstFrames = ["-select_streams", "v:0", "-read_intervals", "%+#5", "-show_frames"];

    public async Task<ProbeResult> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = Parse(await RunAsync(Streams, path, cancellationToken).ConfigureAwait(false));
        var hdr = result.VideoRange is VideoRange.Hdr10 or VideoRange.DolbyVision;
        var captions = CanCarryCaptions(result.Streams.FirstOrDefault(s => s.Type == StreamType.Video)?.Codec);
        if (!hdr && !captions)
        {
            return result;
        }

        // HDR10+ and closed captions aren't in the stream details, only in the frames themselves: the first few are
        // enough (broadcasts send caption data with every frame, empty or not).
        try
        {
            var frames = await RunAsync(FirstFrames, path, cancellationToken).ConfigureAwait(false);
            return result with
            {
                Hdr10Plus = hdr && HasSideData(frames, "2094-40"),
                ClosedCaptions = captions ? HasSideData(frames, "A53") : null,
            };
        }
        catch (MediaProbeException)
        {
            // Still plays as HDR10; the label is all that's missing. Captions are taken as absent, not checked again.
            return result with { ClosedCaptions = captions ? false : null };
        }
    }

    public async Task<BookFileProbe> ProbeBookFileAsync(string path, CancellationToken cancellationToken)
    {
        var json = await RunAsync(BookFile, path, cancellationToken).ConfigureAwait(false);
        var result = Parse(json);
        return new BookFileProbe(result.DurationSeconds, result.Tags, ParseChapters(json), result.HasCoverArt);
    }

    /// <summary>The <c>chapters</c> of <c>-show_chapters</c> output, in order, with blank titles as null.</summary>
    internal static IReadOnlyList<ProbedChapter> ParseChapters(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("chapters", out var chapters) || chapters.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<ProbedChapter>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            if (Seconds(chapter, "start_time") is not { } start || Seconds(chapter, "end_time") is not { } end || end <= start)
            {
                continue;
            }

            string? title = null;
            if (chapter.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Object)
            {
                foreach (var tag in tags.EnumerateObject())
                {
                    if (tag.Name.Equals("title", StringComparison.OrdinalIgnoreCase) && tag.Value.GetString() is { } t && !string.IsNullOrWhiteSpace(t))
                    {
                        title = t.Trim();
                    }
                }
            }

            list.Add(new ProbedChapter(start, end, title));
        }

        return [.. list.OrderBy(c => c.StartSeconds)];

        static double? Seconds(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ? seconds : null;
    }

    /// <summary>The video codecs whose frames carry broadcast closed captions (ATSC A/53).</summary>
    public static bool CanCarryCaptions(string? videoCodec) => videoCodec is "h264" or "hevc" or "mpeg2video";

    private async Task<string> RunAsync(string[] show, string path, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(ffprobePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // -i, so a file named like an option is still read as a file; local files only, whatever the file points at.
        foreach (var arg in Output.Concat(show).Append("-i").Append(path))
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

            return await stdout.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new MediaProbeException($"ffprobe timed out after {Timeout.TotalSeconds}s");
        }
    }

    /// <summary>
    /// Reads <c>-show_frames</c> output for side data whose name contains <paramref name="name"/>: ffprobe names them
    /// "HDR Dynamic Metadata SMPTE2094-40 (HDR10+)" and "ATSC A53 Part 4 Closed Captions". Not <c>-show_entries</c>,
    /// whose names for side data differ between FFmpeg versions and drop its fields when wrong.
    /// </summary>
    internal static bool HasSideData(string json, string name)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("frames", out var frames)
            && frames.EnumerateArray().Any(f => f.TryGetProperty("side_data_list", out var side)
                && side.EnumerateArray().Any(d => Str(d, "side_data_type")?.Contains(name, StringComparison.Ordinal) == true));
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
