using System.Collections.Concurrent;
using Flexarr.Api.Devices;
using Flexarr.Api.Events;
using Flexarr.Streaming;

namespace Flexarr.Api.Playback;

/// <summary>An active playback: what's playing, how, and (for transcodes) the ffmpeg session behind it.</summary>
public sealed class PlaybackSession
{
    public required string Id { get; init; }
    public required Guid ItemId { get; init; }

    /// <summary>A book's files, in order on its timeline, served at <c>/playback/{sid}/book/{index}</c>. Null for anything else.</summary>
    public IReadOnlyList<Books.BookPart>? BookParts { get; init; }

    /// <summary>The device's name, for a book's "Jump to where you were on …".</summary>
    public string? DeviceName { get; init; }

    /// <summary>
    /// The player said it plays books (it named itself, as only apps that know books do). One that didn't plays a book
    /// as a video, its first file only, so its "ended" means nothing for a book of several.
    /// </summary>
    public bool PlaysBooks { get; init; }

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
    public DateTime Created { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Got ready ahead of Play (<see cref="PlaybackRequest.Prepare"/>) and not yet used by a player: not shown as
    /// playing anywhere, and its conversion gives way to any stream being watched. A player's first progress report, its
    /// first request for a converted stream's playlists or segments, or reading the file it plays as it is once
    /// <see cref="PlaybackSessions.WarmingGrace"/> has passed (Apple's engine fetches a file's opening bytes ahead of
    /// time, straight away) ends it.
    /// </summary>
    public bool Warming { get; set; }

    /// <summary>The title, for the log line once a playback got ready ahead of Play is taken up.</summary>
    public string? Title { get; init; }

    /// <summary>Work put off while it was only got ready (previews, intro detection, subtitles), done once it's used.</summary>
    public Action? OnUsed { get; set; }
    public double LastPosition { get; set; }
    public bool Paused { get; set; }

    /// <summary>When the player last said where it was (<see cref="LastSeen"/> moves with every request for the stream too).</summary>
    public DateTime? ReportedAt { get; set; }

    /// <summary>What the player last told remotes (see <see cref="ProgressRequest"/>): null from players that don't.</summary>
    public RemoteState? Remote { get; set; }

    /// <summary>Shown as what its device is playing at the last look (see <see cref="PlaybackSessions.Shows"/>), so going quiet is noticed once.</summary>
    public bool Shown { get; set; }

    /// <summary>The title as remotes are told it, kept for the playback so each report doesn't look it up again.</summary>
    public Endpoints.ItemSummaryDto? Summary { get; set; }
    public bool CountedAsPlay { get; set; }

    /// <summary>A Started event went out for it, and no Stopped since (see <see cref="Events.PlaybackEvents"/>).</summary>
    public bool Announced { get; set; }

    /// <summary>It's past the credits marker, so the Credits event has gone out.</summary>
    public bool InCredits { get; set; }

    /// <summary>It played to the end, so the Ended event has gone out.</summary>
    public bool Finished { get; set; }
}

/// <summary>What a player told remotes about itself in its progress reports, the latest of each.</summary>
public sealed record RemoteState(double Rate, PlayerPrompt? Prompt, bool HasNext, PlayerTracks? Tracks, Devices.DeviceSound? Sound);

public sealed partial class PlaybackSessions
{
    private static readonly TimeSpan Expiry = TimeSpan.FromHours(12);

    /// <summary>A playback that hasn't been heard from for this long isn't shown as what its device is playing.</summary>
    public static readonly TimeSpan NowPlayingStaleness = TimeSpan.FromMinutes(15);

    /// <summary>The most tracks of each kind kept from a report, and the longest name and id.</summary>
    private const int MaxTracks = 50;
    private const int MaxLabel = 100;
    private const int MaxTrackId = 64;
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _bookReports = new(StringComparer.Ordinal);
    private readonly TranscodeManager transcodes;
    private readonly ILogger<PlaybackSessions> logger;
    private readonly PlaybackEvents? events;
    private readonly NowPlayingFeed? nowPlaying;

    public PlaybackSessions(TranscodeManager transcodes, ILogger<PlaybackSessions> logger, PlaybackEvents? events = null, NowPlayingFeed? nowPlaying = null)
    {
        this.transcodes = transcodes;
        this.logger = logger;
        this.events = events;
        this.nowPlaying = nowPlaying;

        // A transcode ended to make way for another viewer takes its playback with it; so does one got ready ahead of
        // Play that made way (nobody was watching it, so there's nothing to tell).
        void Remove(string id)
        {
            if (_sessions.TryRemove(id, out var session))
            {
                events?.Removed(session);
                nowPlaying?.Notice(session);
            }
        }

        transcodes.TakenOver += Remove;
        transcodes.Yielded += Remove;
    }

    public IEnumerable<PlaybackSession> Active => _sessions.Values;

    /// <summary>The playbacks a player is using: <see cref="Active"/> without those only got ready ahead of Play.</summary>
    public IEnumerable<PlaybackSession> Playing => _sessions.Values.Where(s => !s.Warming);

    /// <summary>A file played as it is, still being read this long after it was got ready, is being played.</summary>
    public static readonly TimeSpan WarmingGrace = TimeSpan.FromSeconds(30);

