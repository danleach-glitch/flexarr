using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Flexarr.Streaming.Live;

/// <summary>How a recording went: the parts it wrote (more than one when the stream dropped and came back), and the
/// last thing that went wrong, in words for the viewer.</summary>
public sealed record RecordResult(IReadOnlyList<string> Parts, string? Error);

/// <summary>
/// Records a channel as it comes (picture, every sound track, nothing converted) into MPEG-TS parts in a working
/// folder, until a time. If the stream drops it's reconnected into a new part, so a blip loses seconds, not the rest of
/// the programme; parts already in the folder (from before a restart) are kept and followed. <see cref="RemuxAsync"/>
/// joins the parts into the finished file.
/// </summary>
public static partial class LiveRecorder
{
    /// <summary>A part shorter than this, ending early, counts as the stream not playing (rather than a drop).</summary>
    private static readonly TimeSpan Retry = TimeSpan.FromSeconds(10);

    /// <param name="relay">Where a source with a secret in its address is read from (see <see cref="LiveRelay"/>).</param>
    public static async Task<RecordResult> RecordAsync(string ffmpeg, LiveSource source, string workDirectory, DateTime until,
        TimeProvider clock, ILogger logger, CancellationToken ct, LiveRelay? relay = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clock);
        Directory.CreateDirectory(workDirectory);
        // Carrying on after a restart: the parts already written stay, and this one follows them.
        var parts = Directory.GetFiles(workDirectory, "part*.ts").Where(p => new FileInfo(p).Length > 0).Order(StringComparer.Ordinal).ToList();
        string? error = null;
        while (!ct.IsCancellationRequested)
        {
            var left = until - clock.GetUtcNow().UtcDateTime;
            if (left < TimeSpan.FromSeconds(5))
            {
                break;
            }

            var part = Path.Combine(workDirectory, $"part{parts.Count + 1:000}.ts");
            int code;
            string stderr;
            using (var lease = relay?.Open(source))
            {
                (code, stderr) = await RunAsync(ffmpeg, RecordArguments(lease?.Source ?? source, part, left), ct).ConfigureAwait(false);
                stderr = lease?.UpstreamError ?? stderr;
            }

            if (File.Exists(part) && new FileInfo(part).Length > 0)
            {
                parts.Add(part);
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            var stillLeft = until - clock.GetUtcNow().UtcDateTime;
            if (code == 0 && stillLeft < TimeSpan.FromSeconds(30))
            {
                error = null;
                break;
            }

            // Ended early: the stream dropped, or didn't play. Wait a moment and go again into a new part.
            error = stderr.Length > 0 ? LiveStreamException.FromError(source.Redact(stderr)).Message : "The stream stopped.";
            LogDropped(logger, code, source.Redact(stderr));
            try
            {
                await Task.Delay(Retry, clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        return new RecordResult(parts, parts.Count == 0 ? error ?? "Nothing was recorded." : error);
    }

    /// <summary>
    /// Joins the parts into one Matroska file at <paramref name="output"/>, as they are (nothing converted), written
    /// beside it first and moved into place when whole. Null when it worked, else why not.
    /// </summary>
    public static async Task<string?> RemuxAsync(string ffmpeg, IReadOnlyList<string> parts, string output, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (parts.Count == 0)
        {
            return "Nothing was recorded.";
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var partial = output + ".partial";
        List<string> args = ["-hide_banner", "-v", "error", "-nostdin", "-y", "-fflags", "+genpts+discardcorrupt"];
        string? list = null;
        if (parts.Count == 1)
        {
            args.AddRange(["-i", parts[0]]);
        }
        else
        {
            list = Path.Combine(Path.GetDirectoryName(parts[0])!, "parts.txt");
            await File.WriteAllLinesAsync(list, parts.Select(p => $"file '{p.Replace("'", "'\\''", StringComparison.Ordinal)}'"), ct).ConfigureAwait(false);
            args.AddRange(["-f", "concat", "-safe", "0", "-i", list]);
        }

        args.AddRange(["-map", "0:v?", "-map", "0:a?", "-c", "copy", "-f", "matroska", partial]);
        var (code, stderr) = await RunAsync(ffmpeg, args, ct).ConfigureAwait(false);
        if (code != 0 || !File.Exists(partial))
        {
            File.Delete(partial);
            return stderr.Length > 0 ? $"The recording couldn't be saved: {stderr.Split('\n')[^1]}" : "The recording couldn't be saved.";
        }

        File.Move(partial, output, overwrite: true);
        return null;
    }

    internal static List<string> RecordArguments(LiveSource source, string part, TimeSpan length) =>
    [
        "-hide_banner", "-v", "error", "-nostdin", "-y",
        "-fflags", "+genpts+discardcorrupt",
        "-protocol_whitelist", "http,https,tcp,tls",
        "-reconnect", "1", "-reconnect_streamed", "1", "-reconnect_on_network_error", "1", "-reconnect_delay_max", "10",
        "-rw_timeout", "20000000",
        "-i", source.Url,
        "-t", ((int)Math.Ceiling(length.TotalSeconds)).ToString(CultureInfo.InvariantCulture),
        // The picture and every sound track (audio description among them), as they come.
        "-map", "0:v:0", "-map", "0:a?", "-c", "copy", "-sn", "-dn",
        "-max_muxing_queue_size", "2048",
        "-f", "mpegts", part,
    ];

    private static async Task<(int Code, string Error)> RunAsync(string ffmpeg, IEnumerable<string> args, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new LiveStreamException($"Could not start {ffmpeg}");
        var stderr = TailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped early (cancelled, or the server's going): what's written so far is kept, TS being whole to the
            // last packet.
            ProcessSignals.KillTree(process);
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Killed; it'll go.
            }

            return (-1, "");
        }

        return (process.ExitCode, (await stderr.ConfigureAwait(false)).Trim());
    }

    /// <summary>Most lines of ffmpeg's output kept for the log: a flaky source over three hours prints without end.</summary>
    internal const int KeptErrorLines = 20;

    /// <summary>Reads <paramref name="reader"/> to its end a line at a time, keeping only the last few lines.</summary>
    internal static async Task<string> TailAsync(TextReader reader)
    {
        var kept = new Queue<string>(KeptErrorLines);
        while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (kept.Count == KeptErrorLines)
            {
                kept.Dequeue();
            }

            kept.Enqueue(line);
        }

        return string.Join('\n', kept);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording: the stream stopped early (ffmpeg {Code}): {Error}")]
    private static partial void LogDropped(ILogger logger, int code, string error);
}
