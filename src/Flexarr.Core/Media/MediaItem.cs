namespace Flexarr.Core.Media;

public enum MediaKind
{
    Movie,
    Series,
    Season,
    Episode,
    Artist,
    Album,
    Track,
    Podcast,
    PodcastEpisode,
}

/// <summary>
/// A browsable thing: a movie, or a node in the series → season → episode, artist → album → track or
/// podcast → podcast episode tree. Playable items (movies, episodes, tracks) own one or more <see cref="MediaFile"/>s;
/// a podcast episode is played from its feed (see <see cref="Podcasts.PodcastEpisode"/>).
/// </summary>
public sealed class MediaItem
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public Guid? ParentId { get; set; }
    public MediaItem? Parent { get; set; }
    public List<MediaItem> Children { get; set; } = [];

    public MediaKind Kind { get; set; }
    public required string Title { get; set; }
    public required string SortTitle { get; set; }
    public int? Year { get; set; }

    /// <summary>Folder on disk for movies and series; identifies the item across rescans.</summary>
    public string? Path { get; set; }

    public int? SeasonNumber { get; set; }
    public int? EpisodeNumber { get; set; }

    /// <summary>Last episode in a multi-episode file (S01E01-E02 → 2).</summary>
    public int? EpisodeNumberEnd { get; set; }

    /// <summary>Daily shows are identified by air date instead of episode number.</summary>
    public DateOnly? AirDate { get; set; }

    public int? DiscNumber { get; set; }
    public int? TrackNumber { get; set; }

    /// <summary>
    /// A track's own artist when it isn't the album's (a compilation's tracks, a featured artist); null when they're
    /// the same. Tracks only.
    /// </summary>
    public string? TrackArtist { get; set; }

    /// <summary>MusicBrainz release id for albums, artist id for artists.</summary>
    public string? MusicBrainzId { get; set; }

    /// <summary>
    /// What sort of release an album is: one of <see cref="AlbumKind"/>'s values. Lidarr tells us; tags rarely say,
    /// so the scanner leaves it null, and an album without one is taken to be a plain album.
    /// </summary>
    public string? AlbumType { get; set; }

    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public List<MediaFile> Files { get; set; } = [];

    /// <summary>
    /// When all of this item's files (or children) went missing from disk. Missing items are hidden everywhere but
    /// kept, with their watch history and collections, so they come back as they were if the files reappear
    /// (a drive that hadn't mounted, a file mid-upgrade); the scanner removes them after a grace period.
    /// </summary>
    public DateTime? MissingSince { get; set; }

    /// <summary>
    /// When this missing item's watch history and collections were handed to the title that replaced it (see
    /// <see cref="Scanning.WatchHistoryCarryOver"/>). It isn't handed over again unless it comes back and goes missing anew.
    /// </summary>
    public DateTime? HandedOverAt { get; set; }

    // ---- Metadata (filled by metadata providers; null until then) ----

    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }
    public string? ContentRating { get; set; }

    /// <summary>Studio for movies, network for TV.</summary>
    public string? Studio { get; set; }

    public int? RuntimeMinutes { get; set; }
    public string? Status { get; set; }
    public DateOnly? ReleaseDate { get; set; }

    /// <summary>Audience rating out of 10 (IMDb/TMDB/TVDB).</summary>
    public double? CommunityRating { get; set; }

    /// <summary>Critics' score out of 100 (Rotten Tomatoes).</summary>
    public int? CriticRating { get; set; }

    public double? Popularity { get; set; }
    public string? TrailerYouTubeId { get; set; }

    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? LogoUrl { get; set; }

    /// <summary>Episode still.</summary>
    public string? ThumbUrl { get; set; }

    /// <summary>Vivid colour sampled from the artwork, as #rrggbb, for ambient UI tinting.</summary>
    public string? AccentColor { get; set; }

    /// <summary>Id of this item in Radarr/Sonarr, for deep links and actions.</summary>
    public int? ArrId { get; set; }

    public string? TvMazeId { get; set; }

    /// <summary>
    /// Matched by hand (Fix match): refreshes keep the chosen TVDB id (shows) or TMDB id (films) instead of matching
    /// by folder or title again.
    /// </summary>
    public bool MatchLocked { get; set; }

    public string? MetadataSource { get; set; }
    public DateTime? MetadataUpdatedAt { get; set; }

    public List<Genre> Genres { get; set; } = [];
    public List<Credit> Credits { get; set; } = [];
    public List<PlaybackState> PlaybackStates { get; set; } = [];

    /// <summary>A podcast's feed, for <see cref="MediaKind.Podcast"/>.</summary>
    public Podcasts.PodcastFeed? Feed { get; set; }

    /// <summary>Where a <see cref="MediaKind.PodcastEpisode"/>'s sound is, and what its feed says about it.</summary>
    public Podcasts.PodcastEpisode? Enclosure { get; set; }
}
