using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

/// <summary>A film or show Seerr is waiting for: asked for (in Flexarr or in Seerr itself) and not in yet.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="TmdbId">What unrequesting takes, with <paramref name="MediaType"/>.</param>
/// <param name="ReleaseDate">When it's out (upcoming), else when it came out.</param>
/// <param name="State">"upcoming" (not out yet), "searching", "downloading" or "importing", as music's.</param>
/// <param name="Progress">While downloading: how far, 0 to 1.</param>
/// <param name="Seasons">For a show, the seasons asked for; null for every one.</param>
/// <param name="LibraryItemId">A show that's partly in the library already.</param>
public sealed record TitleRequestedDto(
    string MediaType,
    int TmdbId,
    string Title,
    int? Year,
    string? Poster,
    DateOnly? ReleaseDate,
    string State,
    double? Progress,
    IReadOnlyList<int>? Seasons,
    Guid? LibraryItemId);

/// <summary>
/// Every film and show Seerr is waiting for, household-wide: its open requests whose title isn't in yet, with how far
/// along Radarr or Sonarr are. Only titles in folders the profile can see (and, given a library, in that library's).
/// </summary>
public static class RequestRequested
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Seerr's and the *arrs' answers are kept this long (a page and its revisits), dropped by a request or an unrequest.</summary>
    private static readonly TimeSpan ListsFor = TimeSpan.FromSeconds(30);

    /// <summary>A title's name and poster barely change.</summary>
    private static readonly TimeSpan TitlesFor = TimeSpan.FromHours(6);

    private static CancellationTokenSource _changed = new();

    /// <summary>A request or an unrequest changed what Seerr is waiting for.</summary>
    public static void Forget() => Interlocked.Exchange(ref _changed, new CancellationTokenSource()).Cancel();

    private sealed record Found(string Title, int? Year, string? Poster, string? Path, string State, double? Progress, DateOnly? ReleaseDate);

    /// <param name="library">Only titles in this library (its Requested view); null for every one the profile can see.</param>
    /// <returns>Null when Seerr didn't answer.</returns>
    public static async Task<List<TitleRequestedDto>?> ListAsync(SeerrInstance seerr, SeerrClient seerrClient, ArrClient arr, FlexarrOptions options, FlexarrDbContext db,
        IMemoryCache cache, Library? library, CancellationToken ct)
    {
        var requests = await Cached(cache, $"seerr-requested:{seerr.BaseUrl}", ListsFor, t => seerrClient.RequestsAsync(seerr, t), ct);
        if (requests is null)
        {
            return null;
        }

        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        var wantMovies = library is null ? !hidden.Hides(MediaKind.Movie) : library.Type == LibraryType.Movies;
        var wantShows = library is null ? !hidden.Hides(MediaKind.Series) : library.Type == LibraryType.TvShows;

        // Open (pending, approved, failed) for a title Seerr is still getting (pending, processing or partly in: not
        // all in, nor deleted or blocked since), one row per title with every season asked for.
        var titles = requests.Where(r => r.Status is 1 or 2 or 4 && r.Media is { Status: 2 or 3 or 4 } && r.Type is "movie" or "tv")
            .Where(r => r.Type == "movie" ? wantMovies : wantShows)
            .GroupBy(r => (r.Type, r.Media!.TmdbId))
            .Select(g => (g.Key.Type, g.Key.TmdbId, TvdbId: g.Select(r => r.Media!.TvdbId).FirstOrDefault(t => t is > 0),
                RootFolder: g.Select(r => r.RootFolder).FirstOrDefault(f => !string.IsNullOrEmpty(f)),
                Seasons: g.Key.Type == "tv" && g.All(r => r.Seasons is { Count: > 0 }) ? g.SelectMany(r => r.Seasons!).Select(s => s.SeasonNumber).Distinct().Order().ToList() : null,
                Pending: g.All(r => r.Status == 1)))
            .ToList();
        if (titles.Count == 0)
        {
            return [];
        }

        // A film already in a library the profile sees is in, whatever Seerr thinks; a show may be partly in.
        var inLibrary = await RequestEndpoints.LibraryIndexAsync(db, ct);
        var everywhere = await db.MediaItems.IgnoreQueryFilters().Where(i => i.TmdbId != null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series))
            .Select(i => new { i.Kind, i.TmdbId, i.LibraryId }).ToListAsync(ct);
        var whereIs = everywhere.ToLookup(i => $"{(i.Kind == MediaKind.Movie ? "movie" : "tv")}:{i.TmdbId}", i => i.LibraryId);

        var libraries = await db.Libraries.IgnoreQueryFilters().AsNoTracking().Select(l => new { l.Id, l.Type, l.CreatedAt }).ToDictionaryAsync(l => l.Id, ct);
        var folders = (await db.LibraryFolders.IgnoreQueryFilters().AsNoTracking().Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct))
            .Where(f => libraries.ContainsKey(f.LibraryId))
            .Select(f => new { f.LibraryId, f.Path, libraries[f.LibraryId].Type, libraries[f.LibraryId].CreatedAt }).ToList();
        var visibleLibraries = await db.Libraries.Select(l => l.Id).ToListAsync(ct);

        var radarrs = options.Arr.Where(a => a.Kind == ArrKind.Radarr).ToList();
        var sonarrs = options.Arr.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        // One that doesn't answer (a sleeping NAS) is asked once, not once per title, each waiting out the timeout.
        var down = new HashSet<ArrInstance>();
        var queues = new Dictionary<ArrInstance, IReadOnlyList<ArrQueueItem>>();
        foreach (var instance in radarrs.Concat(sonarrs).Where(i => titles.Any(t => (t.Type == "movie") == (i.Kind == ArrKind.Radarr))))
        {
            var queue = await Cached(cache, $"requested-queue:{instance.BaseUrl}", ListsFor, t => arr.GetQueueAsync(instance, t), ct);
            if (queue is null)
            {
                down.Add(instance);
            }

            queues[instance] = queue ?? [];
        }

        var now = DateTime.UtcNow;
        var listed = new List<TitleRequestedDto>();
        foreach (var title in titles)
        {
            var key = $"{title.Type}:{title.TmdbId.ToString(CultureInfo.InvariantCulture)}";
            var itemId = inLibrary.TryGetValue(key, out var id) ? id : (Guid?)null;
            if (title.Type == "movie" && itemId is not null)
            {
                continue;
            }

            var (found, gone) = title.Type == "movie"
                ? await MovieAsync(arr, radarrs, queues, down, cache, title.TmdbId, now, ct)
                : title.TvdbId is { } tvdb ? await ShowAsync(arr, sonarrs, queues, down, cache, tvdb, title.Seasons, now, ct) : (null, false);

            // Radarr or Sonarr has it all already (the library just hasn't caught up), or answered that it doesn't hold
            // an approved request's title at all (taken out since, so nobody's looking): not on its way.
            if (gone && !(found is null && title.Pending))
            {
                continue;
            }

            found ??= await FromSeerrAsync(seerrClient, seerr, cache, title.Type, title.TmdbId, ct);
            if (found is null)
            {
                continue;
            }

            // Where it lives: the library item's library, else the folder Radarr or Sonarr keeps it in (or Seerr sent
            // it to), else the oldest library of its kind, as Coming soon places it.
            var type = title.Type == "movie" ? LibraryType.Movies : LibraryType.TvShows;
            var path = found.Path ?? title.RootFolder;
            var local = string.IsNullOrEmpty(path) ? null : PathMapper.Map(path, options.PathMappings);
            var homes = whereIs[key].Distinct().ToList();
            if (homes.Count == 0 && local is not null)
            {
                homes = [.. folders.Where(f => f.Type == type && PathComparison.IsUnder(local, f.Path, PathComparison.FileSystemComparison)).Select(f => f.LibraryId).Distinct()];
            }

            if (homes.Count == 0 && folders.Where(f => f.Type == type).OrderBy(f => f.CreatedAt).ThenBy(f => f.LibraryId).FirstOrDefault() is { } oldest)
            {
                homes = [oldest.LibraryId];
            }

            if (!homes.Any(visibleLibraries.Contains) || (library is not null && !homes.Contains(library.Id)))
            {
                continue;
            }

            listed.Add(new TitleRequestedDto(title.Type, title.TmdbId, found.Title, found.Year, found.Poster, found.ReleaseDate, found.State, found.Progress, title.Seasons,
                itemId));
        }

        // What's moving first, then what's being looked for, then what's coming (soonest first); as music's.
        return listed.OrderBy(r => r.State switch { "importing" => 0, "downloading" => 1, "searching" => 2, _ => 3 })
            .ThenBy(r => r.State == "upcoming" ? r.ReleaseDate?.DayNumber ?? int.MaxValue : 0)
            .ThenBy(r => r.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <returns>
    /// What Radarr says of the film; <c>Gone</c> when every Radarr answered and it's either on disk already or held by
    /// none of them.
    /// </returns>
    /// <param name="down">Instances that didn't answer earlier in this listing: skipped, and any that don't now join them.</param>
    private static async Task<(Found? Found, bool Gone)> MovieAsync(ArrClient arr, List<ArrInstance> radarrs, Dictionary<ArrInstance, IReadOnlyList<ArrQueueItem>> queues,
        HashSet<ArrInstance> down, IMemoryCache cache, int tmdbId, DateTime now, CancellationToken ct)
    {
        var answered = 0;
        foreach (var radarr in radarrs.Where(r => !down.Contains(r)))
        {
            var films = await Cached(cache, $"requested-movie:{radarr.BaseUrl}:{tmdbId}", ListsFor, t => arr.GetMoviesByTmdbIdAsync(radarr, tmdbId, t), ct);
            answered += films is null ? 0 : 1;
            if (films is null)
            {
                down.Add(radarr);
            }

            if (films is not [var film, ..])
            {
                continue;
            }

            if (film.HasFile)
            {
                return (null, true);
            }

            var (state, progress) = Download(queues.GetValueOrDefault(radarr, []).Where(q => q.MovieId == film.Id).ToList());
            var out_ = new[] { film.DigitalRelease, film.PhysicalRelease }.OfType<DateTime>().Order().FirstOrDefault(d => d > now);
            DateOnly? release = null;
            if (state is null)
            {
                var released = film.Status is not ("announced" or "inCinemas") || (film.DigitalRelease ?? film.PhysicalRelease) is { } d && d <= now;
                state = released ? "searching" : "upcoming";
                release = released || out_ == default ? null : DateOnly.FromDateTime(out_);
            }

            return (new Found(film.Title, film.Year > 0 ? film.Year : null, Poster(film.Images), film.Path, state, progress, release), false);
        }

        return (null, radarrs.Count > 0 && answered == radarrs.Count);
    }

    /// <param name="seasons">The seasons asked for; null for every one.</param>
    /// <returns>As <see cref="MovieAsync"/>: <c>Gone</c> when Sonarr has every season asked for, or no Sonarr holds the show.</returns>
    private static async Task<(Found? Found, bool Gone)> ShowAsync(ArrClient arr, List<ArrInstance> sonarrs, Dictionary<ArrInstance, IReadOnlyList<ArrQueueItem>> queues,
        HashSet<ArrInstance> down, IMemoryCache cache, int tvdbId, IReadOnlyList<int>? seasons, DateTime now, CancellationToken ct)
    {
        var answered = 0;
        foreach (var sonarr in sonarrs.Where(s => !down.Contains(s)))
        {
            var shows = await Cached(cache, $"requested-series:{sonarr.BaseUrl}:{tvdbId}", ListsFor, t => arr.GetSeriesByTvdbIdAsync(sonarr, tvdbId, t), ct);
            answered += shows is null ? 0 : 1;
            if (shows is null)
            {
                down.Add(sonarr);
            }

            if (shows is not [var show, ..])
            {
                continue;
            }

            var asked = show.Seasons.Where(s => s.SeasonNumber > 0 && (seasons is null ? s.Monitored : seasons.Contains(s.SeasonNumber))).ToList();
            if (asked.Count > 0 && asked.All(s => s.Statistics is { EpisodeCount: > 0 } st && st.EpisodeFileCount >= st.EpisodeCount))
            {
                return (null, true);
            }

            var (state, progress) = Download(queues.GetValueOrDefault(sonarr, []).Where(q => q.SeriesId == show.Id).ToList());
            DateOnly? release = null;
            if (state is null)
            {
                var upcoming = show.Status == "upcoming" || show.FirstAired is { } first && first > now;
                state = upcoming ? "upcoming" : "searching";
                release = upcoming && show.FirstAired is { } f ? DateOnly.FromDateTime(f) : null;
            }

            return (new Found(show.Title, show.Year > 0 ? show.Year : null, Poster(show.Images), show.Path, state, progress, release), false);
        }

        return (null, sonarrs.Count > 0 && answered == sonarrs.Count);
    }

    /// <summary>Not in Radarr or Sonarr (yet, or any more): Seerr's own name and poster for it, still searching as far as anyone here knows.</summary>
    private static async Task<Found?> FromSeerrAsync(SeerrClient seerrClient, SeerrInstance seerr, IMemoryCache cache, string type, int tmdbId, CancellationToken ct)
    {
        var key = $"requested-title:{type}:{tmdbId}";
        if (cache.TryGetValue(key, out Found? hit))
        {
            return hit;
        }

        JsonElement? detail;
        using (var quick = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            quick.CancelAfter(CallTimeout);
            try
            {
                detail = await seerrClient.GetAsync(seerr, $"{type}/{tmdbId.ToString(CultureInfo.InvariantCulture)}", quick.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return null;
            }
        }

        if (detail is not { ValueKind: JsonValueKind.Object } t)
        {
            return null;
        }

        string? Str(string name) => t.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var date = Str("releaseDate") ?? Str("firstAirDate");
        var found = new Found(Str("title") ?? Str("name") ?? "Untitled",
            date is { Length: >= 4 } && int.TryParse(date[..4], CultureInfo.InvariantCulture, out var y) ? y : null,
            RequestEndpoints.Tmdb(Str("posterPath"), "w500"), null, "searching", null, null);
        cache.Set(key, found, TitlesFor);
        return found;
    }

    /// <summary>The title's downloads: importing once any is done downloading, else downloading with how far (by size).</summary>
    private static (string? State, double? Progress) Download(List<ArrQueueItem> queued)
    {
        if (queued.Count == 0)
        {
            return (null, null);
        }

        if (queued.Any(q => q.TrackedDownloadState is "importPending" or "importing" or "imported" or "importBlocked" or "importFailed" or "failedPending" or "failed"
                || q.Status is "completed" or "failed" or "warning"))
        {
            return ("importing", null);
        }

        // One download per pack: a season pack shows once per episode.
        var downloads = queued.DistinctBy(q => q.DownloadId ?? q.Id.ToString(CultureInfo.InvariantCulture)).ToList();
        var size = downloads.Sum(q => q.Size);
        var progress = size > 0 ? 1 - downloads.Sum(q => q.Sizeleft) / size : 0;
        return ("downloading", Math.Clamp(Math.Round(progress, 3), 0, 1));
    }

    private static string? Poster(IReadOnlyList<ArrImage> images) =>
        ImageEndpoints.RemoteUrl(images.FirstOrDefault(i => string.Equals(i.CoverType, "poster", StringComparison.OrdinalIgnoreCase))?.RemoteUrl);

    /// <summary>
    /// One call, kept a little while (dropped by a request or an unrequest); null when it failed or took too long, which
    /// isn't kept, so the next page asks again.
    /// </summary>
    private static async Task<T?> Cached<T>(IMemoryCache cache, string key, TimeSpan ttl, Func<CancellationToken, Task<T>> call, CancellationToken ct) where T : class
    {
        if (cache.TryGetValue(key, out T? hit) && hit is not null)
        {
            return hit;
        }

        var asked = Volatile.Read(ref _changed).Token;
        using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
        quick.CancelAfter(CallTimeout);
        try
        {
            var result = await call(quick.Token);
            using var entry = cache.CreateEntry(key);
            entry.Value = result;
            entry.AbsoluteExpirationRelativeToNow = ttl;
            entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(asked));
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }
}
