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
/// <remarks>
/// A conversion is the server's job, not the request's: it starts when a player is handed the book (or first asks
/// for the file) and carries on when that request gives up, so a player that retries picks up where it got to rather
/// than starting again. Only <see cref="MaxConcurrent"/> run at once, server-wide. Copies unused for
/// <see cref="UnusedFor"/> are pruned, and the oldest go first once they add up to more than <see cref="MaxCacheBytes"/>.
/// </remarks>
public sealed partial class BookFileConverter : IDisposable
{
    /// <summary>Conversions running at once, across every book and listener.</summary>
    public const int MaxConcurrent = 2;

    /// <summary>
    /// The most files one player being handed a book starts converting ahead of time: the one it starts in and the next
    /// few. The rest are converted when they're asked for, so one request can't queue a whole book (or a hundred).
    /// </summary>
    public const int MaxPrepared = 3;

    /// <summary>The most the converted copies may take up together, before the least recently used go.</summary>
    public const long MaxCacheBytes = 10L * 1024 * 1024 * 1024;

    /// <summary>A copy nobody has played for this long is deleted.</summary>
    public static readonly TimeSpan UnusedFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan Timeout = TimeSpan.FromHours(1);
    private static readonly TimeSpan PruneEvery = TimeSpan.FromHours(12);

    /// <summary>Within this of being used, a copy is never pruned for space: it may be playing.</summary>
    private static readonly TimeSpan InUse = TimeSpan.FromHours(1);

    private readonly IOptionsMonitor<FlexarrOptions> _options;
    private readonly ILogger<BookFileConverter> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _jobs = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _slots = new(MaxConcurrent, MaxConcurrent);
    private readonly CancellationTokenSource _stopping = new();
    private long _prunedAt;

    public BookFileConverter(IOptionsMonitor<FlexarrOptions> options, ILogger<BookFileConverter> logger)
    {
        _options = options;
        _logger = logger;
        Encode = FfmpegAsync;
    }

    /// <summary>Converts the file (first) into the new file (second); false when it can't. Tests swap ffmpeg out here.</summary>
    internal Func<string, string, CancellationToken, Task<bool>> Encode { get; set; }

    /// <summary>Where the converted copies are kept.</summary>
    internal string Directory => Path.Combine(PlaybackEndpoints.TranscodeSettingsFor(_options.CurrentValue).CacheDirectory, "books");

    /// <summary>
    /// The converted copy of <paramref name="path"/>, waiting for it to be made if it isn't yet; null if it can't be.
    /// <paramref name="ct"/> stops only the wait: the conversion carries on for the next time it's asked for.
    /// </summary>
    public async Task<string?> ConvertAsync(Guid fileId, string path, CancellationToken ct) =>
        Start(fileId, path) is { } job ? await job.WaitAsync(ct).ConfigureAwait(false) : null;

    /// <summary>
    /// Starts converting the parts a player is about to ask for that need it, the one at <paramref name="fromSeconds"/>
    /// first, so they're ready (or under way) by the time it does; no more than <see cref="MaxPrepared"/>.
    /// </summary>
    public void Prepare(IEnumerable<BookPart> parts, double fromSeconds = 0)
    {
        ArgumentNullException.ThrowIfNull(parts);
        foreach (var part in parts.Where(p => p.Convert).OrderBy(p => p.OffsetSeconds + p.DurationSeconds <= fromSeconds).ThenBy(p => p.Index).Take(MaxPrepared))
        {
            _ = Start(part.FileId, part.Path);
        }
    }

    /// <summary>Deletes the converted copies of these files (a book's, when it's deleted).</summary>
    public void RemoveCopies(IEnumerable<Guid> fileIds)
    {
        ArgumentNullException.ThrowIfNull(fileIds);
        if (!System.IO.Directory.Exists(Directory))
        {
            return;
        }

        foreach (var id in fileIds)
        {
            foreach (var copy in System.IO.Directory.EnumerateFiles(Directory, $"{id:N}-*"))
            {
                TryDelete(copy);
            }
        }
    }

    /// <summary>
    /// Deletes copies nobody has played for <see cref="UnusedFor"/>, leftovers of conversions that never finished, and,
    /// while they add up to more than <see cref="MaxCacheBytes"/>, the least recently played.
    /// </summary>
    internal void Prune(long maxBytes = MaxCacheBytes)
    {
        var directory = new DirectoryInfo(Directory);
        if (!directory.Exists)
        {
            return;
        }

        var now = DateTime.UtcNow;
        // Only the converter's own files: whatever else is in the folder isn't the prune's to delete.
        var files = directory.EnumerateFiles().Where(f => OwnName().IsMatch(f.Name)).ToList();
        foreach (var part in files.Where(f => f.Extension == ".part" && now - f.LastWriteTimeUtc > TimeSpan.FromDays(1)))
        {
            TryDelete(part.FullName); // ffmpeg writes as it goes, so one this old was abandoned.
        }

        var copies = files.Where(f => f.Extension == ".m4a").OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = copies.Sum(f => f.Length);
        foreach (var copy in copies)
        {
            var unused = now - copy.LastWriteTimeUtc;
            if (unused > UnusedFor || (total > maxBytes && unused > InUse))
            {
                if (TryDelete(copy.FullName))
                {
                    total -= copy.Length;
                }
            }
        }
    }

