using Flexarr.Core.Media;

namespace Flexarr.Metadata;

/// <summary>Keeping what was edited (or chosen) by hand through a refresh.</summary>
public sealed partial class MetadataRefresher
{
    /// <summary>
    /// The fields a hand edit locks (<see cref="MediaItem.DetailsLocked"/>) and the cover chosen by hand
    /// (<see cref="MediaItem.ArtworkLocked"/>), as they were before a refresh. Everything else a refresh brings (ids,
    /// artwork, ratings, cast, seasons and episodes) still comes in.
    /// </summary>
    private sealed record HandEdits(
        bool Details, string Title, string SortTitle, string? OriginalTitle, int? Year, string? Overview, string? Studio, string? AlbumType, List<string> Genres,
        bool Artwork, string? PosterUrl, string? AccentColor);

    private static HandEdits? Keep(MediaItem item) => item.DetailsLocked || item.ArtworkLocked
        ? new HandEdits(item.DetailsLocked, item.Title, item.SortTitle, item.OriginalTitle, item.Year, item.Overview, item.Studio, item.AlbumType,
            [.. item.Genres.Select(g => g.Name)], item.ArtworkLocked, item.PosterUrl, item.AccentColor)
        : null;

    /// <summary>Puts back what <see cref="Keep"/> saw. The item's genres must have been loaded with it.</summary>
    private async Task RestoreAsync(MediaItem item, HandEdits? kept, CancellationToken ct)
    {
        if (kept is null)
        {
            return;
        }

        if (kept.Details)
        {
            item.Title = kept.Title;
            item.SortTitle = kept.SortTitle;
            item.OriginalTitle = kept.OriginalTitle;
            item.Year = kept.Year;
            item.Overview = kept.Overview;
            item.Studio = kept.Studio;
            item.AlbumType = kept.AlbumType;
            if (!item.Genres.Select(g => g.Name).SequenceEqual(kept.Genres, StringComparer.OrdinalIgnoreCase))
            {
                await SetGenresAsync(item, kept.Genres, ct).ConfigureAwait(false);
            }
        }

        if (kept.Artwork)
        {
            item.PosterUrl = kept.PosterUrl;
            item.AccentColor = kept.AccentColor;
        }
    }
}
