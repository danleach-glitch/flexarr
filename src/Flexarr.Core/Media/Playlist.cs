namespace Flexarr.Core.Media;

/// <summary>
/// A profile's own list of tracks, in the order they chose. Unlike collections there are no household playlists:
/// what you listen to is yours, as on Spotify.
/// </summary>
public sealed class Playlist
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ProfileId { get; set; }
    public required string Name { get; set; }

    /// <summary>An album (or track) whose cover stands for the playlist; null shows a mosaic of its tracks' covers.</summary>
    public Guid? CoverItemId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<PlaylistItem> Items { get; set; } = [];
}

/// <summary>
/// One place in a playlist. A track may be in the same playlist more than once (the entry, not the track, is what's
/// moved or removed), so entries have ids of their own.
/// </summary>
public sealed class PlaylistItem
{
    public int Id { get; set; }
    public Guid PlaylistId { get; set; }
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Order within the playlist, from 0; kept without gaps.</summary>
    public int Position { get; set; }
}
