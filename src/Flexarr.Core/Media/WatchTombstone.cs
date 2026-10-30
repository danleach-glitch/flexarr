namespace Flexarr.Core.Media;

/// <summary>
/// What's left of a film, show or episode removed after its files stayed missing past the grace period: who had
/// watched it, how far, and which collections held it, by its TMDB/TVDB/IMDb ids. When the title is added again (a
/// renamed folder, a drive away for days) it picks this back up. Kept for <see cref="Lifetime"/>.
/// </summary>
public sealed class WatchTombstone
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    public int Id { get; set; }

    /// <summary>Movie, Series (for its collections) or Episode.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>The title's ids; for an episode, its show's.</summary>
    public string? TmdbId { get; set; }

    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }
    public DateOnly? AirDate { get; set; }

    public DateTime RemovedAt { get; set; } = DateTime.UtcNow;

    public List<TombstonePlayback> Playback { get; set; } = [];

    public List<TombstoneCollection> Collections { get; set; } = [];

    /// <summary>A parent's Allow or Block on the film or show, for profiles with an age limit.</summary>
    public KidsOverride KidsOverride { get; set; }
}

/// <summary>A <see cref="PlaybackState"/> as it was.</summary>
public sealed record TombstonePlayback(
    string UserId,
    double PositionSeconds,
    double? DurationSeconds,
    bool Watched,
    int PlayCount,
    DateTime LastPlayedAt,
    string? AudioLanguage,
    string? SubtitleLanguage);

public sealed record TombstoneCollection(Guid CollectionId, int Position);
