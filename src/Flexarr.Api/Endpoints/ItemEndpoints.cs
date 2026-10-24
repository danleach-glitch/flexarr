using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

public static class ItemEndpoints
{
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/libraries/{id:guid}/items", async (Guid id, string? search, string? sort, string? genre, int? skip, int? take, FlexarrDbContext db, CancellationToken ct) =>
        {
            var query = db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                // SQLite LIKE is case-insensitive for ASCII.
                var pattern = $"%{search.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
                query = query.Where(i => EF.Functions.Like(i.Title, pattern));
            }

            if (!string.IsNullOrWhiteSpace(genre))
            {
                query = query.Where(i => i.Genres.Any(g => g.Name == genre));
            }

            var total = await query.CountAsync(ct);
            var ordered = sort switch
            {
                "added" => query.OrderByDescending(i => i.AddedAt),
                "year" => query.OrderByDescending(i => i.Year).ThenBy(i => i.SortTitle),
                "rating" => query.OrderByDescending(i => i.CommunityRating).ThenBy(i => i.SortTitle),
                "runtime" => query.OrderBy(i => i.RuntimeMinutes).ThenBy(i => i.SortTitle),
                _ => query.OrderBy(i => i.SortTitle).ThenBy(i => i.Year),
            };
            var items = await ordered.Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 100, 1, MaxPageSize)).Summaries().ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Items");

        api.MapGet("/libraries/{id:guid}/genres", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
            await db.Genres
                .Select(g => new { g.Name, Count = g.Items.Count(i => i.LibraryId == id && i.ParentId == null) })
                .Where(g => g.Count > 0)
                .OrderByDescending(g => g.Count)
                .ToListAsync(ct))
            .WithTags("Items");

        api.MapGet("/items/recent", async (int? take, Guid? libraryId, FlexarrDbContext db, CancellationToken ct) =>
            await db.MediaItems
                .Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode) && (libraryId == null || i.LibraryId == libraryId))
                .OrderByDescending(i => i.AddedAt)
                .Take(Math.Clamp(take ?? 24, 1, 100))
                .Summaries()
                .ToListAsync(ct))
            .WithTags("Items");

        api.MapGet("/items/{id:guid}", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Where(i => i.Id == id).Summaries().FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var entity = await db.MediaItems.AsNoTracking().Include(i => i.Genres).FirstAsync(i => i.Id == id, ct);

            var ancestors = new List<ItemSummaryDto>();
            for (var parentId = entity.ParentId; parentId is { } pid;)
            {
                ancestors.Insert(0, await db.MediaItems.Where(i => i.Id == pid).Summaries().FirstAsync(ct));
                parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstAsync(ct);
            }

            var children = await db.MediaItems.Where(i => i.ParentId == id).OrderBy(i => i.SortTitle).Summaries().ToListAsync(ct);
            var files = await db.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == id).Include(f => f.Streams)
                .OrderByDescending(f => f.Height).ToListAsync(ct);

            // Seasons and episodes show their series' cast.
            var creditsFor = entity.Kind is MediaKind.Season or MediaKind.Episode && ancestors.Count > 0 ? ancestors[0].Id : id;
            var credits = await db.Credits.Where(c => c.MediaItemId == creditsFor).OrderBy(c => c.Order)
                .Select(c => new CreditDto(c.PersonId, c.Person!.Name, c.Role, c.Character, ArtworkUrls.Person(c.PersonId, c.Person.ImageUrl != null)))
                .ToListAsync(ct);

            var series = entity.Kind is MediaKind.Season or MediaKind.Episode && ancestors.Count > 0
                ? await db.MediaItems.AsNoTracking().Include(i => i.Genres).FirstAsync(i => i.Id == ancestors[0].Id, ct)
                : entity;

            return Results.Ok(new ItemDetailDto(
                item, entity.Path, entity.OriginalTitle, entity.Overview ?? (entity.Kind == MediaKind.Season ? series.Overview : null),
                entity.Studio ?? series.Studio, series.Status, entity.TrailerYouTubeId,
                series.Genres.Select(g => g.Name).Order().ToList(),
                series.TvdbId, entity.TmdbId ?? series.TmdbId, entity.ImdbId ?? series.ImdbId,
                ancestors, children, credits, files.Select(ToDto).ToList()));
        }).WithTags("Items");

        return api;
    }

    private static FileDto ToDto(MediaFile f) => new(
        f.Id, f.Path, f.Size, f.Container, f.DurationSeconds, f.Bitrate, f.VideoCodec, f.Width, f.Height, f.VideoRange,
        f.AudioCodec, f.AudioChannels, f.ProbeError,
        f.Streams.OrderBy(s => s.Index)
            .Select(s => new StreamDto(s.Index, s.Type, s.Codec, s.Profile, s.Language, s.Title, s.IsDefault, s.IsForced, s.Width, s.Height, s.Channels))
            .ToList());
}
