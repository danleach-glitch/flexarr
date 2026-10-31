using System.Collections.Concurrent;
using System.Diagnostics;
using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Books;

/// <summary>
/// Converts a book file a player can't decode (WMA, Ogg on an iPhone) to AAC in an .m4a, once, and keeps it in the
/// cache: book files are long, so converting them on the fly while someone seeks around would be slow and costly.
/// Most books never need it: m4b, mp3 and m4a play everywhere.
/// </summary>
public sealed partial class BookFileConverter(IOptionsMonitor<FlexarrOptions> options, ILogger<BookFileConverter> logger)
{
    private static readonly TimeSpan Timeout = TimeSpan.FromHours(1);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <summary>The converted copy of <paramref name="path"/>, made now if there isn't one yet; null if it can't be.</summary>
    public async Task<string?> ConvertAsync(Guid fileId, string path, CancellationToken ct)
    {
        var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
        var source = new FileInfo(path);
        if (!source.Exists)
        {
            return null;
        }

        var directory = Directory.CreateDirectory(Path.Combine(settings.CacheDirectory, "books")).FullName;
        var output = Path.Combine(directory, $"{fileId:N}-{source.LastWriteTimeUtc.Ticks}-{source.Length}.m4a");
        if (File.Exists(output))
        {
            return output;
        }

        var gate = _locks.GetOrAdd(output, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(output))
            {
                return output;
            }

            foreach (var stale in Directory.EnumerateFiles(directory, $"{fileId:N}-*"))
            {
                File.Delete(stale); // an earlier version of this file
            }

            var temporary = output + ".part";
            var start = new ProcessStartInfo(settings.FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var arg in new[] { "-v", "error", "-y", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", temporary })
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {settings.FfmpegPath}");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            var errors = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                process.Kill(entireProcessTree: true);
                File.Delete(temporary);
                throw;
            }

            if (process.ExitCode != 0)
            {
                LogFailed(Logging.Redactor.ShortHash(path), (await errors.ConfigureAwait(false)).Trim());
                File.Delete(temporary);
                return null;
            }

            File.Move(temporary, output, overwrite: true);
            return output;
        }
        finally
        {
            gate.Release();
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not convert book file {File}: {Error}")]
    private partial void LogFailed(string file, string error);
}
