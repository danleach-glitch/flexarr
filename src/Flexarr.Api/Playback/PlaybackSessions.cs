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

public sealed class PlaybackSessions(TranscodeManager transcodes)
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(12);
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);

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
            await RemoveAsync(session.Id);
        }

        await transcodes.SweepAsync();
    }
}

/// <summary>Throttles and cleans up transcodes every few seconds.</summary>
public sealed class PlaybackSweeper(PlaybackSessions sessions, TranscodeManager transcodes) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        transcodes.DeleteOrphans();
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(3));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await sessions.SweepAsync();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await transcodes.DisposeAsync();
        await base.StopAsync(cancellationToken);
    }
}
