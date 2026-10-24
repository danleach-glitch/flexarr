namespace Flexarr.Core.Media;

public enum MediaKind
{
    Movie,
    Series,
    Season,
    Episode,
}

/// <summary>
/// A browsable thing: a movie, or a node in the series → season → episode tree.
/// Playable items (movies, episodes) own one or more <see cref="MediaFile"/>s.
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

    public string? TvdbId { get; set; }
    public string? TmdbId { get; set; }
    public string? ImdbId { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public List<MediaFile> Files { get; set; } = [];
}
