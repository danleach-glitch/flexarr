using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record HealthFileDto(Guid Id, string Path, long Size, string? Resolution, string? VideoCodec, long? Bitrate, bool Keep);

public sealed record DuplicateDto(ItemSummaryDto Item, string? SeriesTitle, IReadOnlyList<HealthFileDto> Files, long ReclaimableBytes);

public sealed record UnreadableDto(Guid FileId, string Path, string Error, ItemSummaryDto? Item);

public sealed record LibraryHealthDto(
    IReadOnlyList<DuplicateDto> Duplicates,
    long ReclaimableBytes,
    IReadOnlyList<UnreadableDto> Unreadable,
    IReadOnlyList<ItemSummaryDto> MissingArtwork,
    IReadOnlyList<ItemSummaryDto> StandardDefinition,
    int StandardDefinitionTotal);

public sealed record FileProblemReportDto(FileProblemDto File, ItemSummaryDto? Item);

public sealed record HealthCheckReportDto(bool Running, int Checked, int Total, DateTime? FinishedAt, IReadOnlyList<FileProblemReportDto> Problems);

public static class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/library-health", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            // Duplicates: more than one file for one movie or episode. The best copy (resolution, then bitrate) is kept.
            var files = await db.MediaFiles.AsNoTracking()
                .Where(f => f.MediaItem!.Files.Count > 1 && (f.MediaItem.Kind == MediaKind.Movie || f.MediaItem.Kind == MediaKind.Episode))
                .Select(f => new { f.Id, f.MediaItemId, f.Path, f.Size, f.Width, f.Height, f.VideoCodec, f.Bitrate })
                .ToListAsync(ct);
            var groups = files.GroupBy(f => f.MediaItemId).ToList();
            var ids = groups.Select(g => g.Key).ToList();
            var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

            var duplicates = groups.Select(g =>
                {
                    var ranked = g.OrderByDescending(f => Math.Max(f.Height ?? 0, (f.Width ?? 0) * 9 / 16)).ThenByDescending(f => f.Bitrate ?? 0).ThenByDescending(f => f.Size).ToList();
                    var list = ranked.Select((f, i) => new HealthFileDto(f.Id, f.Path, f.Size, Resolution.Label(f.Width, f.Height), f.VideoCodec, f.Bitrate, i == 0)).ToList();
                    var item = items[g.Key];
                    return new DuplicateDto(item, item.SeriesTitle, list, list.Where(f => !f.Keep).Sum(f => f.Size));
                })
                .OrderByDescending(d => d.ReclaimableBytes)
                .ToList();

            var unreadable = await db.MediaFiles.AsNoTracking().Where(f => f.ProbeError != null)
                .Select(f => new { f.Id, f.Path, f.ProbeError, f.MediaItemId }).ToListAsync(ct);
            var unreadableItems = await db.MediaItems.Where(i => unreadable.Select(u => u.MediaItemId).Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);

            var missing = await db.MediaItems.Where(i => i.ParentId == null && i.PosterUrl == null).OrderBy(i => i.SortTitle).Take(100).Summaries(profile.Id).ToListAsync(ct);

            // Films and shows whose best copy is standard definition: candidates for an upgrade search.
            var sd = db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.Files.Any() && i.Files.Max(f => f.Height) < 700 && i.Files.Max(f => f.Width) < 1200);
            return new LibraryHealthDto(
                duplicates,
                duplicates.Sum(d => d.ReclaimableBytes),
                unreadable.Select(u => new UnreadableDto(u.Id, u.Path, u.ProbeError!, unreadableItems.GetValueOrDefault(u.MediaItemId))).ToList(),
                missing,
                await sd.OrderBy(i => i.SortTitle).Take(60).Summaries(profile.Id).ToListAsync(ct),
                await sd.CountAsync(ct));
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // The Health check button: look at every file on disk (in the background), then report what's wrong.
        api.MapPost("/library-health/check", (HealthCheck check) =>
        {
            check.Start();
            return Results.Accepted("/api/v1/library-health/check");
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        api.MapGet("/library-health/check", async (HealthCheck check, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var status = check.Status;
            var ids = status.Problems.Select(p => p.ItemId).Distinct().ToList();
            var items = await db.MediaItems.Where(i => ids.Contains(i.Id)).Summaries(profile.Id).ToDictionaryAsync(i => i.Id, ct);
            return new HealthCheckReportDto(status.Running, status.Checked, status.Total, status.FinishedAt,
                status.Problems.Select(p => new FileProblemReportDto(p, items.GetValueOrDefault(p.ItemId))).ToList());
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        // Asks Radarr or Sonarr to search for this film, episode, season or series again (e.g. to upgrade quality).
        api.MapPost("/items/{id:guid}/search-again", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var instance = options.CurrentValue.Arr.FirstOrDefault(a => a.Name == (item.Kind == MediaKind.Season ? item.Parent?.MetadataSource : item.MetadataSource)
                && a.Kind == (item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr));
            object? command = item.Kind switch
            {
                MediaKind.Movie when item.ArrId is { } movieId => new { name = "MoviesSearch", movieIds = new[] { movieId } },
                MediaKind.Episode when item.ArrId is { } episodeId => new { name = "EpisodeSearch", episodeIds = new[] { episodeId } },
                MediaKind.Season when item.Parent?.ArrId is { } seriesId => new { name = "SeasonSearch", seriesId, seasonNumber = item.SeasonNumber },
                MediaKind.Series when item.ArrId is { } seriesId => new { name = "SeriesSearch", seriesId },
                _ => null,
            };
            if (instance is null || command is null)
            {
                return Results.Problem("This title isn't managed by Sonarr or Radarr, so there's nothing to search with.", statusCode: 409);
            }

            await arr.SendCommandAsync(instance, command, ct);
            return Results.Accepted();
        }).WithTags("Library health").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }
}
