using System.Collections.Concurrent;
using Flexarr.Streaming;

namespace Flexarr.Api.Playback;

/// <summary>An active playback: what's playing, how, and (for transcodes) the ffmpeg session behind it.</summary>
public sealed class PlaybackSession
{
    public required string Id { get; init; }
    public required Guid ItemId { get; init; }

    /// <summary>Whose history progress is recorded against (stream requests carry no profile of their own).</summary>
    public required string ProfileId { get; init; }
    public required Guid FileId { get; init; }
    public required string FilePath { get; init; }
    public required string? Container { get; init; }
    public required double DurationSeconds { get; init; }
    public required PlaybackMethod Method { get; init; }
    public required IReadOnlyList<SubtitleTrack> Subtitles { get; init; }
    public required double? CreditsStart { get; init; }
    public TranscodeSession? Transcode { get; init; }
    public string? AudioLanguage { get; init; }
    public string? SubtitleLanguage { get; init; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public double LastPosition { get; set; }
    public bool Paused { get; set; }
    public bool CountedAsPlay { get; set; }
}

public sealed partial class PlaybackSessions
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly TranscodeManager transcodes;
    private readonly ILogger<PlaybackSessions> logger;

    public PlaybackSessions(TranscodeManager transcodes, ILogger<PlaybackSessions> logger)
    {
        this.transcodes = transcodes;
        this.logger = logger;

        // A transcode ended to make way for another viewer takes its playback with it.
        transcodes.TakenOver += id => _sessions.TryRemove(id, out _);
    }

    public IEnumerable<PlaybackSession> Active => _sessions.Values;

    public void Add(PlaybackSession session) => _sessions[session.Id] = session;

    public PlaybackSession? Get(string id)
    {
        if (_sessions.TryGetValue(id, out var session))
        {
            session.LastSeen = DateTime.UtcNow;
        }

        return session;
    }

    public async Task RemoveAsync(string id)
    {
        if (_sessions.TryRemove(id, out var session) && session.Transcode is { } transcode)
        {
            await transcodes.StopAsync(transcode.Id);
        }
    }

    public async Task SweepAsync()
    {
        foreach (var session in _sessions.Values.Where(s => DateTime.UtcNow - s.LastSeen > Expiry).ToList())
        {
            try
            {
                await RemoveAsync(session.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogExpireFailed(ex, session.Id);
            }
        }

        await transcodes.SweepAsync();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Couldn't end expired playback session {Session}")]
    private partial void LogExpireFailed(Exception ex, string session);
}

/// <summary>Throttles and cleans up transcodes every few seconds.</summary>
public sealed partial class PlaybackSweeper(PlaybackSessions sessions, TranscodeManager transcodes, ILogger<PlaybackSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            transcodes.DeleteOrphans();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogSweepFailed(ex);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            // One bad sweep mustn't end the loop (or stop the host): idle transcodes would never be cleaned up again.
            try
            {
                await sessions.SweepAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSweepFailed(ex);
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await transcodes.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Playback cleanup failed")]
    private partial void LogSweepFailed(Exception ex);
}
