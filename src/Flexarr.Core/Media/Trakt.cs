namespace Flexarr.Core.Media;

/// <summary>
/// A profile's Trakt account: its tokens and where the two-way sync has got to. One row per profile, and one profile
/// per Trakt account (two profiles on one account would scrobble everything twice). Kept in the database, so backups
/// carry it; Disconnect and deleting the profile remove it.
/// </summary>
public sealed class TraktConnection
{
    public required string ProfileId { get; set; }

    public required string AccessToken { get; set; }
    public required string RefreshToken { get; set; }

    /// <summary>When <see cref="AccessToken"/> stops working (Trakt's tokens last 24 hours); refreshed ahead of it.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>The account's slug, which Trakt's user URLs use and which never changes; unique across profiles.</summary>
    public required string UserSlug { get; set; }

    /// <summary>Its display name ("username"), for Settings.</summary>
    public required string Username { get; set; }

    public DateTime ConnectedAt { get; set; } = DateTime.UtcNow;
    public TraktStatus Status { get; set; } = TraktStatus.Connected;

    /// <summary>Trakt refused a watchlist addition because a free account's list is full.</summary>
    public bool WatchlistFull { get; set; }

    /// <summary>The last problem talking to Trakt, in plain words, cleared by the next success; for Settings.</summary>
    public string? LastError { get; set; }

    /// <summary>When the sync last heard from Trakt.</summary>
    public DateTime? LastSyncAt { get; set; }

    /// <summary>
    /// Trakt's <c>/sync/last_activities</c> as of the last sync, raw JSON, compared with the next poll so only what
    /// changed is fetched.
    /// </summary>
    public string? LastActivities { get; set; }

    /// <summary>
    /// Plays on Trakt are taken up to here. The next sync asks only for plays after it, so an old play can't mark
    /// something watched again after it was marked unwatched in Flexarr. Null until the first import.
    /// </summary>
    public DateTime? HistoryCursor { get; set; }

    /// <summary>
    /// When the watchlists were last put in step. A title on Flexarr's list since then that Trakt lacks is new here (and
    /// goes to Trakt); one from before that Trakt lacks was taken off there (and comes off here). Null until the first
    /// sync, which only adds, both ways.
    /// </summary>
    public DateTime? WatchlistSyncedAt { get; set; }

    /// <summary>
    /// When Trakt's whole watched list was last compared with Flexarr's, which catches plays added there with an earlier
    /// date (the history cursor only sees newer ones). Done at most daily, when Trakt says something was watched.
    /// </summary>
    public DateTime? ReconciledAt { get; set; }

    /// <summary>When the history import (preview, then confirm) finished; null until it has.</summary>
    public DateTime? ImportedAt { get; set; }

    /// <summary>The Trakt lists shown as collections for this profile: "{user slug}/{list id}".</summary>
    public List<string> ListIds { get; set; } = [];

    /// <summary>When the chosen lists were last brought across; they're looked at again after a few hours.</summary>
    public DateTime? ListsSyncedAt { get; set; }
}

public enum TraktStatus
{
    Connected,

    /// <summary>Trakt refused the refresh token (revoked on trakt.tv, or unused for months): connect again.</summary>
    NeedsSignIn,
}

/// <summary>What a queued change tells Trakt.</summary>
public enum TraktOutboxKind
{
    /// <summary>Plays to add to history (<c>/sync/history</c>).</summary>
    History,

    WatchlistAdd,
    WatchlistRemove,

    /// <summary>A live scrobble (start, pause or stop); dropped rather than sent late, since "watching now" goes stale.</summary>
    Scrobble,
}

/// <summary>
/// A change waiting to go to Trakt. Sent straight away by the outbox worker and kept, with its attempts and next
/// try, while Trakt or the network is down, so nothing is lost.
/// </summary>
public sealed class TraktOutboxEntry
{
    public long Id { get; set; }
    public required string ProfileId { get; set; }
    public TraktOutboxKind Kind { get; set; }

    /// <summary>The body for Trakt, as JSON; for a scrobble, its action ("start", "pause", "stop") is in <see cref="Action"/>.</summary>
    public required string Payload { get; set; }

    public string? Action { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public string? LastError { get; set; }
}

/// <summary>
/// A play on Trakt of a film or episode the server doesn't have yet, kept by its ids so it arrives already watched.
/// Applied (and removed) when the title is added, through the same paths as <see cref="WatchTombstone"/>.
/// </summary>
public sealed class TraktHistoryHold
{
    public long Id { get; set; }
    public required string ProfileId { get; set; }

    /// <summary><see cref="MediaKind.Movie"/> or <see cref="MediaKind.Episode"/>.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>The film's ids; for an episode, its show's.</summary>
    public string? TmdbId { get; set; }

    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    public DateTime WatchedAt { get; set; }
}
