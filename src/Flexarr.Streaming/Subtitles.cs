using System.Diagnostics;
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

    /// <summary>Embedded subtitle streams plus sidecar files such as "Movie (2020).en.forced.srt".</summary>
    public static IReadOnlyList<SubtitleTrack> For(MediaFile file)
    {
        ArgumentNullException.ThrowIfNull(file);

        var tracks = file.Streams.Where(s => s.Type == StreamType.Subtitle).OrderBy(s => s.Index)
            .Select(s => new SubtitleTrack($"e{s.Index}", s.Index, null, s.Codec, s.Language, s.Title, s.IsDefault, s.IsForced))
            .ToList();

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
            var language = tags.FirstOrDefault(t => LanguageTag().IsMatch(t));
            var forced = tags.Any(t => t.Equals("forced", StringComparison.OrdinalIgnoreCase));
            var sdh = tags.Any(t => t.Equals("sdh", StringComparison.OrdinalIgnoreCase) || t.Equals("cc", StringComparison.OrdinalIgnoreCase));
            tracks.Add(new SubtitleTrack($"x{n++}", null, sidecar, codec, language?.ToLowerInvariant(), sdh ? "SDH" : null, false, forced));
        }

        return tracks;
    }

    /// <summary>Converts a text subtitle track to WebVTT, caching the result. Returns null for picture-based tracks.</summary>
    public static async Task<string?> ToWebVttAsync(MediaFile file, SubtitleTrack track, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        if (!track.IsText)
        {
            return null;
        }

        var source = track.ExternalPath ?? file.Path;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{track.StreamIndex}|{File.GetLastWriteTimeUtc(source).Ticks}")))[..32];
        var target = Path.Combine(settings.CacheDirectory, "subtitles", $"{key}.vtt");
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".tmp";
        var start = new ProcessStartInfo(settings.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true };
        string[] args = track.ExternalPath is null
            ? ["-hide_banner", "-v", "error", "-y", "-i", source, "-map", $"0:{track.StreamIndex}", "-c:s", "webvtt", "-f", "webvtt", partial]
            : ["-hide_banner", "-v", "error", "-y", "-sub_charenc", "UTF-8", "-i", source, "-c:s", "webvtt", "-f", "webvtt", partial];
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var error = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            // Older sidecars are often Windows-1252 rather than UTF-8.
            if (track.ExternalPath is not null && error.Contains("Invalid UTF-8", StringComparison.OrdinalIgnoreCase))
            {
                return await RetryLatin1Async(source, partial, target, settings, ct).ConfigureAwait(false);
            }

            File.Delete(partial);
            return null;
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Prefixes WebVTT with the timestamp mapping Apple's HLS players need to sync with MPEG-TS segments.</summary>
    public static string WithHlsTimestampMap(string vtt)
    {
        ArgumentNullException.ThrowIfNull(vtt);

        // ffmpeg's MPEG-TS muxer starts timestamps 1.4s in (126000 at 90kHz); playlist time 0 maps to that.
        const string map = "X-TIMESTAMP-MAP=MPEGTS:126000,LOCAL:00:00:00.000";
        var newline = vtt.IndexOf('\n', StringComparison.Ordinal);
        return newline < 0 ? $"WEBVTT\n{map}\n" : string.Concat(vtt.AsSpan(0, newline + 1), map, "\n", vtt.AsSpan(newline + 1));
    }

    private static async Task<string?> RetryLatin1Async(string source, string partial, string target, TranscodeSettings settings, CancellationToken ct)
    {
        var start = new ProcessStartInfo(settings.FfmpegPath) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in new[] { "-hide_banner", "-v", "error", "-y", "-sub_charenc", "CP1252", "-i", source, "-c:s", "webvtt", "-f", "webvtt", partial })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            File.Delete(partial);
            return null;
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    [GeneratedRegex(@"^[a-z]{2,3}(?:-[A-Za-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTag();
}
