namespace Flexarr.Core.Media;

/// <summary>
/// A film or show a profile wants to watch later, whether the library has it or not. Kept by its TMDB id (and the
/// library item once there is one), so it waits for titles still to arrive and survives one being removed and added
/// again. One row per profile and title.
/// </summary>
public sealed class WatchlistEntry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ProfileId { get; set; }

    /// <summary><see cref="MediaKind.Movie"/> or <see cref="MediaKind.Series"/>.</summary>
    public MediaKind Kind { get; set; }

    /// <summary>Null only for a library title TMDB doesn't know (a show only on TVDB, a home-made film).</summary>
    public int? TmdbId { get; set; }

    public string? TvdbId { get; set; }
    public string? ImdbId { get; set; }

    /// <summary>The title in the library, once it's there; cleared if it's removed, when the TMDB id finds it again.</summary>
    public Guid? MediaItemId { get; set; }

    /// <summary>What it was called when added, for titles not yet in the library.</summary>
    public required string Title { get; set; }

    public int? Year { get; set; }

    /// <summary>Artwork where it came from (TMDB, or the library title's own), shown while it isn't in the library.</summary>
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }

    /// <summary>When it comes out (a film's release, a show's first episode), if it hasn't yet; for "Coming".</summary>
    public DateOnly? ReleaseDate { get; set; }

    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public WatchlistSource Source { get; set; } = WatchlistSource.Flexarr;

    /// <summary>
    /// When it was found in the library (and its profile told, if it was added before it arrived). Set straight
    /// away for a title added when it's already there.
    /// </summary>
    public DateTime? ArrivedAt { get; set; }

    /// <summary>
    /// When it came off the list; null while it's on. Kept rather than deleted, so a sync elsewhere (Trakt) can be told,
    /// and adding it again brings this entry back.
    /// </summary>
    public DateTime? RemovedAt { get; set; }

    public WatchlistRemoval? RemovedReason { get; set; }
}

/// <summary>Why a title came off a watchlist.</summary>
public enum WatchlistRemoval
{
    /// <summary>Someone took it off.</summary>
    Removed,

    /// <summary>A film, watched.</summary>
    Watched,

    /// <summary>A show, started (Continue watching has it from there).</summary>
    Started,
}

/// <summary>Where a watchlist entry came from: only titles someone adds in Flexarr are ever requested for them.</summary>
public enum WatchlistSource
{
    Flexarr,
    Trakt,
}
