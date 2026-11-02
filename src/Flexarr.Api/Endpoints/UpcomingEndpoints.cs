using System.Globalization;
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
    DownloadStateDto? Download,
    string? Source = null,
    string? ManageUrl = null,
    int? TmdbId = null);

/// <param name="Source">The Sonarr or Radarr instance it's in, by name.</param>
/// <param name="ManageUrl">That instance's queue, where a stuck download is sorted out.</param>
public sealed record DownloadDto(string Id, MediaKind Kind, string Title, string? SeriesTitle, int? SeasonNumber, int? EpisodeNumber, string? Poster, Guid? LibraryItemId, DownloadStateDto State,
    string? Source = null, string? ManageUrl = null);

public static class UpcomingEndpoints
{
    /// <summary>How long one *arr call may take before the page goes on without that instance.</summary>
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(8);

    // Calendars barely change; queues move, but a minute-old progress bar is fine and spares the *arrs on every Home load.
    private static readonly TimeSpan CalendarTtl = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QueueTtl = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(15);

    public static RouteGroupBuilder MapUpcomingEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/upcoming", async (int? days, IOptionsMonitor<FlexarrOptions> options, ArrClient arr, IMemoryCache cache, FlexarrDbContext db, HttpRequest request, CancellationToken ct) =>
            await UpcomingAsync(Math.Clamp(days ?? 21, 1, 90), options.CurrentValue, arr, cache, db, request.Host.Host, ct)).WithTags("Upcoming");

        api.MapGet("/downloads", async (IOptionsMonitor<FlexarrOptions> options, ArrClient arr, IMemoryCache cache, FlexarrDbContext db, HttpRequest request, CancellationToken ct) =>
            await DownloadsAsync(options.CurrentValue, arr, cache, db, request.Host.Host, ct)).WithTags("Upcoming");

        return api;
    }

    /// <param name="host">The host the browser reached Flexarr on, for links into *arr apps configured as localhost.</param>
    public static async Task<List<UpcomingDto>> UpcomingAsync(int days, FlexarrOptions options, ArrClient arr, IMemoryCache? cache, FlexarrDbContext db, string? host, CancellationToken ct) =>
        [.. (await UpcomingWithFoldersAsync(days, options, arr, cache, db, host, ct)).Select(u => u.Upcoming)];

    /// <summary>
    /// What's coming to one film or TV library: a title already in Flexarr counts where it is; one not yet in any
    /// library counts by the folder Sonarr or Radarr keeps it in (through the path mappings), else in the oldest
    /// library of its kind, so it shows somewhere rather than nowhere.
    /// </summary>
    public static async Task<List<UpcomingDto>> ForLibraryAsync(Library library, int days, FlexarrOptions options, ArrClient arr, IMemoryCache? cache, FlexarrDbContext db,
        CancellationToken ct)
    {
        MediaKind? kind = library.Type switch
        {
            LibraryType.Movies => MediaKind.Movie,
            LibraryType.TvShows => MediaKind.Episode,
            _ => null,
        };
        if (kind is null || options.Arr.All(a => a.Kind != (kind == MediaKind.Movie ? ArrKind.Radarr : ArrKind.Sonarr)))
        {
            return [];
        }

        var upcoming = (await UpcomingWithFoldersAsync(days, options, arr, cache, db, null, ct)).Where(u => u.Upcoming.Kind == kind).ToList();
        var inLibrary = upcoming.Select(u => u.Upcoming.LibraryItemId).OfType<Guid>().Distinct().ToList();
        var libraryOf = await db.MediaItems.Where(i => inLibrary.Contains(i.Id)).Select(i => new { i.Id, i.LibraryId }).ToDictionaryAsync(i => i.Id, i => i.LibraryId, ct);
        var sameKind = await db.Libraries.AsNoTracking().Where(l => l.Type == library.Type).OrderBy(l => l.CreatedAt).ThenBy(l => l.Id)
            .Select(l => new { l.Id, Folders = l.Folders.Select(f => f.Path).ToList() }).ToListAsync(ct);
        var oldest = sameKind.FirstOrDefault()?.Id;

        bool Here((UpcomingDto Upcoming, string? Folder) u)
        {
            if (u.Upcoming.LibraryItemId is { } item)
            {
                return libraryOf.TryGetValue(item, out var where) && where == library.Id;
            }

            var path = string.IsNullOrEmpty(u.Folder) ? null : PathMapper.Map(u.Folder, options.PathMappings);
            var homes = path is null ? [] : sameKind.Where(l => l.Folders.Any(f => PathComparison.IsUnder(path, f, PathComparison.FileSystemComparison))).Select(l => l.Id).ToList();
            return homes.Count > 0 ? homes.Contains(library.Id) : oldest == library.Id;
        }

        return [.. upcoming.Where(Here).Select(u => u.Upcoming)];
    }

    /// <summary>The calendar, each with the folder its *arr keeps it in (for placing it in a library; never sent).</summary>
    private static async Task<List<(UpcomingDto Upcoming, string? Folder)>> UpcomingWithFoldersAsync(int days, FlexarrOptions options, ArrClient arr, IMemoryCache? cache,
        FlexarrDbContext db, string? host, CancellationToken ct)
    {
        // From yesterday (a day's slack either side of UTC for the viewer's own "yesterday").
        var today = DateTime.UtcNow.Date;
        var start = today.AddDays(-2);
        var end = today.AddDays(days);
        var range = $"calendar:{start:yyyy-MM-dd}:{end:yyyy-MM-dd}";
        var sonarrs = options.Arr.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        var radarrs = options.Arr.Where(a => a.Kind == ArrKind.Radarr).ToList();

        // Every instance at once, so a slow one costs its own wait rather than adding to everyone else's.
        var queueTask = QueueAsync(options, arr, cache, ct);
        var episodeTasks = sonarrs.Select(sonarr => CachedAsync(cache, Key(sonarr, range), CalendarTtl,
            token => arr.GetCalendarAsync<SonarrEpisode>(sonarr, start, end, token), ct)).ToList();
        var movieTasks = radarrs.Select(radarr => CachedAsync(cache, Key(radarr, range), CalendarTtl,
            token => arr.GetCalendarAsync<RadarrMovie>(radarr, start, end, token), ct)).ToList();
        var queue = await queueTask;
        var episodeCalendars = await Task.WhenAll(episodeTasks);
        var movieCalendars = await Task.WhenAll(movieTasks);

        // Look up just the titles on the calendar, not every id in the library.
        var series = await SeriesIndexAsync(db, episodeCalendars.SelectMany(c => c).Select(e => e.Series?.TvdbId), ct);
        var tmdbIds = movieCalendars.SelectMany(c => c).Select(m => m.TmdbId.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
        // Not ToDictionary: a film can be in the library twice (two folders), sharing one TMDB id.
        var movies = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var m in await db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null && tmdbIds.Contains(i.TmdbId)).Select(i => new { i.Id, i.TmdbId }).ToListAsync(ct))
        {
            movies.TryAdd(m.TmdbId!, m.Id);
        }

        var elsewhere = await HiddenTitles.ForAsync(db, options.PathMappings,
            episodeCalendars.SelectMany(c => c).Select(e => e.Series?.TvdbId), tmdbIds, ct);
        var results = new List<(UpcomingDto Upcoming, string? Folder)>();

        foreach (var (sonarr, calendar) in sonarrs.Zip(episodeCalendars))
        {
            foreach (var episode in calendar)
            {
                var show = episode.Series;
                if (elsewhere.HidesSeries(show?.TvdbId, show?.Path, series))
                {
                    continue;
                }

                var itemId = show is null ? null : series.GetValueOrDefault(show.TvdbId.ToString(CultureInfo.InvariantCulture));
                var date = episode.AirDateUtc ?? DateTime.Parse(episode.AirDate ?? DateTime.UtcNow.ToString("O"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
                queue.TryGetValue($"sonarr-{episode.Id}", out var download);
                results.Add((new UpcomingDto(
                    $"sonarr-{sonarr.Name}-{episode.Id}", MediaKind.Episode, episode.Title ?? "TBA", show?.Title,
                    episode.SeasonNumber, episode.EpisodeNumber, date, "Airs", episode.Overview,
                    Remote(show?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(episode.Images.FirstOrDefault(i => i.CoverType == "screenshot")?.RemoteUrl ?? show?.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    itemId, episode.HasFile, download, sonarr.Name,
                    show?.TitleSlug is { } slug ? WebUrl(sonarr, host, $"series/{slug}") : null), show?.Path));
            }
        }

        foreach (var (radarr, calendar) in radarrs.Zip(movieCalendars))
        {
            foreach (var movie in calendar.Where(m => !elsewhere.HidesMovie(m.TmdbId, m.Path, movies)))
            {
                // A film appears once, on its next release in the window: cinema, then digital, then disc.
                var (date, when) = new[] { (movie.InCinemas, "In cinemas"), (movie.DigitalRelease, "Digital release"), (movie.PhysicalRelease, "On disc") }
                    .Where(r => r.Item1 is { } d && d >= start && d <= end)
                    .Select(r => (r.Item1!.Value, r.Item2))
                    .DefaultIfEmpty((movie.ReleaseDate ?? end, "Released"))
                    .First();
                queue.TryGetValue($"radarr-{movie.Id}", out var download);
                results.Add((new UpcomingDto(
                    $"radarr-{radarr.Name}-{movie.Id}", MediaKind.Movie, movie.Title, null, null, null, date, when, movie.Overview,
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    movies.GetValueOrDefault(movie.TmdbId.ToString(CultureInfo.InvariantCulture)), movie.HasFile, download, radarr.Name,
                    movie.TitleSlug is { } slug ? WebUrl(radarr, host, $"movie/{slug}") : null, movie.TmdbId), movie.Path));
            }
        }

        // Something in no library yet is shown unless the profile can't see any library of its kind.
        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        return results.Where(r => r.Upcoming.LibraryItemId is not null || !hidden.Hides(r.Upcoming.Kind)).OrderBy(r => r.Upcoming.Date).ToList();
    }

    public static async Task<List<DownloadDto>> DownloadsAsync(FlexarrOptions options, ArrClient arr, IMemoryCache? cache, FlexarrDbContext db, string? host, CancellationToken ct)
    {
        var instances = options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr).ToList();
        var queues = await Task.WhenAll(instances.Select(instance => QueueOfAsync(instance, arr, cache, ct)));
        var series = await SeriesIndexAsync(db, queues.SelectMany(q => q).Select(q => q.Series?.TvdbId), ct);
        var queued = queues.SelectMany(q => q).ToList();
        var elsewhere = await HiddenTitles.ForAsync(db, options.PathMappings, queued.Select(q => q.Series?.TvdbId),
            queued.Select(q => q.Movie?.TmdbId.ToString(CultureInfo.InvariantCulture)).OfType<string>(), ct);
        var downloads = new List<DownloadDto>();
        foreach (var (instance, queue) in instances.Zip(queues))
        {
            foreach (var q in queue)
            {
                var hiddenHere = instance.Kind == ArrKind.Sonarr
                    ? elsewhere.HidesSeries(q.Series?.TvdbId, q.Series?.Path, series)
                    : q.Movie is { } m && elsewhere.HidesMovie(m.TmdbId, m.Path, null);
                if (hiddenHere)
                {
                    continue;
                }

                var state = new DownloadStateDto(Math.Round(q.Progress, 3), q.Timeleft, q.TrackedDownloadState ?? q.Status);
                var queuePage = WebUrl(instance, host, "activity/queue");
                downloads.Add(instance.Kind == ArrKind.Sonarr
                    ? new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Episode, q.Episode?.Title ?? q.Title ?? "Episode", q.Series?.Title,
                        q.Episode?.SeasonNumber, q.Episode?.EpisodeNumber, Remote(q.Series?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                        q.Series is null ? null : series.GetValueOrDefault(q.Series.TvdbId.ToString(CultureInfo.InvariantCulture)), state, instance.Name, queuePage)
                    : new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Movie, q.Movie?.Title ?? q.Title ?? "Movie", null, null, null,
                        Remote(q.Movie?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl), null, state, instance.Name, queuePage));
            }
        }

        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        return downloads.Where(d => d.LibraryItemId is not null || !hidden.Hides(d.Kind)).OrderByDescending(d => d.State.Progress).ToList();
    }

    /// <summary>Queue entries keyed by "sonarr-{episodeId}" / "radarr-{movieId}".</summary>
    internal static Task<Dictionary<string, DownloadStateDto>> QueueAsync(FlexarrOptions options, ArrClient arr, CancellationToken ct) =>
        QueueAsync(options, arr, null, ct);

    /// <inheritdoc cref="QueueAsync(FlexarrOptions, ArrClient, CancellationToken)"/>
    internal static async Task<Dictionary<string, DownloadStateDto>> QueueAsync(FlexarrOptions options, ArrClient arr, IMemoryCache? cache, CancellationToken ct)
    {
        var instances = options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr).ToList();
        var queues = await Task.WhenAll(instances.Select(instance => QueueOfAsync(instance, arr, cache, ct)));
        var map = new Dictionary<string, DownloadStateDto>(StringComparer.Ordinal);
        foreach (var (instance, queue) in instances.Zip(queues))
        {
            foreach (var q in queue)
            {
                var key = instance.Kind == ArrKind.Sonarr ? $"sonarr-{q.EpisodeId}" : $"radarr-{q.MovieId}";
                map.TryAdd(key, new DownloadStateDto(Math.Round(q.Progress, 3), q.Timeleft, q.TrackedDownloadState ?? q.Status));
            }
        }

        return map;
    }

    private static Task<IReadOnlyList<ArrQueueItem>> QueueOfAsync(ArrInstance instance, ArrClient arr, IMemoryCache? cache, CancellationToken ct) =>
        CachedAsync(cache, Key(instance, "queue"), QueueTtl, token => arr.GetQueueAsync(instance, token), ct);

    /// <summary>Library shows by TVDB id, for just the ids asked about.</summary>
    private static async Task<Dictionary<string, Guid?>> SeriesIndexAsync(FlexarrDbContext db, IEnumerable<int?> tvdbIds, CancellationToken ct)
    {
        var wanted = tvdbIds.OfType<int>().Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
        var map = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return map;
        }

        foreach (var s in await db.MediaItems.Where(i => i.Kind == MediaKind.Series && i.TvdbId != null && wanted.Contains(i.TvdbId)).Select(i => new { i.Id, i.TvdbId }).ToListAsync(ct))
        {
            map.TryAdd(s.TvdbId!, s.Id);
        }

        return map;
    }

    /// <summary>
    /// Titles in a library hidden from this profile (someone else's shows), which Coming soon and Downloads leave out
    /// rather than show as new: known by their id there, or by the *arr keeping them in one of its folders.
    /// </summary>
    private sealed class HiddenTitles
    {
        private static readonly HiddenTitles None = new([], [], [], [], []);
        private readonly HashSet<string> _series;
        private readonly HashSet<string> _movies;
        private readonly List<string> _hiddenFolders;
        private readonly List<string> _visibleFolders;
        private readonly IReadOnlyList<PathMapping> _mappings;

        private HiddenTitles(HashSet<string> series, HashSet<string> movies, List<string> hiddenFolders, List<string> visibleFolders, IReadOnlyList<PathMapping> mappings)
        {
            (_series, _movies, _hiddenFolders, _visibleFolders, _mappings) = (series, movies, hiddenFolders, visibleFolders, mappings);
        }

        public static async Task<HiddenTitles> ForAsync(FlexarrDbContext db, IReadOnlyList<PathMapping> mappings, IEnumerable<int?> tvdbIds, IEnumerable<string> tmdbIds,
            CancellationToken ct)
        {
            var hidden = db.HiddenLibraryIds.ToList();
            if (hidden.Count == 0)
            {
                return None;
            }

            var tvdb = tvdbIds.OfType<int>().Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var tmdb = tmdbIds.Distinct().ToList();
            var items = db.MediaItems.IgnoreQueryFilters().Where(i => hidden.Contains(i.LibraryId));
            var series = tvdb.Count == 0 ? [] : await items.Where(i => i.Kind == MediaKind.Series && i.TvdbId != null && tvdb.Contains(i.TvdbId)).Select(i => i.TvdbId!).ToListAsync(ct);
            var movies = tmdb.Count == 0 ? [] : await items.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null && tmdb.Contains(i.TmdbId)).Select(i => i.TmdbId!).ToListAsync(ct);
            var folders = await db.LibraryFolders.Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct);
            return new HiddenTitles([.. series], [.. movies],
                [.. folders.Where(f => hidden.Contains(f.LibraryId)).Select(f => f.Path)],
                [.. folders.Where(f => !hidden.Contains(f.LibraryId)).Select(f => f.Path)], mappings);
        }

        /// <param name="visible">The profile's own shows by TVDB id: one it can see somewhere is never hidden.</param>
        public bool HidesSeries(int? tvdbId, string? path, Dictionary<string, Guid?> visible)
        {
            var id = tvdbId?.ToString(CultureInfo.InvariantCulture);
            return !(id is not null && visible.ContainsKey(id)) && ((id is not null && _series.Contains(id)) || InHiddenFolder(path));
        }

        public bool HidesMovie(int tmdbId, string? path, Dictionary<string, Guid?>? visible)
        {
            var id = tmdbId.ToString(CultureInfo.InvariantCulture);
            return !(visible?.ContainsKey(id) ?? false) && (_movies.Contains(id) || InHiddenFolder(path));
        }

        /// <summary>Where the *arr keeps it is in a hidden library's folder, and not also in one the profile can see.</summary>
        private bool InHiddenFolder(string? arrPath)
        {
            if (string.IsNullOrEmpty(arrPath) || _hiddenFolders.Count == 0)
            {
                return false;
            }

            var path = PathMapper.Map(arrPath, _mappings);
            return _hiddenFolders.Any(f => PathComparison.IsUnder(path, f, PathComparison.FileSystemComparison))
                && !_visibleFolders.Any(f => PathComparison.IsUnder(path, f, PathComparison.FileSystemComparison));
        }
    }

    private static string Key(ArrInstance instance, string what) => $"arr:{instance.Kind}:{instance.Name}:{instance.BaseUrl}:{what}";

    /// <summary>Cancelled (and replaced) to drop every cached *arr call at once.</summary>
    private static CancellationTokenSource _arrCalls = new();

    /// <summary>
    /// Forgets the cached calendars and queues, so the next page load asks Sonarr and Radarr again: a webhook means
    /// something just downloaded or was added, and it shouldn't show as still on its way for another minute or two.
    /// </summary>
    public static void ForgetArrCalls()
    {
        // Not disposed: a page load may be reading its token right now, and one with no timer holds nothing to free.
        Interlocked.Exchange(ref _arrCalls, new CancellationTokenSource()).Cancel();
    }

    /// <param name="ttl">How long to keep it.</param>
    /// <param name="asked">The token current when the call was made: forgetting since then drops this answer too.</param>
    private static MemoryCacheEntryOptions Expiring(TimeSpan ttl, CancellationToken asked) => new MemoryCacheEntryOptions
    {
        AbsoluteExpirationRelativeToNow = ttl,
        ExpirationTokens = { new Microsoft.Extensions.Primitives.CancellationChangeToken(asked) },
    };

    /// <summary>
    /// One *arr call, shared for a little while by everyone loading the page. An *arr being down or too slow
    /// shouldn't break the page: it contributes nothing, and isn't asked again for a few seconds.
    /// </summary>
    private static async Task<IReadOnlyList<T>> CachedAsync<T>(IMemoryCache? cache, string key, TimeSpan ttl, Func<CancellationToken, Task<IReadOnlyList<T>>> call, CancellationToken ct)
    {
        if (cache is not null && cache.TryGetValue(key, out IReadOnlyList<T>? hit) && hit is not null)
        {
            return hit;
        }

        // Taken before asking: an answer that was under way when a webhook said things changed may predate the change,
        // so it's used for this page but not kept.
        var asked = _arrCalls.Token;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);
        try
        {
            var result = await call(timeout.Token);
            if (!asked.IsCancellationRequested)
            {
                cache?.Set(key, result, Expiring(ttl, asked));
            }

            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException
            || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            if (!asked.IsCancellationRequested)
            {
                cache?.Set(key, (IReadOnlyList<T>)[], Expiring(FailureTtl, asked));
            }

            return [];
        }
    }

    /// <summary>
    /// A page in a Sonarr/Radarr web UI. An instance configured as localhost is only localhost to this server, so the
    /// browser is sent to the same host it reached Flexarr on, at the *arr's port.
    /// </summary>
    internal static string WebUrl(ArrInstance instance, string? host, string path)
    {
        var url = new UriBuilder(instance.BaseUrl);
        if (instance.BaseUrl.IsLoopback && !string.IsNullOrEmpty(host))
        {
            url.Host = host;
        }

        url.Path = $"{url.Path.TrimEnd('/')}/{path}";
        return url.Uri.ToString();
    }

    private static string? Remote(string? url) => ImageEndpoints.RemoteUrl(url);
}
