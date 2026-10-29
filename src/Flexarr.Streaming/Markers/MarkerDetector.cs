using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flexarr.Core.Media;

namespace Flexarr.Streaming.Markers;

public sealed record DetectedMarker(MarkerType Type, double Start, double End, string Source);

/// <summary>
/// Finds intros and end credits. Chapter titles win when a file has them ("Intro", "Opening", "Credits");
/// otherwise episodes of a season are compared with each other by audio fingerprint, since a show's
/// theme tune and credits music repeat while everything else differs.
/// </summary>
public sealed partial class MarkerDetector(string ffmpegPath, string ffprobePath)
{
    private const double IntroWindow = 480;   // intros start in the first few minutes
    private const double CreditsWindow = 300; // credits live in the last five
    private const double MinIntro = 15;
    private const double MaxIntro = 150;
    private const double MinCredits = 15;
    private static readonly string[] QuietArgs = ["-hide_banner", "-v", "error", "-nostdin"];

    /// <summary>Longest one ffmpeg or ffprobe run may take: a stalled network share mustn't stop detection for good.</summary>
    private static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(5);

    /// <summary>Markers for each file of one season, in episode order. Files with nothing found map to an empty list.</summary>
    public async Task<Dictionary<Guid, List<DetectedMarker>>> DetectSeasonAsync(IReadOnlyList<MediaFile> episodes, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(episodes);

        var result = episodes.ToDictionary(e => e.Id, _ => new List<DetectedMarker>());
        var needsIntro = new List<MediaFile>();
        var needsCredits = new List<MediaFile>();

        foreach (var file in episodes)
        {
            var fromChapters = await ChapterMarkersAsync(file.Path, ct).ConfigureAwait(false);
            result[file.Id].AddRange(fromChapters);
            if (fromChapters.All(m => m.Type != MarkerType.Intro))
            {
                needsIntro.Add(file);
            }

            if (fromChapters.All(m => m.Type != MarkerType.Credits))
            {
                needsCredits.Add(file);
            }
        }

        if (needsIntro.Count >= 2 || needsCredits.Count >= 2)
        {
            var heads = new Dictionary<Guid, uint[]>();
            var tails = new Dictionary<Guid, uint[]>();
            foreach (var file in episodes)
            {
                if (needsIntro.Count >= 2)
                {
                    heads[file.Id] = AudioFingerprint.Compute(await PcmAsync(file.Path, fromEnd: false, IntroWindow, ct).ConfigureAwait(false));
                }

                if (needsCredits.Count >= 2 && file.DurationSeconds is > CreditsWindow * 2)
                {
                    tails[file.Id] = AudioFingerprint.Compute(await PcmAsync(file.Path, fromEnd: true, CreditsWindow, ct).ConfigureAwait(false));
                }
            }

            foreach (var file in needsIntro.Where(f => heads.ContainsKey(f.Id)))
            {
                var shared = Neighbours(episodes, file)
                    .Where(n => heads.ContainsKey(n.Id))
                    .Select(n => AudioFingerprint.FindShared(heads[file.Id], heads[n.Id], MinIntro, MaxIntro))
                    .Where(s => s is not null)
                    .MaxBy(s => s!.Duration);
                if (shared is not null)
                {
                    // An intro that starts within a few seconds of the beginning starts at the beginning.
                    var start = shared.StartA < 3 ? 0 : shared.StartA;
                    result[file.Id].Add(new DetectedMarker(MarkerType.Intro, start, shared.EndA, "fingerprint"));
                }
            }

            foreach (var file in needsCredits.Where(f => tails.ContainsKey(f.Id)))
            {
                var shared = Neighbours(episodes, file)
                    .Where(n => tails.ContainsKey(n.Id))
                    .Select(n => AudioFingerprint.FindShared(tails[file.Id], tails[n.Id], MinCredits, CreditsWindow))
                    .Where(s => s is not null)
                    .MaxBy(s => s!.Duration);
                if (shared is not null && file.DurationSeconds is { } duration)
                {
                    var windowStart = duration - CreditsWindow;
                    result[file.Id].Add(new DetectedMarker(MarkerType.Credits, windowStart + shared.StartA, duration, "fingerprint"));
                }
            }
        }

        return result;
    }

    /// <summary>Markers from chapter titles alone (used for movies, and first for episodes).</summary>
    public async Task<List<DetectedMarker>> ChapterMarkersAsync(string path, CancellationToken ct)
    {
        var json = await RunAsync(ffprobePath, ["-v", "error", .. FfmpegCommand.LocalFile, "-print_format", "json", "-show_chapters", path], ct).ConfigureAwait(false);
        return json is null ? [] : FromChapters(json);
    }

