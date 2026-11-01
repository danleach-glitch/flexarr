using System.Collections.Concurrent;
using System.Threading.Channels;
using Flexarr.Api.Authentication;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Playback;
using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Events;

public enum PlaybackEventKind
{
    /// <summary>Something began playing (its first report that it's under way).</summary>
    Started,
    Paused,
    Resumed,

    /// <summary>The end credits began: the moment to bring the lights up.</summary>
    Credits,

    /// <summary>It played to the end.</summary>
    Ended,

    /// <summary>The player closed, or stopped reporting long enough to count as gone.</summary>
    Stopped,
}

/// <summary>
/// A change in what's playing somewhere, for Home Assistant and the like: "a film started on the living room TV".
/// </summary>
/// <param name="SessionId">The playback; every event for one viewing carries the same id.</param>
/// <param name="DeviceId">The browser or device sign-in playing it; null for a stream opened with the API key.</param>
/// <param name="Item">What's playing; null if it has since left the library.</param>
/// <param name="Chapter">The chapter of a book playing. Absent from servers before 0.58.</param>
public sealed record PlaybackEventDto(PlaybackEventKind Kind, string SessionId, string? DeviceId, string? DeviceName, string ProfileId, string? ProfileName,
    ItemSummaryDto? Item, double PositionSeconds, double DurationSeconds, DateTime At, string? Chapter = null);

/// <summary>
/// Works out playback events from the progress players report (started, paused, resumed, credits, ended, stopped) and
/// hands them to everyone listening on <c>GET /events</c>. Events are noticed on the request path and filled in
/// (title, device and profile names) off it, by <see cref="PlaybackEventPublisher"/>.
/// </summary>
public sealed class PlaybackEvents
{
    /// <summary>A playback still under way stops reporting for this long (the app was killed, the TV slept): it's over.</summary>
    public static readonly TimeSpan PlayingSilence = TimeSpan.FromMinutes(2);

    /// <summary>Paused for this long without a word: over too, as the device list stops showing it then.</summary>
    public static readonly TimeSpan PausedSilence = TimeSpan.FromMinutes(15);

    private readonly Channel<(PlaybackEventKind Kind, PlaybackSession Session, DateTime At)> _noticed =
        Channel.CreateUnbounded<(PlaybackEventKind, PlaybackSession, DateTime)>(new UnboundedChannelOptions { SingleReader = true });

    private readonly ConcurrentDictionary<Listener, byte> _listeners = new();

    /// <summary>One open <c>GET /events</c>. Disposing it (the request ended) stops its events.</summary>
    public sealed class Listener : IDisposable
    {
        private readonly PlaybackEvents _owner;

        internal Listener(PlaybackEvents owner) => _owner = owner;

        internal Channel<PlaybackEventDto> Channel { get; } =
            System.Threading.Channels.Channel.CreateBounded<PlaybackEventDto>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

        public ChannelReader<PlaybackEventDto> Events => Channel.Reader;

        public void Dispose()
        {
            _owner._listeners.TryRemove(this, out _);
            Channel.Writer.TryComplete();
        }
    }

    public Listener Listen()
    {
        var listener = new Listener(this);
        _listeners[listener] = 0;
        return listener;
    }

    internal ChannelReader<(PlaybackEventKind Kind, PlaybackSession Session, DateTime At)> Noticed => _noticed.Reader;

    internal void Publish(PlaybackEventDto dto)
    {
        foreach (var listener in _listeners.Keys)
        {
            listener.Channel.Writer.TryWrite(dto);
        }
    }

    /// <summary>
    /// A progress report is in, before it's applied to <paramref name="session"/>: notes what it changes. The first
    /// report of a playing title starts it; a change of pause is a pause or a resume; passing the credits marker
    /// (again, after a seek back) is the credits; an ended report is the end.
    /// </summary>
    public void Observe(PlaybackSession session, ProgressRequest progress)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(progress);
        lock (session)
        {
            session.LastPosition = progress.PositionSeconds;
            if (!session.Announced)
            {
                // A player paused before it got going (or reporting the end of something never seen to start) hasn't started.
                if (progress.Paused || progress.Ended)
                {
                    return;
                }

                session.Announced = true;
                session.Finished = false;
                Notice(PlaybackEventKind.Started, session);
            }
            else if (progress.Paused != session.Paused && !progress.Ended)
            {
                Notice(progress.Paused ? PlaybackEventKind.Paused : PlaybackEventKind.Resumed, session);
            }

            if (session.CreditsStart is { } credits)
            {
                if (progress.PositionSeconds < credits)
                {
                    session.InCredits = false;
                }
                else if (!session.InCredits)
                {
                    session.InCredits = true;
                    Notice(PlaybackEventKind.Credits, session);
                }
            }

            if (progress.Ended && !session.Finished)
            {
                session.Finished = true;
                Notice(PlaybackEventKind.Ended, session);
            }
        }
    }

    /// <summary>The playback is over (closed, taken over, or expired): stopped, if it had started.</summary>
    public void Removed(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        Stop(session);
    }

    /// <summary>Playbacks that have gone quiet: stopped. Should one report again, it starts again.</summary>
    public void Sweep(IEnumerable<PlaybackSession> sessions, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(sessions);
        foreach (var session in sessions)
        {
            var heard = PlaybackSessions.HeardAt(session);
            if (session.Announced && now - heard > (session.Paused ? PausedSilence : PlayingSilence))
            {
                Stop(session);
            }
        }
    }

    private void Stop(PlaybackSession session)
    {
        lock (session)
        {
            if (!session.Announced)
            {
                return;
            }

            session.Announced = false;
            session.InCredits = false;
            Notice(PlaybackEventKind.Stopped, session);
        }
    }

    private void Notice(PlaybackEventKind kind, PlaybackSession session) =>
        _noticed.Writer.TryWrite((kind, session, DateTime.UtcNow));
}

/// <summary>Fills in noticed playback events (the title as the viewer's profile sees it, names) and sends them out.</summary>
public sealed partial class PlaybackEventPublisher(PlaybackEvents events, SignIns signIns, IServiceScopeFactory scopes, ILogger<PlaybackEventPublisher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var (kind, session, at) in events.Noticed.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var item = await db.MediaItems.Where(i => i.Id == session.ItemId).Summaries(session.ProfileId).FirstOrDefaultAsync(stoppingToken);
                var profile = await db.Profiles.AsNoTracking().Where(p => p.Id == session.ProfileId).Select(p => p.Name).FirstOrDefaultAsync(stoppingToken);
                var device = session.SignInId is { } id ? signIns.List().FirstOrDefault(s => s.Id == id)?.Name : null;
                var chapter = session.BookParts is null ? null : await db.MediaChapters.Where(c => c.MediaItemId == session.ItemId && c.StartSeconds <= session.LastPosition)
                    .OrderByDescending(c => c.StartSeconds).Select(c => c.Title).FirstOrDefaultAsync(stoppingToken);
                events.Publish(new PlaybackEventDto(kind, session.Id, session.SignInId, device, session.ProfileId, profile, item, session.LastPosition,
                    session.DurationSeconds, at, chapter));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPublishFailed(ex, kind, session.Id);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't send the {Kind} event for playback {Session}")]
    private partial void LogPublishFailed(Exception ex, PlaybackEventKind kind, string session);
}
