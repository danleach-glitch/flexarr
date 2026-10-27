using System.Collections.Concurrent;
using Flexarr.Api.Events;
using Flexarr.Streaming;

namespace Flexarr.Api.Playback;

/// <summary>An active playback: what's playing, how, and (for transcodes) the ffmpeg session behind it.</summary>
public sealed class PlaybackSession
{
    public required string Id { get; init; }
    public required Guid ItemId { get; init; }

    /// <summary>Whose history progress is recorded against (stream requests carry no profile of their own).</summary>
    public required string ProfileId { get; init; }

    /// <summary>The browser or device sign-in that started it, so "what's playing on the living room TV" can be answered.</summary>
    public string? SignInId { get; init; }
    public required Guid FileId { get; init; }
    public required string FilePath { get; init; }

    /// <summary>
    /// A podcast episode's sound file on its host, relayed by the stream endpoint (see <see cref="Podcasts.PodcastRelay"/>);
    /// <see cref="FilePath"/> is empty then.
    /// </summary>
    public string? RemoteUrl { get; init; }

    /// <summary>
    /// Where <see cref="RemoteUrl"/>'s redirects (podcast hosts chain several, for counting listens) last led, so a
    /// seek goes straight there rather than through them all again.
    /// </summary>
    public string? ResolvedRemoteUrl { get; set; }
    public required string? Container { get; init; }
    public required double DurationSeconds { get; init; }
    public required PlaybackMethod Method { get; init; }
    public required IReadOnlyList<SubtitleTrack> Subtitles { get; init; }
    public required double? CreditsStart { get; init; }
    public TranscodeSession? Transcode { get; init; }
    public string? AudioLanguage { get; init; }
    /// <summary>The subtitles showing: the server's choice at the start, then whatever the viewer picks in the player.</summary>
    public string? SubtitleLanguage { get; set; }
    /// <summary>The stream's soundtrack carries Dolby Atmos (E-AC-3 with JOC): the master playlist says so.</summary>
    public bool AudioIsAtmos { get; init; }
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
    public double LastPosition { get; set; }
    public bool Paused { get; set; }
    public bool CountedAsPlay { get; set; }

    /// <summary>A Started event went out for it, and no Stopped since (see <see cref="Events.PlaybackEvents"/>).</summary>
    public bool Announced { get; set; }

    /// <summary>It's past the credits marker, so the Credits event has gone out.</summary>
    public bool InCredits { get; set; }

    /// <summary>It played to the end, so the Ended event has gone out.</summary>
    public bool Finished { get; set; }
}

public sealed partial class PlaybackSessions
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly TranscodeManager transcodes;
    private readonly ILogger<PlaybackSessions> logger;
    private readonly PlaybackEvents? events;

    public PlaybackSessions(TranscodeManager transcodes, ILogger<PlaybackSessions> logger, PlaybackEvents? events = null)
    {
        this.transcodes = transcodes;
        this.logger = logger;
        this.events = events;

        // A transcode ended to make way for another viewer takes its playback with it.
        transcodes.TakenOver += id =>
        {
            if (_sessions.TryRemove(id, out var session))
            {
                events?.Removed(session);
            }
        };
    }

    public IEnumerable<PlaybackSession> Active => _sessions.Values;

    public void Add(PlaybackSession session)
    {
        _sessions[session.Id] = session;

        // Another screen's start can end this transcode before its playback gets here, when TakenOver had nothing to
        // remove. The reason is recorded before TakenOver fires, so one of the two always catches it.
        if (transcodes.TakenOverReason(session.Id) is not null)
        {
            _sessions.TryRemove(session.Id, out _);
        }
    }

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
        if (!_sessions.TryRemove(id, out var session))
        {
            return;
        }

        events?.Removed(session);
        if (session.Transcode is { } transcode)
        {
            await transcodes.StopAsync(transcode.Id);
        }
    }

    public async Task SweepAsync()
    {
        events?.Sweep(_sessions.Values, DateTime.UtcNow);
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

/// <summary>Stops idle transcodes and cleans up every few seconds.</summary>
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
