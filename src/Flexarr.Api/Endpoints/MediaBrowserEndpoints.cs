using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record MediaBrowserUpdate(string Path, string? UpdateType);

public sealed record MediaBrowserUpdateRequest(IReadOnlyList<MediaBrowserUpdate> Updates);

/// <summary>
/// Just enough of the Emby/Jellyfin API for Sonarr and Radarr's built-in "Emby / Jellyfin" connection to work
/// with Flexarr: the connection test, finding a title's folders, and the library-updated notification.
/// Point the connection at Flexarr's host and port with Flexarr's API key.
/// </summary>
public static class MediaBrowserEndpoints
{
    public static IEndpointRouteBuilder MapMediaBrowserEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithTags("Emby/Jellyfin compatibility").AllowAnonymous()
            .AddEndpointFilter(async (context, next) =>
                Authorized(context.HttpContext) ? await next(context) : Results.Unauthorized());

        group.MapGet("/System/Configuration", () => Results.Ok(new { ServerName = "Flexarr" }));

        group.MapPost("/Notifications/Admin", () => Results.NoContent());

        group.MapGet("/Items", async (string? includeItemTypes, int? years, FlexarrDbContext db, CancellationToken ct) =>
        {
            var kind = includeItemTypes?.Contains("Series", StringComparison.OrdinalIgnoreCase) == true ? MediaKind.Series : MediaKind.Movie;
            var query = db.MediaItems.Where(i => i.Kind == kind && i.Path != null);
            if (years is { } year)
            {
                query = query.Where(i => i.Year == year);
            }

            var items = await query.Select(i => new { i.Title, i.Path, i.TvdbId, i.TmdbId, i.ImdbId, i.TvMazeId }).ToListAsync(ct);
            return new
            {
                Items = items.Select(i => new
                {
                    Name = i.Title,
                    i.Path,
                    ProviderIds = new
                    {
                        Tvdb = Number(i.TvdbId),
                        Tmdb = Number(i.TmdbId),
                        TvMaze = Number(i.TvMazeId),
                        Imdb = i.ImdbId,
                    },
                }),
            };
        });

        group.MapPost("/Library/Media/Updated", async (MediaBrowserUpdateRequest request, ILibraryScanQueue queue, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var updates = request.Updates.Where(u => !string.IsNullOrWhiteSpace(u.Path)).ToList();
            if (updates.Any(u => PathComparison.HasControlCharacters(u.Path) || u.UpdateType is { } type && PathComparison.HasControlCharacters(type)))
            {
                return Results.Problem("A path in the update holds a control character.", statusCode: 400);
            }

            foreach (var update in updates)
            {
                // Sonarr and Radarr say "Deleted" when they removed the title themselves.
                var deleted = string.Equals(update.UpdateType, "Deleted", StringComparison.OrdinalIgnoreCase);
                await queue.EnqueueAsync(new LibraryScanRequest(PathMapper.Map(update.Path, options.CurrentValue.PathMappings),
                    $"Emby-style update ({update.UpdateType ?? "Modified"})", Deleted: deleted), ct);
            }

            UpcomingEndpoints.ForgetArrCalls(); // As for a webhook: Coming soon and Downloads have just changed.

            return Results.NoContent();
        });

        group.MapPost("/Library/Refresh", async (FlexarrDbContext db, ILibraryScanQueue queue, CancellationToken ct) =>
        {
            foreach (var folder in await db.LibraryFolders.Select(f => f.Path).ToListAsync(ct))
            {
                await queue.EnqueueAsync(new LibraryScanRequest(folder, "Emby-style refresh"), ct);
            }

            return Results.NoContent();
        });

        return app;
    }

    /// <summary>Emby clients send the key as X-MediaBrowser-Token, an Authorization header or ?api_key=.</summary>
    private static bool Authorized(HttpContext http)
    {
        var expected = http.RequestServices.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue.ApiKey ?? "";
        string? provided = http.Request.Headers["X-MediaBrowser-Token"].FirstOrDefault()
            ?? http.Request.Headers["X-Emby-Token"].FirstOrDefault()
            ?? http.Request.Query["api_key"].FirstOrDefault();
        if (provided is null && http.Request.Headers.Authorization.FirstOrDefault() is { } auth)
        {
            var start = auth.IndexOf("Token=\"", StringComparison.Ordinal);
            if (start >= 0)
            {
                var end = auth.IndexOf('"', start + 7);
                provided = end > start ? auth[(start + 7)..end] : null;
            }
        }

        return provided is not null && expected.Length > 0
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
    }

    private static int Number(string? id) => int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;
}
