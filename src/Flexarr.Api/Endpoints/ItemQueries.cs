using System.Linq.Expressions;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

public static class ItemQueries
{
    /// <summary>
    /// A linked title once: of the copies this profile can see (see docs/multi-versions.md), the one with the lowest
    /// id. For lists across libraries. A library's own page lists all it holds, or a title kept under another library
    /// would be missing from it.
    /// </summary>
    public static IQueryable<MediaItem> OnePerTitle(this IQueryable<MediaItem> query) =>
        query.Where(i => i.LinkGroupId == null || !i.LinkGroup!.Items.Any(o => o.Id.CompareTo(i.Id) < 0));

    /// <summary>
    /// As <see cref="OnePerTitle"/>, for a search or filter: the lowest of the copies that match. A title whose lowest copy
    /// happens not to match (its metadata differs from the other's) still has to turn up, as the copy that does.
    /// </summary>
    public static IQueryable<MediaItem> OnePerMatch(this IQueryable<MediaItem> query, Expression<Func<MediaItem, bool>> match) =>
        query.Where(match).Where(i => i.LinkGroupId == null || !i.LinkGroup!.Items.AsQueryable().Where(match).Any(o => o.Id.CompareTo(i.Id) < 0));

    /// <summary>
    /// What's under a parent. For a linked show or season that's what's under all the copies this profile can see, each
    /// title once, so a page shows the seasons and episodes of both libraries.
    /// </summary>
    public static IQueryable<MediaItem> ChildrenOf(this IQueryable<MediaItem> items, Guid parentId, Guid? linkGroupId) =>
        linkGroupId is null ? items.Where(i => i.ParentId == parentId) : items.Where(i => i.Parent!.LinkGroupId == linkGroupId).OnePerTitle();

    /// <summary>A show or season is watched when none of it is left. Worked out here: in the query, it would count the episodes again.</summary>
    private static UserDataDto UserData(double position, bool watched, int playCount, DateTime? lastPlayed, int? unwatched) =>
        new(position, unwatched is { } left ? left == 0 : watched, playCount, lastPlayed, unwatched);

