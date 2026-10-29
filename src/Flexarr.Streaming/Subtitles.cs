using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>Finds subtitle tracks for a file and converts them to WebVTT for players.</summary>
public static partial class Subtitles
{
    private static readonly Dictionary<string, string> SidecarCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        [".srt"] = "subrip",
        [".vtt"] = "webvtt",
        [".ass"] = "ass",
        [".ssa"] = "ssa",
    };

    /// <summary>
    /// Embedded subtitle streams, closed captions in the video, and sidecar files such as "Movie (2020).en.forced.srt".
    /// <paramref name="teletextReadable"/>: this server's FFmpeg can convert teletext.
    /// </summary>
    public static IReadOnlyList<SubtitleTrack> For(MediaFile file, bool teletextReadable = false)
    {
        ArgumentNullException.ThrowIfNull(file);

        var tracks = file.Streams.Where(s => s.Type == StreamType.Subtitle).OrderBy(s => s.Index)
            .Select(s => new SubtitleTrack($"e{s.Index}", s.Index, null, s.Codec, s.Language, s.Title, s.IsDefault, s.IsForced) { TeletextReadable = teletextReadable })
            .ToList();

        // Captions in the video's frames come with the picture: the language is the programme's, so the first audio's.
        if (file.ClosedCaptions == true && file.Streams.FirstOrDefault(s => s.Type == StreamType.Video) is { } video)
        {
            var language = file.Streams.Where(s => s.Type == StreamType.Audio).OrderBy(s => s.Index).FirstOrDefault()?.Language;
            tracks.Add(new SubtitleTrack($"cc{video.Index}", video.Index, null, "eia_608", language, "CC", false, false) { InVideo = true });
        }

        var directory = Path.GetDirectoryName(file.Path);
        var stem = Path.GetFileNameWithoutExtension(file.Path);
        if (directory is null || !Directory.Exists(directory))
        {
            return tracks;
        }

        var n = 0;
        foreach (var sidecar in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(sidecar);
            if (!SidecarCodecs.TryGetValue(Path.GetExtension(sidecar), out var codec) || !name.StartsWith(stem, StringComparison.Ordinal))
            {
                continue;
            }

            // Everything between the video's name and the extension: ".en", ".eng.forced", ".en.sdh".
            var tags = name[stem.Length..^Path.GetExtension(name).Length].Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var at = Array.FindIndex(tags, t => LanguageTag().IsMatch(t));
            var language = at < 0 ? null : tags[at];
            var forced = tags.Any(t => t.Equals("forced", StringComparison.OrdinalIgnoreCase));
            // Bazarr writes "Movie.en.hi.srt"; "hi" alone is Hindi, so it only means hearing-impaired after the language.
            var sdh = tags.Any(t => t.Equals("sdh", StringComparison.OrdinalIgnoreCase) || t.Equals("cc", StringComparison.OrdinalIgnoreCase))
                || (at >= 0 && tags.Skip(at + 1).Any(t => t.Equals("hi", StringComparison.OrdinalIgnoreCase)));
            tracks.Add(new SubtitleTrack($"x{n++}", null, sidecar, codec, language?.ToLowerInvariant(), sdh ? "SDH" : null, false, forced));
        }

        return tracks;
    }

    /// <summary>
    /// Where a track's WebVTT (or, with <paramref name="extension"/> ".ass", its styled original) is cached, keyed by
    /// source, stream and modification time.
    /// </summary>
    public static string CachePath(MediaFile file, SubtitleTrack track, TranscodeSettings settings, string extension = ".vtt")
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        var source = track.ExternalPath ?? file.Path;
        var stream = track.InVideo ? $"cc{track.StreamIndex}" : $"{track.StreamIndex}";
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{stream}|{File.GetLastWriteTimeUtc(source).Ticks}")))[..32];
        return Path.Combine(settings.CacheDirectory, "subtitles", key + extension);
    }

    /// <summary>
    /// Converts the given text subtitle tracks of one file to WebVTT. Embedded tracks are extracted together in a
    /// single pass, because getting even one means reading the whole file (minutes for a large film on a slow drive).
    /// Returns the cached paths of the tracks that converted.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> ToWebVttAsync(MediaFile file, IReadOnlyList<SubtitleTrack> tracks, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tracks);

        var done = new Dictionary<string, string>(StringComparer.Ordinal);
        var embedded = new List<(SubtitleTrack Track, string Target)>();
        var teletext = new List<(SubtitleTrack Track, string Target)>();
        var captions = new List<(SubtitleTrack Track, string Target)>();
        foreach (var track in tracks.Where(t => t.IsText))
        {
            var target = CachePath(file, track, settings);
            if (File.Exists(target) && (!track.IsStyled || track.ExternalPath is not null || File.Exists(CachePath(file, track, settings, ".ass"))))
            {
                done[track.Key] = target;
            }
            else if (track.ExternalPath is not null)
            {
                if (await ConvertSidecarAsync(track.ExternalPath, target, settings, ct).ConfigureAwait(false))
                {
                    done[track.Key] = target;
                }
            }
            else
            {
                (track.InVideo ? captions : track.IsTeletext ? teletext : embedded).Add((track, target));
            }
        }

        if (embedded.Count > 0)
        {
            // Styled tracks are also copied as they are, for players that draw them as authored: same pass, no extra read.
            var outputs = embedded.Select(e => (e.Track, Format: "webvtt", e.Target))
                .Concat(embedded.Where(e => e.Track.IsStyled).Select(e => (e.Track, Format: "ass", Target: CachePath(file, e.Track, settings, ".ass"))))
                .ToList();
            await ExtractAsync([], file.Path, outputs, done, settings, ct).ConfigureAwait(false);
        }

        if (teletext.Count > 0)
        {
            // Its own pass, with the teletext decoder's options: subtitle pages, as text rather than pictures of the page.
            await ExtractAsync(TeletextOptions(), file.Path, teletext.Select(e => (e.Track, Format: "webvtt", e.Target)).ToList(), done, settings, ct).ConfigureAwait(false);
        }

        foreach (var (track, target) in captions)
        {
            if (await ExtractCaptionsAsync(file.Path, target, settings, ct).ConfigureAwait(false))
            {
                done[track.Key] = target;
            }
        }

        return done;
    }

    /// <summary>A teletext page number: three digits, the first (the magazine) 1 to 8.</summary>
    public static bool IsTeletextPage(int page) => page is >= 100 and <= 899;

    /// <summary>
    /// The teletext decoder's options: <paramref name="page"/>, or with none the pages the broadcast marks as subtitles;
    /// as text rather than pictures of the page.
    /// </summary>
    internal static string[] TeletextOptions(int? page = null) =>
        ["-txt_format", "text", "-txt_page", page?.ToString(CultureInfo.InvariantCulture) ?? "subtitle"];

    /// <summary>
    /// A teletext track's WebVTT from one <paramref name="page"/>, for a broadcast whose subtitles aren't on the page it
    /// marks as subtitles. Cached apart from the track's usual WebVTT; null when it didn't convert.
    /// </summary>
    public static async Task<string?> TeletextPageToWebVttAsync(MediaFile file, SubtitleTrack track, int page, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        if (!track.IsTeletext || !IsTeletextPage(page))
        {
            throw new ArgumentException("Only a teletext track has pages, numbered 100 to 899.", nameof(page));
        }

        var target = CachePath(file, track, settings, $".p{page}.vtt");
        if (File.Exists(target))
        {
            return target;
        }

        var done = new Dictionary<string, string>(StringComparer.Ordinal);
        await ExtractAsync(TeletextOptions(page), file.Path, [(track, "webvtt", target)], done, settings, ct).ConfigureAwait(false);
        return done.GetValueOrDefault(track.Key);
    }

    /// <summary>One read of the file, writing each of <paramref name="outputs"/>; the WebVTT ones that land go in <paramref name="done"/>.</summary>
    private static async Task ExtractAsync(string[] inputOptions, string path, List<(SubtitleTrack Track, string Format, string Target)> outputs,
        Dictionary<string, string> done, TranscodeSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputs[0].Target)!);
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file", .. inputOptions, "-i", path, "-vn", "-an", "-dn"];
        foreach (var (track, format, target) in outputs)
        {
            args.AddRange(["-map", $"0:{track.StreamIndex}", "-c:s", format == "ass" ? "copy" : "webvtt", "-f", format, target + ".tmp"]);
        }

        var ok = await RunAsync(settings.FfmpegPath, args, ct).ConfigureAwait(false);
        foreach (var (track, format, target) in outputs)
        {
            var partial = target + ".tmp";
            if (ok && File.Exists(partial))
            {
                File.Move(partial, target, overwrite: true);
                if (format == "webvtt")
                {
                    done[track.Key] = target;
                }
            }
            else if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    /// <summary>
    /// Closed captions in the video's frames: FFmpeg only gets at them by decoding the video, through the movie filter
    /// (its subcc output). The filter takes a file name inside a filter graph, where quotes, colons and brackets in a
    /// real path would need escaping twice over, so it reads a link with a plain name, from the link's own folder.
    /// </summary>
    private static async Task<bool> ExtractCaptionsAsync(string path, string target, TranscodeSettings settings, CancellationToken ct)
    {
        var folder = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(folder);
        var link = $"cc-{Guid.NewGuid():N}{Path.GetExtension(path).ToLowerInvariant() switch { var e when e.Length is > 1 and <= 5 && e[1..].All(char.IsAsciiLetterOrDigit) => e, _ => "" }}";
        var partial = target + ".tmp";
        try
        {
            File.CreateSymbolicLink(Path.Combine(folder, link), path);
            var ok = await RunAsync(settings.FfmpegPath, ["-hide_banner", "-v", "error", "-nostdin", "-y", "-f", "lavfi", "-i", $"movie={link}[out0+subcc]",
                "-map", "0:1", "-c:s", "webvtt", "-f", "webvtt", partial], ct, folder).ConfigureAwait(false);
            if (ok && File.Exists(partial))
            {
                File.Move(partial, target, overwrite: true);
                return true;
            }

            return false;
        }
        finally
        {
            File.Delete(Path.Combine(folder, link));
            File.Delete(partial);
        }
    }

    /// <summary>
    /// A styled (ASS/SSA) track as authored, as UTF-8 text: an embedded one from the copy made alongside its WebVTT, a
    /// sidecar read as it is (Windows-1252 when it isn't UTF-8). Null when it hasn't been extracted.
    /// </summary>
    public static async Task<string?> StyledAsync(MediaFile file, SubtitleTrack track, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(track);
        var path = track.ExternalPath ?? CachePath(file, track, settings, ".ass");
        if (!track.IsStyled || !File.Exists(path))
        {
            return null;
        }

        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
        }
        catch (DecoderFallbackException)
        {
            return (CodePagesEncodingProvider.Instance.GetEncoding(1252) ?? Encoding.Latin1).GetString(bytes);
        }
    }

    private static async Task<bool> ConvertSidecarAsync(string source, string target, TranscodeSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".tmp";

        // Older sidecars are often Windows-1252 rather than UTF-8: try UTF-8 first, then fall back.
        foreach (var encoding in new[] { "UTF-8", "CP1252" })
        {
            // Read as the subtitle format its name says, so a sidecar can't pass itself off as a playlist of other files.
            if (await RunAsync(settings.FfmpegPath, ["-hide_banner", "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file", .. SidecarFormat(source),
                    "-sub_charenc", encoding, "-i", source, "-c:s", "webvtt", "-f", "webvtt", partial], ct).ConfigureAwait(false))
            {
                File.Move(partial, target, overwrite: true);
                return true;
            }
        }

        File.Delete(partial);
        return false;
    }

    /// <summary>ffmpeg's demuxer for a sidecar (its ass demuxer reads .ssa too).</summary>
    internal static string[] SidecarFormat(string path) => SidecarCodecs.GetValueOrDefault(Path.GetExtension(path)) switch
    {
        "subrip" => ["-f", "srt"],
        "webvtt" => ["-f", "webvtt"],
        "ass" or "ssa" => ["-f", "ass"],
        _ => [],
    };

    private static async Task<bool> RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true, WorkingDirectory = workingDirectory ?? "" };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        try
        {
            await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessSignals.KillTree(process);
            throw;
        }

        return process.ExitCode == 0;
    }

    /// <summary>Prefixes WebVTT with the timestamp mapping Apple's HLS players need to sync with MPEG-TS segments.</summary>
    /// <summary>Moves every cue <paramref name="seconds"/> later (earlier when negative). Cues pushed before the start go.</summary>
    public static string Shift(string vtt, double seconds)
    {
        ArgumentNullException.ThrowIfNull(vtt);
        if (seconds == 0)
        {
            return vtt;
        }

        var builder = new StringBuilder(vtt.Length);
        var blocks = vtt.Replace("\r\n", "\n", StringComparison.Ordinal).Split("\n\n");
        for (var i = 0; i < blocks.Length; i++)
        {
            var block = blocks[i];
            var timing = CueTiming().Match(block);
            if (timing.Success)
            {
                var start = ParseTime(timing.Groups["start"].Value) + seconds;
                var end = ParseTime(timing.Groups["end"].Value) + seconds;
                if (end <= 0)
                {
                    continue;
                }

                block = block[..timing.Index] + FormatTime(Math.Max(0, start)) + " --> " + FormatTime(end) + block[(timing.Index + timing.Length)..];
            }

            if (builder.Length > 0)
            {
                builder.Append("\n\n");
            }

            builder.Append(block);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Sets every cue's line to the profile's <paramref name="position"/>, for players that draw WebVTT as the cues say:
    /// the foot of the cue raised to 82% or 72% of the picture's height, or its top at 8%. At the bottom, cues are kept.
    /// </summary>
    public static string Placed(string vtt, SubtitlePosition position)
    {
        ArgumentNullException.ThrowIfNull(vtt);
        var line = position switch
        {
            SubtitlePosition.Raised => "line:82%,end",
            SubtitlePosition.Higher => "line:72%,end",
            SubtitlePosition.Top => "line:8%,start",
            _ => null,
        };
        if (line is null)
        {
            return vtt;
        }

        return CueLine().Replace(vtt.Replace("\r\n", "\n", StringComparison.Ordinal), m =>
        {
            // The cue's own settings stay (alignment, size), except a line of its own.
            var settings = string.Join(' ', m.Groups["settings"].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(x => !x.StartsWith("line:", StringComparison.Ordinal)).Append(line));
            return $"{m.Groups["timing"].Value} {settings}";
        });
    }

    [GeneratedRegex(@"^(?<timing>(?:\d+:)?\d{2}:\d{2}\.\d{3}\s+-->\s+(?:\d+:)?\d{2}:\d{2}\.\d{3})(?<settings>[^\n]*)$", RegexOptions.Multiline)]
    private static partial Regex CueLine();

    private static double ParseTime(string value)
    {
        var parts = value.Split(':');
        var seconds = double.Parse(parts[^1], CultureInfo.InvariantCulture);
        var minutes = int.Parse(parts[^2], CultureInfo.InvariantCulture);
        var hours = parts.Length > 2 ? int.Parse(parts[0], CultureInfo.InvariantCulture) : 0;
        return (hours * 3600) + (minutes * 60) + seconds;
    }

    private static string FormatTime(double seconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Round(seconds * 1000));
        return string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}");
    }

    [GeneratedRegex(@"(?<start>(?:\d+:)?\d{2}:\d{2}\.\d{3})\s+-->\s+(?<end>(?:\d+:)?\d{2}:\d{2}\.\d{3})")]
    private static partial Regex CueTiming();

    public static string WithHlsTimestampMap(string vtt, bool fmp4 = false)
    {
        ArgumentNullException.ThrowIfNull(vtt);

        // ffmpeg's MPEG-TS muxer starts timestamps 1.4s in (126000 at 90kHz); playlist time 0 maps to that. Fragmented
        // MP4 segments carry the film's own time from zero.
        var map = fmp4 ? "X-TIMESTAMP-MAP=MPEGTS:0,LOCAL:00:00:00.000" : "X-TIMESTAMP-MAP=MPEGTS:126000,LOCAL:00:00:00.000";
        var newline = vtt.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? $"WEBVTT\n{map}\n" : string.Concat(vtt.AsSpan(0, newline + 1), map, "\n", vtt.AsSpan(newline + 1));
    }

    [GeneratedRegex(@"^[a-z]{2,3}(?:-[A-Za-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTag();
}
