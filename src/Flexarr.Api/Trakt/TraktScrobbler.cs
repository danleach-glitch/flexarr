using System.Collections.Concurrent;
using Flexarr.Api.Events;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Flexarr.Core.Media;

namespace Flexarr.Api.Trakt;

/// <summary>
/// Tells Trakt what's playing, from the same events Home Assistant hears: start, pause and stop, so Trakt shows
/// "watching now". Flexarr decides when something's watched (<see cref="PlaybackService.WatchedThreshold"/>, or the end
/// credits), and Trakt is only sent a stop when it is, with the progress Trakt needs to count it; anything left part
/// way is sent as a pause, which keeps the place on Trakt. So the two never disagree about what was seen.
/// </summary>
public sealed partial class TraktScrobbler(PlaybackEvents events, IServiceScopeFactory scopes, ILogger<TraktScrobbler> logger) : BackgroundService
{
    /// <summary>Playbacks already sent a stop, so the stop that follows the end (closing the player) sends nothing more.</summary>
    private readonly ConcurrentDictionary<string, byte> _finished = new(StringComparer.Ordinal);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var listener = events.Listen();
        await foreach (var e in listener.Events.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (e.Item is { } item)
                {
                    await HandleAsync(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>(), e.Kind, e.SessionId, e.ProfileId, item.Id, item.Kind,
                        e.PositionSeconds, e.DurationSeconds, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogFailed(ex, e.Kind);
            }
        }
    }

    /// <summary>What a playback event sends Trakt, if anything: the action, or null.</summary>
    internal string? ActionFor(PlaybackEventKind kind, string sessionId, double percent)
    {
        switch (kind)
        {
            case PlaybackEventKind.Started or PlaybackEventKind.Resumed:
                _finished.TryRemove(sessionId, out _);
                return "start";
            case PlaybackEventKind.Paused:
                return _finished.ContainsKey(sessionId) ? null : "pause";
            case PlaybackEventKind.Credits or PlaybackEventKind.Ended:
                return _finished.TryAdd(sessionId, 0) ? "stop" : null;
            case PlaybackEventKind.Stopped:
                if (_finished.TryRemove(sessionId, out _))
                {
                    return null;
                }

                return percent >= PlaybackService.WatchedThreshold * 100 ? "stop" : "pause";
            default:
                return null;
        }
    }

    internal async Task HandleAsync(FlexarrDbContext db, PlaybackEventKind kind, string sessionId, string profileId, Guid itemId, MediaKind itemKind,
        double position, double duration, CancellationToken ct)
    {
        if (itemKind is not (MediaKind.Movie or MediaKind.Episode) || duration <= 0)
        {
            return;
        }

        var percent = Math.Clamp(100 * position / duration, 0, 100);
        if (ActionFor(kind, sessionId, percent) is not { } action || !await TraktOutbox.ConnectedAsync(db, profileId, ct))
        {
            return;
        }

        if ((await TraktOutbox.TitlesAsync(db, [itemId], ct)).GetValueOrDefault(itemId) is not { } title)
        {
            return;
        }

        // A finish Flexarr counts (the credits can start well before Trakt's 80%) is a finish on Trakt too. A pause never
        // counts on Trakt, whatever its progress, so it carries the real place for resuming elsewhere.
        var progress = action == "stop" ? Math.Max(percent, 95) : percent;
        var body = title.Kind == MediaKind.Movie
            ? new TraktScrobbleBody(new TraktSyncMovie(title.Ids), null, null, Math.Round(progress, 2))
            : new TraktScrobbleBody(null, new TraktSyncShow(title.Ids), new TraktEpisode(title.Season!.Value, title.Number!.Value), Math.Round(progress, 2));
        await TraktOutbox.QueueScrobbleAsync(db, profileId, action, body, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't scrobble a {Kind} event to Trakt")]
    private partial void LogFailed(Exception ex, PlaybackEventKind kind);
}