    /// <summary>
    /// Stops every conversion: cancelling kills their ffmpeg. The slots and the token are left undisposed: running jobs
    /// still release one and read the other on their way out, and neither holds anything that needs freeing.
    /// </summary>
    public void Dispose() => _stopping.Cancel();

    private Task<string?>? Start(Guid fileId, string path)
    {
        var source = new FileInfo(path);
        if (!source.Exists)
        {
            return null;
        }

        var directory = System.IO.Directory.CreateDirectory(Directory).FullName;
        var output = Path.Combine(directory, $"{fileId:N}-{source.LastWriteTimeUtc.Ticks}-{source.Length}.m4a");
        PruneNowAndThen();
        if (File.Exists(output))
        {
            Used(output);
            return Task.FromResult<string?>(output);
        }

        // Lazy: two requests at once mustn't both start ffmpeg for the same file.
        return _jobs.GetOrAdd(output, o => new Lazy<Task<string?>>(() => Task.Run(() => RunAsync(fileId, path, o)))).Value;
    }

    private async Task<string?> RunAsync(Guid fileId, string path, string output)
    {
        var stopping = _stopping.Token;
        try
        {
            await _slots.WaitAsync(stopping).ConfigureAwait(false);
            try
            {
                if (File.Exists(output))
                {
                    return output;
                }

                foreach (var stale in System.IO.Directory.EnumerateFiles(Path.GetDirectoryName(output)!, $"{fileId:N}-*")
                    .Where(f => !f.StartsWith(output, StringComparison.Ordinal)))
                {
                    TryDelete(stale); // an earlier version of this file
                }

                // Its own name, renamed into place only once it's whole: a player never gets half a file.
                var temporary = $"{output}.{Guid.NewGuid():N}.part";
                try
                {
                    if (!await Encode(path, temporary, stopping).ConfigureAwait(false) || !File.Exists(temporary))
                    {
                        return null;
                    }

                    File.Move(temporary, output, overwrite: true);
                    return output;
                }
                finally
                {
                    TryDelete(temporary);
                }
            }
            finally
            {
                _slots.Release();
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            return null; // The server is stopping.
        }
#pragma warning disable CA1031 // A conversion that fails is logged and asked for again next time; it mustn't take anything else down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogFailed(Logging.Redactor.ShortHash(path), ex.Message);
            return null;
        }
        finally
        {
            _jobs.TryRemove(output, out _);
        }
    }

    private async Task<bool> FfmpegAsync(string path, string temporary, CancellationToken ct)
    {
        var settings = PlaybackEndpoints.TranscodeSettingsFor(_options.CurrentValue);
        var start = new ProcessStartInfo(settings.FfmpegPath) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var arg in new[] { "-v", "error", "-y", "-i", path, "-map", "0:a:0", "-vn", "-c:a", "aac", "-b:a", "128k", "-movflags", "+faststart", "-f", "mp4", temporary })
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {settings.FfmpegPath}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        // Killed the moment it's cancelled (the server stopping, or the timeout), whatever this method is waiting on.
        using var kill = timeout.Token.Register(() => Kill(process));
        var errors = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            LogFailed(Logging.Redactor.ShortHash(path), (await errors.ConfigureAwait(false)).Trim());
            return false;
        }

        return true;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Exited already.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Exiting as it was asked to.
        }
    }

    /// <summary>Notes that the copy was played (its time is when it was last used), for the prune.</summary>
    private static void Used(string output)
    {
        try
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(output) > InUse)
            {
                File.SetLastWriteTimeUtc(output, DateTime.UtcNow);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void PruneNowAndThen()
    {
        var now = DateTime.UtcNow.Ticks;
        var last = Interlocked.Read(ref _prunedAt);
        if (now - last < PruneEvery.Ticks || Interlocked.CompareExchange(ref _prunedAt, now, last) != last)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                Prune();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogPruneFailed(ex.Message);
            }
        });
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>A copy's name, <c>{file id}-{ticks}-{length}.m4a</c>, or one being written, with <c>.{guid}.part</c> after it.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[0-9a-f]{32}-[0-9]+-[0-9]+\.m4a(\.[0-9a-f]{32}\.part)?$")]
    private static partial System.Text.RegularExpressions.Regex OwnName();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not convert book file {File}: {Error}")]
    private partial void LogFailed(string file, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not tidy the converted book files: {Error}")]
    private partial void LogPruneFailed(string error);
}
