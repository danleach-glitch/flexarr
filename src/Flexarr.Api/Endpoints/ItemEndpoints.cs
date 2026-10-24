using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

public static class ItemEndpoints
{
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/libraries/{id:guid}/items", async (Guid id, string? search, int? skip, int? take, FlexarrDbContext db, CancellationToken ct) =>
        {
            var query = db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                // SQLite LIKE is case-insensitive for ASCII.
                var pattern = $"%{search.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
                query = query.Where(i => EF.Functions.Like(i.Title, pattern));
            }

            var total = await query.CountAsync(ct);
            var items = await Summaries(query.OrderBy(i => i.SortTitle).ThenBy(i => i.Year)
                .Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 100, 1, MaxPageSize))).ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Items");

        api.MapGet("/items/recent", async (int? take, FlexarrDbContext db, CancellationToken ct) =>
            await Summaries(db.MediaItems
                    .Where(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode)
                    .OrderByDescending(i => i.AddedAt)
                    .Take(Math.Clamp(take ?? 24, 1, 100)))
                .ToListAsync(ct))
            .WithTags("Items");

        api.MapGet("/items/{id:guid}", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
        {
            var item = await Summaries(db.MediaItems.Where(i => i.Id == id)).FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var entity = await db.MediaItems.AsNoTracking().FirstAsync(i => i.Id == id, ct);

            var ancestors = new List<ItemSummaryDto>();
            for (var parentId = entity.ParentId; parentId is { } pid;)
            {
                var parent = await Summaries(db.MediaItems.Where(i => i.Id == pid)).FirstAsync(ct);
                ancestors.Insert(0, parent);
                parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstAsync(ct);
            }

            var children = await Summaries(db.MediaItems.Where(i => i.ParentId == id).OrderBy(i => i.SortTitle)).ToListAsync(ct);
            var files = await db.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == id).Include(f => f.Streams)
                .OrderByDescending(f => f.Height).ToListAsync(ct);

            return Results.Ok(new ItemDetailDto(item, entity.Path, entity.TvdbId, entity.TmdbId, entity.ImdbId, ancestors, children,
                files.Select(ToDto).ToList()));
        }).WithTags("Items");

        return api;
    }

    /// <summary>Projects in SQL so lists don't load files and streams.</summary>
    private static IQueryable<ItemSummaryDto> Summaries(IQueryable<MediaItem> query) =>
        query.Select(i => new
            {
                Item = i,
                ChildCount = i.Children.Count,
                Best = i.Files.OrderByDescending(f => f.Height).Select(f => new { f.DurationSeconds, f.Width, f.Height }).FirstOrDefault(),
            })
            .Select(x => new ItemSummaryDto(
                x.Item.Id, x.Item.LibraryId, x.Item.Kind, x.Item.Title, x.Item.Year, x.Item.SeasonNumber, x.Item.EpisodeNumber,
                x.Item.EpisodeNumberEnd, x.Item.AirDate, x.ChildCount,
                x.Best == null ? null : x.Best.DurationSeconds,
                x.Best == null ? null : Resolution.Label(x.Best.Width, x.Best.Height),
                x.Item.AddedAt));

    private static FileDto ToDto(MediaFile f) => new(
        f.Id, f.Path, f.Size, f.Container, f.DurationSeconds, f.Bitrate, f.VideoCodec, f.Width, f.Height, f.VideoRange,
        f.AudioCodec, f.AudioChannels, f.ProbeError,
        f.Streams.OrderBy(s => s.Index)
            .Select(s => new StreamDto(s.Index, s.Type, s.Codec, s.Profile, s.Language, s.Title, s.IsDefault, s.IsForced, s.Width, s.Height, s.Channels))
            .ToList());
}
