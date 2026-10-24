using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// Deleting a film, show or episode for good: its files go through Radarr or Sonarr (which own them), and only once
/// they've confirmed does it leave Flexarr. Files Sonarr and Radarr don't manage are never deleted.
/// </summary>
public static partial class DeleteEndpoints
{
    public static RouteGroupBuilder MapDeleteEndpoints(this RouteGroupBuilder api)
    {
        api.MapDelete("/items/{id:guid}", async (Guid id, FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, ILoggerFactory logs, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().Include(i => i.Parent).ThenInclude(p => p!.Parent).FirstOrDefaultAsync(i => i.Id == id, ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            if (item.Kind is not (MediaKind.Movie or MediaKind.Series or MediaKind.Episode))
            {
                return Results.Problem("Only films, shows and episodes can be deleted.", statusCode: 400);
            }

            // Episodes are matched to Sonarr through their show.
            var source = item.Kind == MediaKind.Episode ? item.Parent?.Parent?.MetadataSource ?? item.MetadataSource : item.MetadataSource;
            var kind = item.Kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr;
            var instance = options.CurrentValue.Arr.FirstOrDefault(a => a.Name == source && a.Kind == kind);
            if (instance is null || item.ArrId is not { } arrId)
            {
                return Results.Problem(
                    $"{item.Title} isn't managed by {kind}, so Flexarr won't delete it. Delete it in {kind} (or on disk), and Flexarr will notice at its next scan.",
                    statusCode: 409);
            }

            try
            {
                switch (item.Kind)
                {
                    case MediaKind.Movie:
                        await arr.DeleteAsync(instance, $"movie/{arrId}?deleteFiles=true&addImportExclusion=false", ct);
                        break;
                    case MediaKind.Series:
                        await arr.DeleteAsync(instance, $"series/{arrId}?deleteFiles=true&addImportListExclusion=false", ct);
                        break;
                    default:
                        // Unmonitor first, or Sonarr would download the episode again.
                        var episode = await arr.GetEpisodeAsync(instance, arrId, ct);
                        await arr.SetEpisodesMonitoredAsync(instance, [arrId], monitored: false, ct);
                        if (episode.EpisodeFileId > 0)
                        {
                            await arr.DeleteAsync(instance, $"episodefile/{episode.EpisodeFileId}", ct);
                        }

                        break;
                }
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"{instance.Name} didn't delete it ({(int?)ex.StatusCode ?? 0}). Nothing was removed from Flexarr.", statusCode: 502);
            }

            // Seasons and episodes go with their show (cascade); an emptied season goes too.
            await db.MediaItems.Where(i => i.Id == id).ExecuteDeleteAsync(ct);
            if (item.Kind == MediaKind.Episode && item.ParentId is { } seasonId && !await db.MediaItems.AnyAsync(i => i.ParentId == seasonId, ct))
            {
                await db.MediaItems.Where(i => i.Id == seasonId).ExecuteDeleteAsync(ct);
            }

            LogDeleted(logs.CreateLogger("Flexarr.Delete"), item.Kind, item.Kind == MediaKind.Episode ? $"{item.Parent?.Parent?.Title} {item.Title}" : item.Title, instance.Name);
            return Results.NoContent();
        }).WithTags("Items").RequireAuthorization(FlexarrClaims.AdminPolicy);

        return api;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Deleted {Kind} {Title} and its files through {Instance}")]
    private static partial void LogDeleted(ILogger logger, MediaKind kind, string? title, string instance);
}
