using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record HealthFileDto(Guid Id, string Path, long Size, string? Resolution, string? VideoCodec, long? Bitrate, bool Keep);

public sealed record DuplicateDto(ItemSummaryDto Item, string? SeriesTitle, IReadOnlyList<HealthFileDto> Files, long ReclaimableBytes);

public sealed record UnreadableDto(Guid FileId, string Path, string Error, ItemSummaryDto? Item);

/// <summary>
/// The share of films and episodes with nothing wrong (0–100), and how many titles each kind of problem touches.
/// </summary>
public sealed record HealthScoreDto(int Score, int Titles, int WithProblems, int Duplicates, int Unreadable, int StandardDefinition, int MissingArtwork, int FileProblems);

public sealed record LibraryHealthDto(
    HealthScoreDto Score,
    IReadOnlyList<DuplicateDto> Duplicates,
    long ReclaimableBytes,
    IReadOnlyList<UnreadableDto> Unreadable,
    IReadOnlyList<ItemSummaryDto> MissingArtwork,
    IReadOnlyList<ItemSummaryDto> StandardDefinition,
    int StandardDefinitionTotal);

public sealed record SkippedFileDto(string Path, string Reason);

public sealed record DuplicateRemovalDto(int Removed, long FreedBytes, IReadOnlyList<SkippedFileDto> Skipped);

public sealed record FileProblemReportDto(FileProblemDto File, ItemSummaryDto? Item);

public sealed record HealthCheckReportDto(bool Running, int Checked, int Total, DateTime? FinishedAt, IReadOnlyList<FileProblemReportDto> Problems);

public static partial class HealthEndpoints
{
    public static RouteGroupBuilder MapHealthEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/library-health", async (FlexarrDbContext db, CurrentProfile profile, HealthCheck check, CancellationToken ct) =>
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
            // The score counts each film or episode once, however many problems it has.
            var titles = await db.MediaItems.CountAsync(i => i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode, ct);
            var sdIds = await sd.Select(i => i.Id).ToListAsync(ct);
            var artless = await db.MediaItems.Where(i => i.ParentId == null && i.PosterUrl == null).Select(i => i.Id).ToListAsync(ct);
            var fileProblems = check.Status.Problems.Select(p => p.ItemId).Distinct().ToList();
            var troubled = new HashSet<Guid>([.. ids, .. unreadable.Select(u => u.MediaItemId), .. sdIds, .. artless, .. fileProblems]);
            var score = new HealthScoreDto(
                titles == 0 ? 100 : (int)Math.Floor(100.0 * Math.Max(0, titles - troubled.Count) / titles),
                titles, troubled.Count, ids.Count, unreadable.Select(u => u.MediaItemId).Distinct().Count(), sdIds.Count, artless.Count, fileProblems.Count);

            return new LibraryHealthDto(
                score,
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

        // Removes a film's or episode's spare copies from disk. Kept: the best copy, and whichever file Sonarr or Radarr
        // track (asked first; if they can't say, nothing is deleted).
        api.MapPost("/items/{id:guid}/remove-duplicates", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr,
            ILoggerFactory logs, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Include(i => i.Files).Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Episode) || item.Files.Count < 2)
            {
                return Results.Ok(new DuplicateRemovalDto(0, 0, []));
            }

            var kind = item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr;
            var source = item.Kind == MediaKind.Episode ? item.Parent?.Parent?.MetadataSource ?? item.MetadataSource : item.MetadataSource;
            var instance = options.CurrentValue.Arr.FirstOrDefault(a => a.Name == source && a.Kind == kind);
            if (instance is null || item.ArrId is not { } arrId)
            {
                return Results.Problem($"{kind} doesn't manage {item.Title}, so Flexarr can't tell which copy it uses. Remove the spare in the Finder.", statusCode: 409);
            }

            string? tracked;
            try
            {
                tracked = item.Kind == MediaKind.Movie
                    ? (await arr.GetMovieAsync(instance, arrId, ct)).MovieFile?.Path
                    : await arr.GetEpisodeAsync(instance, arrId, ct) is { EpisodeFileId: > 0 } episode ? (await arr.GetEpisodeFileAsync(instance, episode.EpisodeFileId, ct)).Path : null;
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{instance.Name} didn't say which copy it uses ({(int?)ex.StatusCode ?? 0}), so nothing was deleted.", statusCode: 502);
            }

            var keep = new HashSet<string>(StringComparer.Ordinal) { Normalize(Rank(item.Files)[0].Path) };
            if (tracked is not null)
            {
                keep.Add(Normalize(PathMapper.Map(tracked, options.CurrentValue.PathMappings)));
            }

            var removed = 0;
            long freed = 0;
            var skipped = new List<SkippedFileDto>();
            foreach (var file in item.Files.Where(f => !keep.Contains(Normalize(f.Path))).ToList())
            {
                try
                {
                    File.Delete(file.Path); // A file already gone is fine: it's dropped from the library either way.
                    db.MediaFiles.Remove(file);
                    removed++;
                    freed += file.Size;
                    LogRemoved(logs.CreateLogger("Flexarr.Health"), file.Path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    skipped.Add(new SkippedFileDto(file.Path, ex.Message));
                }
            }

            await db.SaveChangesAsync(ct);
            return Results.Ok(new DuplicateRemovalDto(removed, freed, skipped));
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

    /// <summary>Best copy first: highest resolution, then bitrate, then size.</summary>
    private static List<MediaFile> Rank(IEnumerable<MediaFile> files) =>
        files.OrderByDescending(f => Math.Max(f.Height ?? 0, (f.Width ?? 0) * 9 / 16)).ThenByDescending(f => f.Bitrate ?? 0).ThenByDescending(f => f.Size).ToList();

    private static string Normalize(string path) => path.Normalize(System.Text.NormalizationForm.FormC);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed spare copy {Path}")]
    private static partial void LogRemoved(ILogger logger, string path);
}
