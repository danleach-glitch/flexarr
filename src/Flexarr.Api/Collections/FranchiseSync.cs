using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Collections;

/// <param name="Poster">A remote image URL (through the image proxy when shown).</param>
public sealed record MissingFilm(int TmdbId, string Title, int? Year, string? Overview, string? Poster);

/// <summary>
/// Keeps franchise collections in step with Radarr: every film series with at least two films in the library becomes
/// a household collection, in release order, remembering the films not in the library yet (to show and request).
/// Runs shortly after start-up, every 12 hours, and on demand. Hidden franchises stay hidden.
/// </summary>
public sealed partial class FranchiseSync(IServiceScopeFactory scopes, ILogger<FranchiseSync> logger) : BackgroundService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            try
            {
                await SyncAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or DbUpdateException)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>Returns how many franchises the library has (two or more of its films).</summary>
    public async Task<int> SyncAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var arr = scope.ServiceProvider.GetRequiredService<ArrClient>();
            var options = scope.ServiceProvider.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;

            // Films in the library by TMDB id (a film can be there twice, in two folders: either will do).
            var films = new Dictionary<int, Guid>();
            foreach (var f in await db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null).Select(i => new { i.Id, i.TmdbId }).ToListAsync(ct))
            {
                if (int.TryParse(f.TmdbId, System.Globalization.CultureInfo.InvariantCulture, out var tmdb))
                {
                    films.TryAdd(tmdb, f.Id);
                }
            }

            var existing = await db.Collections.Include(c => c.Items).Where(c => c.Kind == CollectionKind.Franchise).ToDictionaryAsync(c => c.TmdbCollectionId!.Value, ct);
            var count = 0;
            foreach (var radarr in options.Arr.Where(a => a.Kind == ArrKind.Radarr))
            {
                foreach (var series in await arr.GetCollectionsAsync(radarr, ct))
                {
                    var ordered = series.Movies.OrderBy(m => m.Year == 0 ? int.MaxValue : m.Year).ThenBy(m => m.Title, StringComparer.Ordinal).ToList();
                    var owned = ordered.Where(m => films.ContainsKey(m.TmdbId)).ToList();
                    existing.TryGetValue(series.TmdbId, out var collection);
                    if (owned.Count < 2 && collection is null)
                    {
                        continue;
                    }

                    count += owned.Count >= 2 ? 1 : 0;
                    if (collection is null)
                    {
                        collection = new Collection { Name = Name(series.Title), Kind = CollectionKind.Franchise, TmdbCollectionId = series.TmdbId };
                        db.Collections.Add(collection);
                        existing[series.TmdbId] = collection;
                    }

                    collection.Description ??= series.Overview;
                    collection.PosterUrl = Image(series.Images, "poster") ?? collection.PosterUrl;
                    collection.BackdropUrl = Image(series.Images, "fanart") ?? collection.BackdropUrl;
                    collection.MissingJson = JsonSerializer.Serialize(
                        // Not yet announced properly ("Untitled James Bond Film", no year): nothing to request.
                        ordered.Where(m => !films.ContainsKey(m.TmdbId) && m.Year > 0)
                            .Select(m => new MissingFilm(m.TmdbId, m.Title, m.Year == 0 ? null : m.Year, m.Overview, Image(m.Images, "poster"))).ToList(), Json);

                    // Items follow release order; a film that left the library leaves the collection.
                    var wanted = owned.Select((m, i) => (Id: films[m.TmdbId], Position: i)).ToList();
                    collection.Items.RemoveAll(i => wanted.All(w => w.Id != i.MediaItemId));
                    foreach (var (id, position) in wanted)
                    {
                        if (collection.Items.FirstOrDefault(i => i.MediaItemId == id) is { } item)
                        {
                            item.Position = position;
                        }
                        else
                        {
                            collection.Items.Add(new CollectionItem { MediaItemId = id, Position = position });
                        }
                    }

                    collection.UpdatedAt = DateTime.UtcNow;
                }
            }

            await db.SaveChangesAsync(ct);
            LogSynced(count);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>"James Bond Collection" reads better as "James Bond" among other collections.</summary>
    private static string Name(string title) =>
        title.EndsWith(" Collection", StringComparison.OrdinalIgnoreCase) ? title[..^" Collection".Length] : title;

    private static string? Image(IReadOnlyList<ArrImage> images, string type) =>
        images.FirstOrDefault(i => i.CoverType == type)?.RemoteUrl;

    [LoggerMessage(Level = LogLevel.Information, Message = "Franchise collections synced: {Count} in the library")]
    private partial void LogSynced(int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't sync franchise collections from Radarr")]
    private partial void LogFailed(Exception exception);
}
