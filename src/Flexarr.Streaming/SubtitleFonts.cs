using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>A font attached to a video (as MKVs carry them for their styled subtitles): its stream and file name.</summary>
public sealed record AttachedFont(int StreamIndex, string Name, string ContentType);

/// <summary>
/// The fonts attached to a file, which styled (ASS/SSA) subtitles are drawn with. Anime releases attach the exact
/// fonts their signs and songs use; without them a player falls back to a plain one.
/// </summary>
public static class SubtitleFonts
{
    private static readonly Dictionary<string, string> FontTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".ttf"] = "font/ttf",
        [".ttc"] = "font/collection",
        [".otf"] = "font/otf",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
    };

    /// <summary>Files' fonts as last read, by path and time: a player asks for each font of a file at once, and each needs the list.</summary>
    private static readonly ConcurrentDictionary<string, Task<IReadOnlyList<AttachedFont>?>> Known = new(StringComparer.Ordinal);

    private const int MostKnown = 256;

    /// <summary>
    /// The attachments that are fonts, read from the file's header with ffprobe once for the file as it is now (a file
    /// that changes is read again), however many of its fonts are asked for together.
    /// </summary>
    public static async Task<IReadOnlyList<AttachedFont>> ListAsync(MediaFile file, string ffprobePath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        var key = $"{file.Path}|{File.GetLastWriteTimeUtc(file.Path).Ticks}";
        if (Known.Count > MostKnown)
        {
            Known.Clear();
        }

        // Read without the caller's token, so one player leaving doesn't fail the others waiting on the same read.
        var reading = Known.GetOrAdd(key, _ => ReadAsync(file, ffprobePath));
        try
        {
            var fonts = await reading.WaitAsync(ct).ConfigureAwait(false);
            if (fonts is null)
            {
                // ffprobe failed: not remembered, so the next ask tries again.
                Known.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<AttachedFont>?>>(key, reading));
            }

            return fonts ?? [];
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            Known.TryRemove(new KeyValuePair<string, Task<IReadOnlyList<AttachedFont>?>>(key, reading));
            throw;
        }
    }

    /// <summary>The fonts as ffprobe lists them; null when it couldn't.</summary>
    private static async Task<IReadOnlyList<AttachedFont>?> ReadAsync(MediaFile file, string ffprobePath)
    {
        var ct = CancellationToken.None;
        var json = await RunAsync(ffprobePath, ["-v", "error", "-protocol_whitelist", "file", "-select_streams", "t",
            "-show_entries", "stream=index:stream_tags=filename,mimetype", "-print_format", "json", "-i", file.Path], null, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return [];
        }

        var fonts = new List<AttachedFont>();
        foreach (var stream in streams.EnumerateArray())
        {
            var tags = stream.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Object ? t : default;
            var name = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("filename", out var n) ? n.GetString() : null;
            var mime = tags.ValueKind == JsonValueKind.Object && tags.TryGetProperty("mimetype", out var m) ? m.GetString() : null;
            if (name is null || !stream.TryGetProperty("index", out var index))
            {
                continue;
            }

            // Only fonts, whatever else a file has attached (cover pictures, notes): by extension, or by type when it has none.
            var type = FontTypes.GetValueOrDefault(Path.GetExtension(name))
                ?? (mime is { } x && (x.Contains("font", StringComparison.OrdinalIgnoreCase) || x.Contains("opentype", StringComparison.OrdinalIgnoreCase)) ? "font/ttf" : null);
            if (type is not null)
            {
                fonts.Add(new AttachedFont(index.GetInt32(), Path.GetFileName(name), type));
            }
        }

        return fonts;
    }

    /// <summary>One attached font's bytes, saved in the cache the first time (FFmpeg writes attachments out only to a file).</summary>
    public static async Task<string?> ExtractAsync(MediaFile file, AttachedFont font, TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(font);
        ArgumentNullException.ThrowIfNull(settings);

        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes($"{file.Path}|font{font.StreamIndex}|{File.GetLastWriteTimeUtc(file.Path).Ticks}")))[..32];
        var target = Path.Combine(settings.CacheDirectory, "subtitles", $"{key}.font");
        if (File.Exists(target))
        {
            return target;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".tmp";
        // FFmpeg dumps the attachment as it opens the file, then complains that no output was given: the file is what counts.
        await RunAsync(settings.FfmpegPath, ["-hide_banner", "-v", "error", "-nostdin", "-y", "-protocol_whitelist", "file",
            $"-dump_attachment:{font.StreamIndex}", partial, "-i", file.Path], null, ct).ConfigureAwait(false);
        if (!File.Exists(partial) || new FileInfo(partial).Length == 0)
        {
            File.Delete(partial);
            return null;
        }

        File.Move(partial, target, overwrite: true);
        return target;
    }

    /// <summary>Runs a program and returns what it printed, or null when it failed (except for the dump, where failure is expected).</summary>
    private static async Task<string?> RunAsync(string program, IEnumerable<string> args, string? workingDirectory, CancellationToken ct)
    {
        var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory ?? "" };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            await process.StandardError.ReadToEndAsync(limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException)
        {
            ProcessSignals.KillTree(process);
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }
    }
}