    /// <summary>These ids with later copies of a linked title left out, in their order: for lists already ranked in memory.</summary>
    public static async Task<List<Guid>> OnePerTitleAsync(FlexarrDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var groups = await db.MediaItems.Where(i => ids.Contains(i.Id) && i.LinkGroupId != null)
            .Select(i => new { i.Id, Group = i.LinkGroupId!.Value }).ToDictionaryAsync(i => i.Id, i => i.Group, ct);
        var seen = new HashSet<Guid>();
        return [.. ids.Where(id => !groups.TryGetValue(id, out var group) || seen.Add(group))];
    }

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
                // A linked parent is reported as the copy its page lists (OnePerTitle), since apps look for it there:
                // an episode of another copy still has to light up its season.
                ParentId = i.Parent != null && i.Parent.LinkGroupId != null
                    ? i.Parent.LinkGroup!.Items.OrderBy(o => o.Id).Select(o => (Guid?)o.Id).FirstOrDefault() ?? i.ParentId
                    : i.ParentId,
                // A linked title counts across its copies, each child once.
                ChildCount = i.LinkGroupId == null ? i.Children.Count
                    : i.LinkGroup!.Items.SelectMany(c => c.Children).Select(c => c.LinkGroupId ?? c.Id).Distinct().Count(),
                Best = i.Files.OrderByDescending(f => f.Height).Select(f => new { f.DurationSeconds, f.Width, f.Height }).FirstOrDefault(),
                Poster = i.PosterUrl != null || (i.Parent != null && (i.Parent.PosterUrl != null || (i.Parent.Parent != null && i.Parent.Parent.PosterUrl != null))),
                Backdrop = i.BackdropUrl != null || (i.Parent != null && (i.Parent.BackdropUrl != null || (i.Parent.Parent != null && i.Parent.Parent.BackdropUrl != null))),
                Logo = i.LogoUrl != null || (i.Parent != null && (i.Parent.LogoUrl != null || (i.Parent.Parent != null && i.Parent.Parent.LogoUrl != null))),
                Accent = i.AccentColor ?? (i.Parent != null ? i.Parent.AccentColor ?? (i.Parent.Parent != null ? i.Parent.Parent.AccentColor : null) : null),
                // The show for episodes and seasons; the artist for tracks and albums; the podcast for its episodes.
                SeriesTitle = i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track ? i.Parent!.Parent!.Title
                    : i.Kind == MediaKind.Season || i.Kind == MediaKind.Album || i.Kind == MediaKind.PodcastEpisode ? i.Parent!.Title : null,
                SeriesId = i.Kind == MediaKind.Episode || i.Kind == MediaKind.Track ? i.Parent!.ParentId
                    : i.Kind == MediaKind.Season || i.Kind == MediaKind.Album || i.Kind == MediaKind.PodcastEpisode ? i.ParentId : null,
                PodcastDuration = i.Enclosure != null ? i.Enclosure.DurationSeconds : null,
                // A book's smallest series (the sub-series rather than its universe), its name and the book's number in
                // it read as two single values, for books only. Read as one row of two inside Book below, the database
                // numbered every book's series entries before picking this book's, once for each place Book is used,
                // in every list of anything: most of the time a row of films took once a library had audiobooks.
                SeriesName = i.Kind != MediaKind.Audiobook ? null
                    : i.SeriesEntries.OrderBy(e => e.Series!.Entries.Count).Select(e => e.Series!.Name).FirstOrDefault(),
                SeriesSequence = i.Kind != MediaKind.Audiobook ? null
                    : i.SeriesEntries.OrderBy(e => e.Series!.Entries.Count).Select(e => e.Sequence).FirstOrDefault(),
                // A book's: its authors (two at most), first narrator, and its whole length.
                Book = i.Kind != MediaKind.Audiobook ? null : new
                {
                    Author1 = i.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Select(c => c.Person!.Name).FirstOrDefault(),
                    Author2 = i.Credits.Where(c => c.Role == CreditRole.Author).OrderBy(c => c.Order).Skip(1).Select(c => c.Person!.Name).FirstOrDefault(),
                    Authors = i.Credits.Count(c => c.Role == CreditRole.Author),
                    Narrator = i.Credits.Where(c => c.Role == CreditRole.Narrator).OrderBy(c => c.Order).Select(c => c.Person!.Name).FirstOrDefault(),
                    Duration = i.Book != null ? i.Book.DurationSeconds : 0,
                    FinishedAt = i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.FinishedAt).FirstOrDefault(),
                },
                // Part of a home video library: its folders and videos are kept by their paths, a show's seasons and
                // episodes never are. They're shown by name, not as S01E02.
                HomeVideo = i.Kind == MediaKind.Series ? i.Children.Any(s => s.Path != null)
                    : (i.Kind == MediaKind.Season || i.Kind == MediaKind.Episode) && i.Path != null,
                State = i.PlaybackStates.Where(p => p.UserId == profileId)
                    .Select(p => new { p.PositionSeconds, p.Watched, p.PlayCount, p.LastPlayedAt }).FirstOrDefault(),
                // Linked shows and seasons across their copies, each episode once. Unlinked ones, nearly all, don't go there.
                Unwatched = i.Kind == MediaKind.Series
                    ? i.LinkGroupId == null
                        ? i.Children.SelectMany(s => s.Children).Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
                        : i.LinkGroup!.Items.SelectMany(c => c.Children).SelectMany(s => s.Children)
                            .Where(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)).Select(e => e.LinkGroupId ?? e.Id).Distinct().Count()
                    : i.Kind == MediaKind.Season
                        ? i.LinkGroupId == null
                            ? i.Children.Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
                            : i.LinkGroup!.Items.SelectMany(c => c.Children)
                                .Where(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)).Select(e => e.LinkGroupId ?? e.Id).Distinct().Count()
                        : (int?)null,
            })
            .Select(x => new ItemSummaryDto(
                x.Item.Id, x.Item.LibraryId, x.ParentId, x.Item.Kind, x.Item.Title, x.Item.Year, x.Item.SeasonNumber, x.Item.EpisodeNumber,
                x.Item.EpisodeNumberEnd, x.Item.AirDate, x.ChildCount,
                x.Book != null ? x.Book.Duration : x.Best == null ? x.PodcastDuration : x.Best.DurationSeconds,
                x.Best == null ? null : Resolution.Label(x.Best.Width, x.Best.Height),
                x.Item.AddedAt,
                ArtworkUrls.For(x.Item.Id, ArtworkUrls.Version(x.Item.MetadataUpdatedAt, x.Item.ArtworkChangedAt),
                    x.Poster, x.Backdrop, x.Logo, x.Item.ThumbUrl != null),
                x.Accent,
                x.Item.CommunityRating,
                x.Item.CriticRating,
                x.Item.ContentRating,
                x.Item.RuntimeMinutes,
                x.Item.ReleaseDate,
                UserData(
                    x.State == null ? 0 : x.State.PositionSeconds,
                    x.State != null && x.State.Watched,
                    x.State == null ? 0 : x.State.PlayCount,
                    x.State == null ? null : x.State.LastPlayedAt,
                    x.Unwatched),
                x.SeriesTitle,
                x.Item.DiscNumber,
                x.Item.TrackNumber,
                x.SeriesId,
                x.Item.AlbumType,
                x.Item.TrackArtist,
                x.HomeVideo,
                x.Book == null ? null : new Books.BookSummaryDto(
                    x.Book.Author2 == null ? x.Book.Author1 : x.Book.Author1 + ", " + x.Book.Author2, x.Book.Authors, x.Book.Narrator,
                    x.SeriesName, x.SeriesSequence,
                    x.Book.Duration, x.Book.FinishedAt),
                x.Item.ImdbRating,
                RottenTomatoesDto.Of(x.Item.CriticRating, x.Item.CriticCertified)));
}
