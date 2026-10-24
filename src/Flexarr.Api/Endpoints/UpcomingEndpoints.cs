using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

public sealed record DownloadStateDto(double Progress, string? TimeLeft, string? Status);

/// <summary>Something Sonarr or Radarr expects soon: an episode airing or a film being released.</summary>
public sealed record UpcomingDto(
    string Id,
    MediaKind Kind,
    string Title,
    string? SeriesTitle,
    int? SeasonNumber,
    int? EpisodeNumber,
    DateTime Date,
    string When,
    string? Overview,
    string? Poster,
    string? Still,
    Guid? LibraryItemId,
    bool Available,
    DownloadStateDto? Download);

public sealed record DownloadDto(string Id, MediaKind Kind, string Title, string? SeriesTitle, int? SeasonNumber, int? EpisodeNumber, string? Poster, Guid? LibraryItemId, DownloadStateDto State);

public static class UpcomingEndpoints
{
    public static RouteGroupBuilder MapUpcomingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/upcoming", async (int? days, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CancellationToken ct) =>
            await UpcomingAsync(Math.Clamp(days ?? 21, 1, 90), options.CurrentValue, arr, db, ct)).WithTags("Upcoming");

        api.MapGet("/downloads", async (IOptionsMonitor<FlexarrOptions> options, ArrClient arr, FlexarrDbContext db, CancellationToken ct) =>
            await DownloadsAsync(options.CurrentValue, arr, db, ct)).WithTags("Upcoming");

        return api;
    }

    public static async Task<List<UpcomingDto>> UpcomingAsync(int days, FlexarrOptions options, ArrClient arr, FlexarrDbContext db, CancellationToken ct)
    {
        var start = DateTime.UtcNow.Date.AddDays(-1);
        var end = DateTime.UtcNow.Date.AddDays(days);
        var queue = await QueueAsync(options, arr, ct);
        var series = await SeriesIndexAsync(db, ct);
        // Not ToDictionary: a film can be in the library twice (two folders), sharing one TMDB id.
        var movies = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var m in await db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null).Select(i => new { i.Id, i.TmdbId }).ToListAsync(ct))
        {
            movies.TryAdd(m.TmdbId!, m.Id);
        }
        var results = new List<UpcomingDto>();

        foreach (var sonarr in options.Arr.Where(a => a.Kind == ArrKind.Sonarr))
        {
            foreach (var episode in await SafeAsync(() => arr.GetCalendarAsync<SonarrEpisode>(sonarr, start, end, ct)))
            {
                var show = episode.Series;
                var itemId = show is null ? null : series.GetValueOrDefault(show.TvdbId.ToString(CultureInfo.InvariantCulture));
                var date = episode.AirDateUtc ?? DateTime.Parse(episode.AirDate ?? DateTime.UtcNow.ToString("O"), CultureInfo.InvariantCulture);
                queue.TryGetValue($"sonarr-{episode.Id}", out var download);
                results.Add(new UpcomingDto(
                    $"sonarr-{sonarr.Name}-{episode.Id}", MediaKind.Episode, episode.Title ?? "TBA", show?.Title,
                    episode.SeasonNumber, episode.EpisodeNumber, date, "Airs", episode.Overview,
                    Remote(show?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(episode.Images.FirstOrDefault(i => i.CoverType == "screenshot")?.RemoteUrl ?? show?.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    itemId, episode.HasFile, download));
            }
        }

        foreach (var radarr in options.Arr.Where(a => a.Kind == ArrKind.Radarr))
        {
            foreach (var movie in await SafeAsync(() => arr.GetCalendarAsync<RadarrMovie>(radarr, start, end, ct)))
            {
                // A film appears once, on its next release in the window: cinema, then digital, then disc.
                var (date, when) = new[] { (movie.InCinemas, "In cinemas"), (movie.DigitalRelease, "Digital release"), (movie.PhysicalRelease, "On disc") }
                    .Where(r => r.Item1 is { } d && d >= start && d <= end)
                    .Select(r => (r.Item1!.Value, r.Item2))
                    .DefaultIfEmpty((movie.ReleaseDate ?? end, "Released"))
                    .First();
                queue.TryGetValue($"radarr-{movie.Id}", out var download);
                results.Add(new UpcomingDto(
                    $"radarr-{radarr.Name}-{movie.Id}", MediaKind.Movie, movie.Title, null, null, null, date, when, movie.Overview,
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    movies.GetValueOrDefault(movie.TmdbId.ToString(CultureInfo.InvariantCulture)), movie.HasFile, download));
            }
        }

        // Something not in a library this profile can see is shown only if it's kept out of no library of that kind.
        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        return results.Where(r => r.LibraryItemId is not null || !hidden.Hides(r.Kind)).OrderBy(r => r.Date).ToList();
    }

    public static async Task<List<DownloadDto>> DownloadsAsync(FlexarrOptions options, ArrClient arr, FlexarrDbContext db, CancellationToken ct)
    {
        var series = await SeriesIndexAsync(db, ct);
        var downloads = new List<DownloadDto>();
        foreach (var instance in options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr))
        {
            foreach (var q in await SafeAsync(() => arr.GetQueueAsync(instance, ct)))
            {
                var state = new DownloadStateDto(Math.Round(q.Progress, 3), q.Timeleft, q.TrackedDownloadState ?? q.Status);
                downloads.Add(instance.Kind == ArrKind.Sonarr
                    ? new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Episode, q.Episode?.Title ?? q.Title ?? "Episode", q.Series?.Title,
                        q.Episode?.SeasonNumber, q.Episode?.EpisodeNumber, Remote(q.Series?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                        q.Series is null ? null : series.GetValueOrDefault(q.Series.TvdbId.ToString(CultureInfo.InvariantCulture)), state)
                    : new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Movie, q.Movie?.Title ?? q.Title ?? "Film", null, null, null,
                        Remote(q.Movie?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl), null, state));
            }
        }

        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        return downloads.Where(d => d.LibraryItemId is not null || !hidden.Hides(d.Kind)).OrderByDescending(d => d.State.Progress).ToList();
    }

    /// <summary>Queue entries keyed by "sonarr-{episodeId}" / "radarr-{movieId}".</summary>
    private static async Task<Dictionary<string, DownloadStateDto>> QueueAsync(FlexarrOptions options, ArrClient arr, CancellationToken ct)
    {
        var map = new Dictionary<string, DownloadStateDto>(StringComparer.Ordinal);
        foreach (var instance in options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr))
        {
            foreach (var q in await SafeAsync(() => arr.GetQueueAsync(instance, ct)))
            {
                var key = instance.Kind == ArrKind.Sonarr ? $"sonarr-{q.EpisodeId}" : $"radarr-{q.MovieId}";
                map.TryAdd(key, new DownloadStateDto(Math.Round(q.Progress, 3), q.Timeleft, q.TrackedDownloadState ?? q.Status));
            }
        }

        return map;
    }

    private static async Task<Dictionary<string, Guid?>> SeriesIndexAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var s in await db.MediaItems.Where(i => i.Kind == MediaKind.Series && i.TvdbId != null).Select(i => new { i.Id, i.TvdbId }).ToListAsync(ct))
        {
            map.TryAdd(s.TvdbId!, s.Id);
        }

        return map;
    }

    /// <summary>An *arr being down shouldn't break the page; it just contributes nothing.</summary>
    private static async Task<IReadOnlyList<T>> SafeAsync<T>(Func<Task<IReadOnlyList<T>>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return [];
        }
    }

    private static string? Remote(string? url) => url is null ? null : $"/api/v1/images/remote?url={Uri.EscapeDataString(url)}";
}
