using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public static class ItemEndpoints
{
    private const int MaxPageSize = 500;

    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/libraries/{id:guid}/items", async (Guid id, string? search, string? sort, string? genre, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
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
            var items = await ordered.Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 100, 1, MaxPageSize)).Summaries(profile.Id).ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Items");

        api.MapGet("/libraries/{id:guid}/genres", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
            await db.Genres
                .Select(g => new { g.Name, Count = g.Items.Count(i => i.LibraryId == id && i.ParentId == null) })
                .Where(g => g.Count > 0)
                .OrderByDescending(g => g.Count)
                .ToListAsync(ct))
            .WithTags("Items");

        api.MapGet("/items/recent", async (int? take, Guid? libraryId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.MediaItems
                .Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode) && (libraryId == null || i.LibraryId == libraryId))
                .OrderByDescending(i => i.AddedAt)
                .Take(Math.Clamp(take ?? 24, 1, 100))
                .Summaries(profile.Id)
                .ToListAsync(ct))
            .WithTags("Items");

        // includeSeason (the web's title page): a series also carries its Up Next episode and one season's episodes (the
        // requested number, else the first unwatched), so the page needs one request rather than three in a row.
        api.MapGet("/items/{id:guid}", async (Guid id, bool? includeSeason, int? season, FlexarrDbContext db, PlaybackService playback,
            CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var bazarr = options.CurrentValue.Bazarr?.Instance is not null;
            var detail = await DetailAsync(id, db, profile, bazarr, ct);
            if (detail is null)
            {
                return Results.NotFound();
            }

            if (includeSeason == true && detail.Item.Kind == MediaKind.Series)
            {
                var nextUp = await playback.NextUpAsync(id, ct) is { } episode
                    ? await db.MediaItems.Where(i => i.Id == episode.Id).Summaries(profile.Id).FirstAsync(ct)
                    : null;
                // The same choice the page makes: specials last, then the requested season, the first unwatched, or the first.
                var seasons = detail.Children.OrderBy(s => s.SeasonNumber == 0).ThenBy(s => s.SeasonNumber).ToList();
                var selected = seasons.FirstOrDefault(s => season is { } n && s.SeasonNumber == n)
                    ?? seasons.FirstOrDefault(s => !s.UserData.Watched && s.SeasonNumber != 0)
                    ?? seasons.FirstOrDefault();
                var seasonDetail = selected is null ? null : await DetailAsync(selected.Id, db, profile, bazarr, ct);
                detail = detail with { NextUp = nextUp, Season = seasonDetail };
            }

            return Results.Ok(detail);
        }).WithTags("Items");

        // Everything an artist (or one album) has, in the order a record collection plays: albums oldest first, then
        // disc and track. One request, so the web's Play and Shuffle buttons don't fetch each album in turn.
        api.MapGet("/items/{id:guid}/tracks", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var kind = await db.MediaItems.Where(i => i.Id == id).Select(i => (MediaKind?)i.Kind).FirstOrDefaultAsync(ct);
            if (kind is not (MediaKind.Artist or MediaKind.Album))
            {
                return Results.NotFound();
            }

            var tracks = db.MediaItems.Where(i => i.Kind == MediaKind.Track && (i.ParentId == id || i.Parent!.ParentId == id));
            return Results.Ok(await tracks
                .OrderBy(t => t.Parent!.Year == null).ThenBy(t => t.Parent!.Year).ThenBy(t => t.Parent!.SortTitle)
                .ThenBy(t => t.SortTitle)
                .Summaries(profile.Id).ToListAsync(ct));
        }).WithTags("Items");

        return api;
    }

    /// <param name="bazarr">Whether Bazarr is set up, so films and episodes Sonarr and Radarr manage can have subtitles found.</param>
    private static async Task<ItemDetailDto?> DetailAsync(Guid id, FlexarrDbContext db, CurrentProfile profile, bool bazarr, CancellationToken ct)
    {
        var item = await db.MediaItems.Where(i => i.Id == id).Summaries(profile.Id).FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return null;
        }

        if (await db.MediaItems.AsNoTracking().Include(i => i.Genres).FirstOrDefaultAsync(i => i.Id == id, ct) is not { } entity)
        {
            return null;
        }

        var ancestors = new List<ItemSummaryDto>();
        for (var parentId = entity.ParentId; parentId is { } pid;)
        {
            // A scan may take a parent away mid-request: the trail stops there instead of failing.
            if (await db.MediaItems.Where(i => i.Id == pid).Summaries(profile.Id).FirstOrDefaultAsync(ct) is not { } parent)
            {
                break;
            }

            ancestors.Insert(0, parent);
            parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
        }

        // An artist's albums read newest first; everything else in its natural order.
        var childQuery = db.MediaItems.Where(i => i.ParentId == id);
        var children = await (entity.Kind == MediaKind.Artist
                ? childQuery.OrderByDescending(i => i.Year).ThenBy(i => i.SortTitle)
                : childQuery.OrderBy(i => i.SortTitle))
            .Summaries(profile.Id).ToListAsync(ct);
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

        // A season's episode list shows overviews; send them with it rather than one request per episode.
        var childOverviews = entity.Kind == MediaKind.Season
            ? await db.MediaItems.Where(i => i.ParentId == id).Select(i => new { i.Id, i.Overview }).ToDictionaryAsync(i => i.Id, i => i.Overview, ct)
            : new Dictionary<Guid, string?>();

        return new ItemDetailDto(
            item, entity.Path, entity.OriginalTitle, entity.Overview ?? (entity.Kind == MediaKind.Season ? series.Overview : null),
            entity.Studio ?? series.Studio, series.Status, entity.TrailerYouTubeId,
            series.Genres.Select(g => g.Name).Order().ToList(),
            series.TvdbId, entity.TmdbId ?? series.TmdbId, entity.ImdbId ?? series.ImdbId,
            ancestors, children, credits, files.Select(ToDto).ToList(), childOverviews, entity.MatchLocked,
            CanSearchSubtitles: bazarr && entity.Kind is (MediaKind.Movie or MediaKind.Episode) && entity.ArrId is not null && files.Count > 0);
    }

    private static FileDto ToDto(MediaFile f) => new(
        f.Id, f.Path, f.Size, f.Container, f.DurationSeconds, f.Bitrate, f.VideoCodec, f.Width, f.Height, f.VideoRange,
        f.AudioCodec, f.AudioChannels, f.ProbeError,
        f.Streams.OrderBy(s => s.Index)
            .Select(s => new StreamDto(s.Index, s.Type, s.Codec, s.Profile, s.Language, s.Title, s.IsDefault, s.IsForced, s.Width, s.Height, s.Channels))
            .ToList(),
        f.Hdr10Plus == true);
}
