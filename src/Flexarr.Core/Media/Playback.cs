namespace Flexarr.Core.Media;

/// <summary>Where someone is in an item, and whether they've seen it. One row per user and playable item.</summary>
public sealed class PlaybackState
{
    /// <summary>The main profile, which always exists (see <see cref="Profile"/>).</summary>
    public const string DefaultUser = "owner";

    public string UserId { get; set; } = DefaultUser;
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public double PositionSeconds { get; set; }
    public double? DurationSeconds { get; set; }
    public bool Watched { get; set; }
    public int PlayCount { get; set; }
    public DateTime LastPlayedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// When a book was last finished. Kept when it's listened to again, so finished books stay counted; null for
    /// anything but books, and for books never finished.
    /// </summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>The device that last recorded a place in a book, so another can offer to jump there. Books only.</summary>
    public string? Device { get; set; }

    /// <summary>Preferred audio/subtitle languages carry over between episodes of a series.</summary>
    public string? AudioLanguage { get; set; }
    public string? SubtitleLanguage { get; set; }

    /// <summary>Seconds this item's subtitles are shown later (or, negative, earlier) to match the speech.</summary>
    public double SubtitleDelaySeconds { get; set; }
}

public enum MarkerType
{
    Intro,
    Recap,
    Credits,
}

/// <summary>A skippable segment of a file: an intro, a recap, or the end credits.</summary>
public sealed class MediaMarker
{
    public int Id { get; set; }
    public Guid MediaFileId { get; set; }
    public MarkerType Type { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }

    /// <summary>"chapters", "fingerprint" or "blackframes".</summary>
    public required string Source { get; set; }
}

/// <summary>The playback speed a profile watches a show at, kept for its next episodes. One row per profile and show.</summary>
public sealed class ShowPlaybackSpeed
{
    public string ProfileId { get; set; } = PlaybackState.DefaultUser;
    public Guid SeriesId { get; set; }
    public double Speed { get; set; } = 1;
}

/// <summary>
/// A show taken out of someone's Continue Watching row: its next episode stays out until they play (or mark) an
/// episode of it after <see cref="DismissedAt"/>. One row per profile and show.
/// </summary>
public sealed class ContinueWatchingDismissal
{
    public string ProfileId { get; set; } = PlaybackState.DefaultUser;
    public Guid SeriesId { get; set; }
    public DateTime DismissedAt { get; set; } = DateTime.UtcNow;
}
