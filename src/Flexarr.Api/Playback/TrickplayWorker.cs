using System.Collections.Concurrent;
using System.Threading.Channels;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Streaming;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>Files waiting for scrubbing previews; queued the first time each is played.</summary>
public sealed class TrickplayQueue
{
    private readonly Channel<(Guid FileId, long QueuedAt)> _channel = Channel.CreateUnbounded<(Guid, long)>();
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    public void Enqueue(Guid fileId)
    {
        if (_pending.TryAdd(fileId, 0))
        {
            _channel.Writer.TryWrite((fileId, TimeProvider.System.GetTimestamp()));
        }
    }

    public IAsyncEnumerable<(Guid FileId, long QueuedAt)> ReadAllAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    public void Done(Guid fileId) => _pending.TryRemove(fileId, out _);
}

public sealed partial class TrickplayWorker(TrickplayQueue queue, IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, ILogger<TrickplayWorker> logger) : BackgroundService
{
    /// <summary>
    /// How long after a file's first play its previews wait: decoding the whole film while the player is still
    /// waiting for its first segments can hold the picture up for most of a minute (#202).
    /// </summary>
    private static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (fileId, queuedAt) in queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var wait = StartDelay - TimeProvider.System.GetElapsedTime(queuedAt);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stoppingToken);
                }

                var settings = PlaybackEndpoints.TranscodeSettingsFor(options.CurrentValue);
                if (Trickplay.Read(settings.CacheDirectory, fileId) is not null)
                {
                    continue;
                }

                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var file = await db.MediaFiles.AsNoTracking().Include(f => f.Streams).FirstOrDefaultAsync(f => f.Id == fileId, stoppingToken);
                if (file is not { Width: > 0, Height: > 0 })
                {
                    continue;
                }

                var started = TimeProvider.System.GetTimestamp();
                var tenBit = file.Streams.Any(s => s.Type == StreamType.Video && s.Profile?.Contains("10", StringComparison.Ordinal) == true);
                var manifest = await Trickplay.GenerateAsync(file.Path, file.Id, file.Width.Value, file.Height.Value, file.VideoRange != VideoRange.Sdr, tenBit, settings, stoppingToken);
                if (manifest is null)
                {
                    LogFailed(file.Path);
                }
                else
                {
                    LogGenerated(Path.GetFileName(file.Path), manifest.Count, TimeProvider.System.GetElapsedTime(started).TotalSeconds);
                }
            }
            // One file failing (ffmpeg missing, the database busy) mustn't stop previews for the rest, or the server.
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogError(ex, fileId);
            }
            finally
            {
                queue.Done(fileId);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Made scrubbing previews for {File}: {Count} frames in {Seconds:F0}s")]
    private partial void LogGenerated(string file, int count, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't make scrubbing previews for {Path}")]
    private partial void LogFailed(string path);

    [LoggerMessage(Level = LogLevel.Error, Message = "Making scrubbing previews for file {FileId} failed")]
    private partial void LogError(Exception exception, Guid fileId);
}