    /// <summary>A player is using <paramref name="session"/>: if it was only got ready ahead of Play, it now counts as playing.</summary>
    public void Used(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (!session.Warming)
        {
            return;
        }

        session.Warming = false;
        session.LastSeen = DateTime.UtcNow;
        if (session.Transcode is { } transcode)
        {
            transcode.Warming = false;
        }

        var deferred = session.OnUsed;
        session.OnUsed = null;
        deferred?.Invoke();
        LogTakenUp(Logging.Redactor.ShortHash(session.Id), session.Title ?? "a title");
        nowPlaying?.Notice(session);
    }

    /// <summary>
    /// The file <paramref name="session"/> plays as it is (or a book's) was asked for: in use, unless it's the opening
    /// bytes fetched just after it was got ready.
    /// </summary>
    public void Streamed(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Warming && DateTime.UtcNow - session.Created > WarmingGrace)
        {
            Used(session);
        }
    }

    /// <summary>
    /// Notes that a device (by its sign-in) just said where it is in a book it plays without a playback here: a phone
    /// playing a downloaded book reports its place, and nothing else.
    /// </summary>
    public void BookReported(string signInId) => _bookReports[signInId] = DateTime.UtcNow;

    /// <summary>When the device last said where it was in a book it plays itself; null if not since the server started.</summary>
    public DateTime? BookReportedAt(string signInId) => _bookReports.TryGetValue(signInId, out var at) ? at : null;

    public void Add(PlaybackSession session)
    {
        _sessions[session.Id] = session;

        // Another screen's start can end this transcode before its playback gets here, when TakenOver had nothing to
        // remove. The reason is recorded before TakenOver fires, so one of the two always catches it.
        if (transcodes.TakenOverReason(session.Id) is not null)
        {
            _sessions.TryRemove(session.Id, out _);
        }

        nowPlaying?.Notice(session);
    }

    /// <summary>The player reported where it's got to (and, from apps that do, what a remote needs): remotes follow.</summary>
    public void Reported(PlaybackSession session, ProgressRequest progress)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(progress);
        Used(session);
        session.LastPosition = progress.PositionSeconds;
        session.Paused = progress.Paused;
        session.ReportedAt = DateTime.UtcNow;
        session.Shown = true;
        if (progress.ForRemotes)
        {
            var before = session.Remote;
            var rate = progress.Rate is { } r && double.IsFinite(r) ? Math.Clamp(r, 0.25, 4) : before?.Rate ?? 1;
            session.Remote = new RemoteState(rate, Parse<PlayerPrompt>(progress.Prompt), progress.HasNext ?? false, Trim(progress.Tracks) ?? before?.Tracks,
                Sound(progress.Sound) ?? before?.Sound);
        }

        nowPlaying?.Notice(session);
    }

    /// <summary>
    /// Whether <paramref name="session"/> counts as what its device is playing at <paramref name="now"/>: heard from in the
    /// last quarter of an hour, and, playing rather than paused, reporting (as the playback events count it).
    /// </summary>
    public static bool Shows(PlaybackSession session, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(session);
        return !session.Warming && now - session.LastSeen < NowPlayingStaleness && (session.Paused || now - HeardAt(session) < PlaybackEvents.PlayingSilence);
    }

    /// <summary>
    /// When a playback was last heard from, for telling it has gone quiet. A player that tells remotes (an Apple TV from
    /// 0.56) playing: its last report, as requests for its stream can go on while its app is gone. Anything else, or
    /// paused: its last request of any kind (a phone casting stops reporting while the TV it cast to streams on).
    /// </summary>
    public static DateTime HeardAt(PlaybackSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return !session.Paused && session.Remote is not null ? session.ReportedAt ?? session.LastSeen : session.LastSeen;
    }

    private static T? Parse<T>(string? name) where T : struct, Enum =>
        Enum.TryParse<T>(name, ignoreCase: false, out var value) && Enum.IsDefined(value) && !int.TryParse(name, out _) ? value : null;

    private static DeviceSound? Sound(ReportedSound? sound) =>
        sound is null ? null : new DeviceSound(Parse<DialogueBoost>(sound.DialogueBoost), Parse<NightMode>(sound.NightMode), Parse<Speakers>(sound.Speakers));

    /// <summary>A report's tracks within bounds: a misbehaving player can't fill memory or every phone's screen.</summary>
    private static PlayerTracks? Trim(PlayerTracks? tracks)
    {
        if (tracks is null)
        {
            return null;
        }

        static IReadOnlyList<PlayerTrack> Keep(IReadOnlyList<PlayerTrack>? list) => (list ?? [])
            .Where(t => t is { Id.Length: > 0 and <= MaxTrackId, Label: not null })
            .Take(MaxTracks)
            .Select(t => t.Label.Length > MaxLabel ? t with { Label = t.Label[..MaxLabel] } : t)
            .ToList();

        return new PlayerTracks(Keep(tracks.Audio), Keep(tracks.Subtitles));
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
        nowPlaying?.Notice(session);
        if (session.Transcode is { } transcode)
        {
            await transcodes.StopAsync(transcode.Id);
        }
    }

    public async Task SweepAsync()
    {
        var now = DateTime.UtcNow;
        events?.Sweep(_sessions.Values, now);

        // A device that went quiet (its app killed, the TV asleep) stops being shown as playing: remotes and Live
        // Activities hear so now, rather than when the playback finally expires.
        foreach (var session in _sessions.Values)
        {
            var shows = Shows(session, now);
            if (session.Shown && !shows)
            {
                nowPlaying?.Notice(session);
            }

            session.Shown = shows;
        }

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

    [LoggerMessage(Level = LogLevel.Information, Message = "Playback {PlaybackId} of {Title}: got ready ahead of Play, now playing")]
    private partial void LogTakenUp(string playbackId, string title);

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