    internal static List<DetectedMarker> FromChapters(string ffprobeJson)
    {
        using var doc = JsonDocument.Parse(ffprobeJson);
        var markers = new List<DetectedMarker>();
        if (!doc.RootElement.TryGetProperty("chapters", out var chapters))
        {
            return markers;
        }

        // How long the file is, as far as its chapters go: a "Credits" chapter early in it is the opening credits.
        var total = chapters.EnumerateArray()
            .Select(c => double.TryParse(c.GetProperty("end_time").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var e) ? e : 0)
            .DefaultIfEmpty(0).Max();
        foreach (var chapter in chapters.EnumerateArray())
        {
            var title = chapter.TryGetProperty("tags", out var tags) && tags.TryGetProperty("title", out var t) ? t.GetString() : null;
            var type = title switch
            {
                not null when IntroTitle().IsMatch(title) => MarkerType.Intro,
                not null when RecapTitle().IsMatch(title) => MarkerType.Recap,
                not null when CreditsTitle().IsMatch(title) => MarkerType.Credits,
                _ => (MarkerType?)null,
            };
            if (type is { } kind
                && double.TryParse(chapter.GetProperty("start_time").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var start)
                && double.TryParse(chapter.GetProperty("end_time").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var end)
                && end > start)
            {
                // Opening credits (Peacemaker's dance, Luther's titles) are an intro to skip, not the end to move on from.
                if (kind == MarkerType.Credits && total > 0 && start < total * 0.6)
                {
                    kind = MarkerType.Intro;
                }
                // End credits run to (nearly) the end, leaving room for a post-credits scene. A film's "Ending" chapter
                // three quarters in is its climax, not credits to skip.
                else if (kind == MarkerType.Credits && total > 0 && total - end > Math.Max(300, total * 0.1))
                {
                    continue;
                }

                markers.Add(new DetectedMarker(kind, start, end, "chapters"));
            }
        }

        return markers;
    }

    /// <summary>The episodes either side of this one: themes are most stable between adjacent episodes.</summary>
    private static IEnumerable<MediaFile> Neighbours(IReadOnlyList<MediaFile> episodes, MediaFile file)
    {
        var i = episodes.ToList().IndexOf(file);
        foreach (var j in new[] { i + 1, i - 1, i + 2, i - 2 })
        {
            if (j >= 0 && j < episodes.Count)
            {
                yield return episodes[j];
            }
        }
    }

    private async Task<short[]> PcmAsync(string path, bool fromEnd, double seconds, CancellationToken ct)
    {
        var window = seconds.ToString(CultureInfo.InvariantCulture);
        string[] seek = fromEnd ? ["-sseof", $"-{window}"] : ["-ss", "0"];
        var start = new ProcessStartInfo(ffmpegPath) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in QuietArgs.Concat(seek).Concat(
                     [.. FfmpegCommand.LocalFile, "-i", path, "-t", window, "-map", "0:a:0", "-vn", "-sn", "-ac", "1", "-ar", AudioFingerprint.SampleRate.ToString(CultureInfo.InvariantCulture), "-f", "s16le", "-"]))
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        using var buffer = new MemoryStream();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RunLimit);
        try
        {
            var drainErrors = process.StandardError.ReadToEndAsync(limit.Token);
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, limit.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            await drainErrors.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            ProcessSignals.KillTree(process);
            ThrowIfTimedOut(path, ct);
            throw;
        }

        var bytes = buffer.GetBuffer().AsSpan(0, (int)buffer.Length);
        var samples = new short[bytes.Length / 2];
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(bytes[..(samples.Length * 2)]).CopyTo(samples);
        return samples;
    }

    private static async Task<string?> RunAsync(string executable, string[] args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(RunLimit);
        try
        {
            // Read both at once: a process blocked writing a full stderr pipe would never finish its stdout.
            var output = process.StandardOutput.ReadToEndAsync(limit.Token);
            var errors = process.StandardError.ReadToEndAsync(limit.Token);
            await Task.WhenAll(output, errors).ConfigureAwait(false);
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (OperationCanceledException)
        {
            ProcessSignals.KillTree(process);
            ThrowIfTimedOut(args[^1], ct);
            throw;
        }
    }

    /// <summary>Cancelled by <see cref="RunLimit"/> rather than by the caller: a failure for this file, not a shutdown.</summary>
    private static void ThrowIfTimedOut(string path, CancellationToken ct)
    {
        if (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Reading {Path.GetFileName(path)} took more than {RunLimit.TotalMinutes} minutes");
        }
    }

    [GeneratedRegex(@"^\s*(intro|opening|opening credits|op|title sequence|theme)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex IntroTitle();

    [GeneratedRegex(@"^\s*(recap|previously( on)?|preview)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex RecapTitle();

    [GeneratedRegex(@"^\s*(credits|end credits|closing credits|ending|ed|outro)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex CreditsTitle();
}
