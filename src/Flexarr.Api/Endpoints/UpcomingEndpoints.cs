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
    public static async Task<List<UpcomingDto>> UpcomingAsync(int days, FlexarrOptions options, ArrClient arr, IMemoryCache? cache, FlexarrDbContext db, string? host, CancellationToken ct)
    {
        // From yesterday (a day's slack either side of UTC for the viewer's own "yesterday").
        var today = DateTime.UtcNow.Date;
        var start = today.AddDays(-2);
        var end = today.AddDays(days);
        var range = $"calendar:{start:yyyy-MM-dd}:{end:yyyy-MM-dd}";
        var sonarrs = options.Arr.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        var radarrs = options.Arr.Where(a => a.Kind == ArrKind.Radarr).ToList();
        var readarrs = options.Arr.Where(a => a.Kind == ArrKind.Readarr).ToList();

        // Every instance at once, so a slow one costs its own wait rather than adding to everyone else's.
        var queueTask = QueueAsync(options, arr, cache, ct);
        var episodeTasks = sonarrs.Select(sonarr => CachedAsync(cache, Key(sonarr, range), CalendarTtl,
            token => arr.GetCalendarAsync<SonarrEpisode>(sonarr, start, end, token), ct)).ToList();
        var movieTasks = radarrs.Select(radarr => CachedAsync(cache, Key(radarr, range), CalendarTtl,
            token => arr.GetCalendarAsync<RadarrMovie>(radarr, start, end, token), ct)).ToList();
        var bookTasks = readarrs.Select(readarr => CachedAsync(cache, Key(readarr, range), CalendarTtl,
            token => arr.GetBookCalendarAsync(readarr, start, end, token), ct)).ToList();
        var queue = await queueTask;
        var episodeCalendars = await Task.WhenAll(episodeTasks);
        var movieCalendars = await Task.WhenAll(movieTasks);
        var bookCalendars = await Task.WhenAll(bookTasks);

        // Look up just the titles on the calendar, not every id in the library.
        var series = await SeriesIndexAsync(db, episodeCalendars.SelectMany(c => c).Select(e => e.Series?.TvdbId), ct);
        var tmdbIds = movieCalendars.SelectMany(c => c).Select(m => m.TmdbId.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
        // Not ToDictionary: a film can be in the library twice (two folders), sharing one TMDB id.
        var movies = new Dictionary<string, Guid?>(StringComparer.Ordinal);
        foreach (var m in await db.MediaItems.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null && tmdbIds.Contains(i.TmdbId)).Select(i => new { i.Id, i.TmdbId }).ToListAsync(ct))
        {
            movies.TryAdd(m.TmdbId!, m.Id);
        }
        var bookIds = bookCalendars.SelectMany(c => c).Select(b => b.Id).OfType<string>().Distinct().ToList();
        var elsewhere = await HiddenTitles.ForAsync(db, options.PathMappings,
            episodeCalendars.SelectMany(c => c).Select(e => e.Series?.TvdbId), tmdbIds, bookIds, ct);
        var results = new List<UpcomingDto>();

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
                results.Add(new UpcomingDto(
                    $"sonarr-{sonarr.Name}-{episode.Id}", MediaKind.Episode, episode.Title ?? "TBA", show?.Title,
                    episode.SeasonNumber, episode.EpisodeNumber, date, "Airs", episode.Overview,
                    Remote(show?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(episode.Images.FirstOrDefault(i => i.CoverType == "screenshot")?.RemoteUrl ?? show?.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    itemId, episode.HasFile, download, sonarr.Name,
                    show?.TitleSlug is { } slug ? WebUrl(sonarr, host, $"series/{slug}") : null));
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
                results.Add(new UpcomingDto(
                    $"radarr-{radarr.Name}-{movie.Id}", MediaKind.Movie, movie.Title, null, null, null, date, when, movie.Overview,
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                    Remote(movie.Images.FirstOrDefault(i => i.CoverType == "fanart")?.RemoteUrl),
                    movies.GetValueOrDefault(movie.TmdbId.ToString(CultureInfo.InvariantCulture)), movie.HasFile, download, radarr.Name,
                    movie.TitleSlug is { } slug ? WebUrl(radarr, host, $"movie/{slug}") : null, movie.TmdbId));
            }
        }

        // Books by authors the app follows, on their release day. Shown only to profiles that can see an audiobooks library.
        // Through MediaItems, whose filter hides the libraries this profile can't see: no id of a book in one.
        var ownBooks = bookIds.Count == 0 ? [] : await (
            from d in db.BookDetails
            join i in db.MediaItems on d.MediaItemId equals i.Id
            where d.ArrBookId != null && bookIds.Contains(d.ArrBookId)
            select new { d.ArrBookId, d.ArrInstance, d.MediaItemId }).ToListAsync(ct);
        foreach (var (readarr, calendar) in readarrs.Zip(bookCalendars))
        {
            foreach (var book in calendar.Where(b => b.ReleaseDate is not null))
            {
                var own = ownBooks.FirstOrDefault(o => o.ArrBookId == book.Id && o.ArrInstance == readarr.Name)?.MediaItemId;
                if (own is null && elsewhere.HidesBook(book.Id, book.Author?.Path))
                {
                    continue;
                }

                queue.TryGetValue($"readarr-{book.Id}", out var download);
                var cover = book.Images.Concat(book.Edition?.Images ?? []).FirstOrDefault(i => i.CoverType is "cover" or "poster")?.RemoteUrl;
                results.Add(new UpcomingDto(
                    $"readarr-{readarr.Name}-{book.Id}", MediaKind.Audiobook, book.Title ?? "Untitled", book.Author?.AuthorName ?? book.AuthorTitle,
                    null, null, book.ReleaseDate!.Value, "Out", book.Overview, Remote(cover), null,
                    own, false, download, readarr.Name, null));
            }
        }

        // Something in no library yet is shown unless the profile can't see any library of its kind.
        var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
        var noBooks = !await db.Libraries.AnyAsync(l => l.Type == LibraryType.Audiobooks, ct);
        return results.Where(r => r.LibraryItemId is not null || !hidden.Hides(r.Kind))
            .Where(r => !(noBooks && r.Kind == MediaKind.Audiobook)).OrderBy(r => r.Date).ToList();
    }

    public static async Task<List<DownloadDto>> DownloadsAsync(FlexarrOptions options, ArrClient arr, IMemoryCache? cache, FlexarrDbContext db, string? host, CancellationToken ct)
    {
        var instances = options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr or ArrKind.Readarr).ToList();
        var queues = await Task.WhenAll(instances.Select(instance => QueueOfAsync(instance, arr, cache, ct)));
        var series = await SeriesIndexAsync(db, queues.SelectMany(q => q).Select(q => q.Series?.TvdbId), ct);
        var queued = queues.SelectMany(q => q).ToList();
        var elsewhere = await HiddenTitles.ForAsync(db, options.PathMappings, queued.Select(q => q.Series?.TvdbId),
            queued.Select(q => q.Movie?.TmdbId.ToString(CultureInfo.InvariantCulture)).OfType<string>(),
            queued.Select(q => q.BookId ?? q.Book?.Id).OfType<string>(), ct);
        var downloads = new List<DownloadDto>();
        foreach (var (instance, queue) in instances.Zip(queues))
        {
            foreach (var q in queue)
            {
                var hiddenHere = instance.Kind switch
                {
                    ArrKind.Sonarr => elsewhere.HidesSeries(q.Series?.TvdbId, q.Series?.Path, series),
                    ArrKind.Readarr => elsewhere.HidesBook(q.BookId ?? q.Book?.Id, q.Book?.Author?.Path),
                    _ => q.Movie is { } m && elsewhere.HidesMovie(m.TmdbId, m.Path, null),
                };
                if (hiddenHere)
                {
                    continue;
                }

                var state = new DownloadStateDto(Math.Round(q.Progress, 3), q.Timeleft, q.TrackedDownloadState ?? q.Status);
                var queuePage = WebUrl(instance, host, "activity/queue");
                if (instance.Kind == ArrKind.Readarr)
                {
                    downloads.Add(new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Audiobook, q.Book?.Title ?? q.Title ?? "Book",
                        q.Book?.Author?.AuthorName ?? q.Book?.AuthorTitle, null, null,
                        Remote(q.Book?.Images.FirstOrDefault(i => i.CoverType is "cover" or "poster")?.RemoteUrl), null, state, instance.Name, queuePage));
                    continue;
                }

                downloads.Add(instance.Kind == ArrKind.Sonarr
                    ? new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Episode, q.Episode?.Title ?? q.Title ?? "Episode", q.Series?.Title,
                        q.Episode?.SeasonNumber, q.Episode?.EpisodeNumber, Remote(q.Series?.Images.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl),
                        q.Series is null ? null : series.GetValueOrDefault(q.Series.TvdbId.ToString(CultureInfo.InvariantCulture)), state, instance.Name, queuePage)
                    : new DownloadDto($"q-{instance.Name}-{q.Id}", MediaKind.Movie, q.Movie?.Title ?? q.Title ?? "Film", null, null, null,
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
        var instances = options.Arr.Where(a => a.Kind is ArrKind.Sonarr or ArrKind.Radarr or ArrKind.Readarr).ToList();
        var queues = await Task.WhenAll(instances.Select(instance => QueueOfAsync(instance, arr, cache, ct)));
        var map = new Dictionary<string, DownloadStateDto>(StringComparer.Ordinal);
        foreach (var (instance, queue) in instances.Zip(queues))
        {
            foreach (var q in queue)
            {
                var key = instance.Kind switch
                {
                    ArrKind.Sonarr => $"sonarr-{q.EpisodeId}",
                    ArrKind.Readarr => $"readarr-{q.BookId ?? q.Book?.Id}",
                    _ => $"radarr-{q.MovieId}",
                };
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
        private static readonly HiddenTitles None = new([], [], [], [], [], []);
        private readonly HashSet<string> _series;
        private readonly HashSet<string> _movies;
        private readonly HashSet<string> _books;
        private readonly List<string> _hiddenFolders;
        private readonly List<string> _visibleFolders;
        private readonly IReadOnlyList<PathMapping> _mappings;

        private HiddenTitles(HashSet<string> series, HashSet<string> movies, HashSet<string> books, List<string> hiddenFolders, List<string> visibleFolders, IReadOnlyList<PathMapping> mappings)
        {
            (_series, _movies, _books, _hiddenFolders, _visibleFolders, _mappings) = (series, movies, books, hiddenFolders, visibleFolders, mappings);
        }

        public static async Task<HiddenTitles> ForAsync(FlexarrDbContext db, IReadOnlyList<PathMapping> mappings, IEnumerable<int?> tvdbIds, IEnumerable<string> tmdbIds,
            IEnumerable<string> bookIds, CancellationToken ct)
        {
            var hidden = db.HiddenLibraryIds.ToList();
            if (hidden.Count == 0)
            {
                return None;
            }

            var tvdb = tvdbIds.OfType<int>().Distinct().Select(id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var tmdb = tmdbIds.Distinct().ToList();
            var books = bookIds.Distinct().ToList();
            var items = db.MediaItems.IgnoreQueryFilters().Where(i => hidden.Contains(i.LibraryId));
            var series = tvdb.Count == 0 ? [] : await items.Where(i => i.Kind == MediaKind.Series && i.TvdbId != null && tvdb.Contains(i.TvdbId)).Select(i => i.TvdbId!).ToListAsync(ct);
            var movies = tmdb.Count == 0 ? [] : await items.Where(i => i.Kind == MediaKind.Movie && i.TmdbId != null && tmdb.Contains(i.TmdbId)).Select(i => i.TmdbId!).ToListAsync(ct);
            var ownBooks = books.Count == 0 ? [] : await (
                from d in db.BookDetails
                join i in items on d.MediaItemId equals i.Id
                where d.ArrBookId != null && books.Contains(d.ArrBookId)
                select d.ArrBookId!).ToListAsync(ct);
            var folders = await db.LibraryFolders.Select(f => new { f.LibraryId, f.Path }).ToListAsync(ct);
            return new HiddenTitles([.. series], [.. movies], [.. ownBooks],
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

        public bool HidesBook(string? arrBookId, string? authorPath) => (arrBookId is not null && _books.Contains(arrBookId)) || InHiddenFolder(authorPath);

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
