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

    /// <summary>Preferred audio/subtitle languages carry over between episodes of a series.</summary>
    public string? AudioLanguage { get; set; }
    public string? SubtitleLanguage { get; set; }
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
