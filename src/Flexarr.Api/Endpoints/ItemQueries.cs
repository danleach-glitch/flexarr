using Flexarr.Core.Media;

namespace Flexarr.Api.Endpoints;

public static class ItemQueries
{
    /// <summary>
    /// Projects items to summaries in SQL (no files or streams loaded). Seasons and episodes borrow artwork
    /// from their series when they have none of their own.
    /// </summary>
    /// <param name="query">The items.</param>
    /// <param name="profileId">Whose watch state to include.</param>
    public static IQueryable<ItemSummaryDto> Summaries(this IQueryable<MediaItem> query, string profileId) =>
        query.Select(i => new
            {
                Item = i,
                ChildCount = i.Children.Count,
                Best = i.Files.OrderByDescending(f => f.Height).Select(f => new { f.DurationSeconds, f.Width, f.Height }).FirstOrDefault(),
                Poster = i.PosterUrl != null || (i.Parent != null && (i.Parent.PosterUrl != null || (i.Parent.Parent != null && i.Parent.Parent.PosterUrl != null))),
                Backdrop = i.BackdropUrl != null || (i.Parent != null && (i.Parent.BackdropUrl != null || (i.Parent.Parent != null && i.Parent.Parent.BackdropUrl != null))),
                Logo = i.LogoUrl != null || (i.Parent != null && (i.Parent.LogoUrl != null || (i.Parent.Parent != null && i.Parent.Parent.LogoUrl != null))),
                Accent = i.AccentColor ?? (i.Parent != null ? i.Parent.AccentColor ?? (i.Parent.Parent != null ? i.Parent.Parent.AccentColor : null) : null),
                // The show for episodes and seasons; the artist for tracks and albums.
                SeriesTitle = i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track ? i.Parent!.Parent!.Title
                    : i.Kind == MediaKind.Season || i.Kind == MediaKind.Album ? i.Parent!.Title : null,
                SeriesId = i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track ? i.Parent!.ParentId
                    : i.Kind == MediaKind.Season || i.Kind == MediaKind.Album ? i.ParentId : null,
                State = i.PlaybackStates.Where(p => p.UserId == profileId)
                    .Select(p => new { p.PositionSeconds, p.Watched, p.PlayCount, p.LastPlayedAt }).FirstOrDefault(),
                Unwatched = i.Kind == MediaKind.Series
                    ? i.Children.SelectMany(s => s.Children).Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
                    : i.Kind == MediaKind.Season
                        ? i.Children.Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
                        : (int?)null,
            })
            .Select(x => new ItemSummaryDto(
                x.Item.Id, x.Item.LibraryId, x.Item.ParentId, x.Item.Kind, x.Item.Title, x.Item.Year, x.Item.SeasonNumber, x.Item.EpisodeNumber,
                x.Item.EpisodeNumberEnd, x.Item.AirDate, x.ChildCount,
                x.Best == null ? null : x.Best.DurationSeconds,
                x.Best == null ? null : Resolution.Label(x.Best.Width, x.Best.Height),
                x.Item.AddedAt,
                ArtworkUrls.For(x.Item.Id, x.Item.MetadataUpdatedAt == null ? 0 : x.Item.MetadataUpdatedAt.Value.Ticks / 10_000_000,
                    x.Poster, x.Backdrop, x.Logo, x.Item.ThumbUrl != null),
                x.Accent,
                x.Item.CommunityRating,
                x.Item.CriticRating,
                x.Item.ContentRating,
                x.Item.RuntimeMinutes,
                x.Item.ReleaseDate,
                new UserDataDto(
                    x.State == null ? 0 : x.State.PositionSeconds,
                    x.Unwatched != null ? x.Unwatched == 0 : x.State != null && x.State.Watched,
                    x.State == null ? 0 : x.State.PlayCount,
                    x.State == null ? null : x.State.LastPlayedAt,
                    x.Unwatched),
                x.SeriesTitle,
                x.Item.DiscNumber,
                x.Item.TrackNumber,
                x.SeriesId));
}
