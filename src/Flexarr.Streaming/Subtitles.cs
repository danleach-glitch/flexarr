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

    /// <summary>Where a track's WebVTT is cached (keyed by source, stream and modification time).</summary>
    public static string CachePath(MediaFile file, SubtitleTrack track, TranscodeSettings settings)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(settings);

        var source = track.ExternalPath ?? file.Path;
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{track.StreamIndex}|{File.GetLastWriteTimeUtc(source).Ticks}")))[..32];
        return Path.Combine(settings.CacheDirectory, "subtitles", $"{key}.vtt");
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
        foreach (var track in tracks.Where(t => t.IsText))
        {
            var target = CachePath(file, track, settings);
            if (File.Exists(target))
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
                embedded.Add((track, target));
            }
        }

        if (embedded.Count == 0)
        {
            return done;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(embedded[0].Target)!);
        var args = new List<string> { "-hide_banner", "-v", "error", "-nostdin", "-y", "-i", file.Path, "-vn", "-an", "-dn" };
        foreach (var (track, target) in embedded)
        {
            args.AddRange(["-map", $"0:{track.StreamIndex}", "-c:s", "webvtt", "-f", "webvtt", target + ".tmp"]);
        }

        var ok = await RunAsync(settings.FfmpegPath, args, ct).ConfigureAwait(false);
        foreach (var (track, target) in embedded)
        {
            var partial = target + ".tmp";
            if (ok && File.Exists(partial))
            {
                File.Move(partial, target, overwrite: true);
                done[track.Key] = target;
            }
            else if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }

        return done;
    }

    private static async Task<bool> ConvertSidecarAsync(string source, string target, TranscodeSettings settings, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".tmp";

        // Older sidecars are often Windows-1252 rather than UTF-8: try UTF-8 first, then fall back.
        foreach (var encoding in new[] { "UTF-8", "CP1252" })
        {
            if (await RunAsync(settings.FfmpegPath, ["-hide_banner", "-v", "error", "-nostdin", "-y", "-sub_charenc", encoding, "-i", source, "-c:s", "webvtt", "-f", "webvtt", partial], ct).ConfigureAwait(false))
            {
                File.Move(partial, target, overwrite: true);
                return true;
            }
        }

        File.Delete(partial);
        return false;
    }

    private static async Task<bool> RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await process.WaitForExitAsync(ct).ConfigureAwait(false);
        return process.ExitCode == 0;
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

    [GeneratedRegex(@"^[a-z]{2,3}(?:-[A-Za-z]{2,4})?$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageTag();
}
