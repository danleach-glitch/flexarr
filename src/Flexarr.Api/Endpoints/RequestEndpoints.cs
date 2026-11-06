using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints.MusicRequests;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;
using Flexarr.Core.Localization;

namespace Flexarr.Api.Endpoints;

public enum AvailabilityDto
{
    /// <summary>Not in the library and not requested.</summary>
    None,
    Requested,
    PartlyAvailable,
    Available,
}

/// <summary>A film or show from outside the library (via Seerr), with whether you have it.</summary>
/// <param name="OnWatchlist">Whether it's on the profile's watchlist (absent from servers before 0.56).</param>
public sealed record ExternalTitleDto(
    int TmdbId,
    string MediaType,
    string Title,
    int? Year,
    string? Overview,
    string? Poster,
    string? Backdrop,
    double Rating,
    AvailabilityDto Availability,
    Guid? LibraryItemId,
    bool? OnWatchlist = null);

public sealed record ExternalRowDto(string Id, string Title, IReadOnlyList<ExternalTitleDto> Items);

/// <param name="Seasons">For a show, just these seasons; null or empty for every one (absent from servers before 0.59).</param>
public sealed record TitleRequest(string MediaType, int TmdbId, IReadOnlyList<int>? Seasons = null);

/// <summary>A library requests can be added to, for the "new shows go to" setting.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="IsDefault">Where Seerr adds them when a profile hasn't chosen.</param>
public sealed record RequestDestinationDto(Guid Id, string Name, string MediaType, bool IsDefault);

public sealed record ExternalPersonDto(string Name, string? Character, string? Image);

/// <summary>Everything worth knowing about a film or show you don't have, from Seerr (which gets it from TMDB).</summary>
/// <param name="OnWatchlist">Whether it's on the profile's watchlist (absent from servers before 0.56).</param>
/// <param name="CriticScore">Rotten Tomatoes critics, percent.</param>
/// <param name="AudienceScore">Rotten Tomatoes audience, percent.</param>
/// <param name="RottenTomatoesState">The tomato for <paramref name="CriticScore"/> (absent from servers before 0.59).</param>
public sealed record ExternalDetailDto(
    int TmdbId,
    string MediaType,
    string Title,
    string? Tagline,
    int? Year,
    string? Overview,
    string? Poster,
    string? Backdrop,
    IReadOnlyList<string> Genres,
    int? RuntimeMinutes,
    int? Seasons,
    string? Certification,
    string? Status,
    double? TmdbRating,
    int? CriticScore,
    int? AudienceScore,
    double? ImdbRating,
    string? TrailerYouTubeId,
    IReadOnlyList<ExternalPersonDto> Cast,
    IReadOnlyList<string> MadeBy,
    string? Studio,
    AvailabilityDto Availability,
    Guid? LibraryItemId,
    IReadOnlyList<ExternalTitleDto> Recommendations,
    bool? OnWatchlist = null,
    RottenTomatoesState? RottenTomatoesState = null);

public static partial class RequestEndpoints
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "{Service} refused or didn't answer taking a request back ({Status})")]
    private static partial void LogUndoRefused(ILogger logger, Exception ex, string service, int? status);

    private static readonly (string Id, string List, string Title)[] Lists =
    [
        ("trending", "trending", Loc.Mark("Trending this week")),
        ("movies", "movies", Loc.Mark("Popular movies")),
        ("tv", "tv", Loc.Mark("Popular new shows")),
        ("upcoming", "movies/upcoming", Loc.Mark("Coming to cinemas")),
    ];

    /// <summary>
    /// TMDB's popular shows are mostly long-running ones (Grey's Anatomy, Friends), so that row keeps to shows that
    /// started in the last year, up to today so it isn't filled with ones yet to air.
    /// </summary>
    internal static string FilterFor(string id, DateOnly today) => id switch
    {
        "tv" => $"firstAirDateGte={today.AddYears(-1):yyyy-MM-dd}&firstAirDateLte={today:yyyy-MM-dd}",
        _ => "",
    };

    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder api)
    {
        // Profiles with an age limit don't request: Seerr's titles have no ages Flexarr can check.
        var group = api.MapGroup("/requests").WithTags("Requests").NotForAgeLimited();
        api.MapMusicRequestEndpoints(group);

        // Whether requests are set up, so clients can hide the feature otherwise. To a profile with an age limit, they aren't.
        api.MapGet("/requests/status", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CancellationToken ct) =>
        {
            var instance = db.IsLimited() ? null : options.CurrentValue.Seerr?.Instance;
            return new { configured = instance is not null, reachable = instance is not null && await SafePingAsync(seerr, instance, ct) };
        }).WithTags("Requests");

        // The libraries this profile's requests could go to (every one, for the admin choosing for others).
        group.MapGet("/destinations", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CurrentProfile current, CancellationToken ct) =>
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Ok(Array.Empty<RequestDestinationDto>());
            }

            try
            {
                var libraries = current.IsAdmin ? db.Libraries.IgnoreQueryFilters() : db.Libraries;
                var destinations = await RequestDestinations.ListAsync(seerr, instance, options.CurrentValue.PathMappings, libraries, ct);
                return Results.Ok(destinations.Select(d => new RequestDestinationDto(d.LibraryId, d.Name, d.Kind == MediaKind.Movie ? "movie" : "tv", d.IsDefault)).ToList());
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("Seerr didn’t say where it can add requests ({status}).", new { status = (int?)ex.StatusCode ?? 0 }, status: 502);
            }
        });

        group.MapGet("/discover", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, Watchlist.WatchlistService watchlist,
            Trakt.TraktRecommendations trakt, CurrentProfile current, CancellationToken ct) =>
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Ok(Array.Empty<ExternalRowDto>());
            }

            var library = await LibraryIndexAsync(db, ct);
            var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
            var wanted = await watchlist.KeysAsync(current.Id, ct);
            var rows = new List<ExternalRowDto>();
            var today = DateOnly.FromDateTime(DateTime.UtcNow);
            // Trakt's picks are worked out alongside Seerr's lists (they only use Seerr and Trakt from here on).
            var picksTask = trakt.RowAsync(current.Id, instance, library, wanted, hidden, ct);
            ExternalRowDto? picks;
            try
            {
                foreach (var (id, list, title) in Lists)
                {
                    try
                    {
                        var page = await seerr.DiscoverAsync(instance, list, 1, ct, FilterFor(id, today));
                        rows.Add(new ExternalRowDto(id, Loc.T(title), page.Results.Where(r => r.MediaType is "movie" or "tv").Select(r => ToDto(r, library, wanted))
                            .Where(t => t.LibraryItemId is not null || !hidden.Hides(KindOf(t.MediaType))).ToList()));
                    }
                    catch (HttpRequestException)
                    {
                        // One list failing shouldn't hide the others.
                    }
                }
            }
            finally
            {
                // Never left running past the request, which owns the database context it uses.
                picks = await picksTask;
            }

            // A profile connected to Trakt gets its picks, after what's trending.
            if (picks is not null)
            {
                rows.Insert(Math.Min(1, rows.Count), picks);
            }

            return Results.Ok(rows);
        });

        // Search everything (not just the library): films and shows TMDB knows, marked if you have them.
        group.MapGet("/search", async (string q, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, Watchlist.WatchlistService watchlist,
            CurrentProfile current, CancellationToken ct) =>
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance || string.IsNullOrWhiteSpace(q))
            {
                return Results.Ok(Array.Empty<ExternalTitleDto>());
            }

            try
            {
                var page = await seerr.SearchAsync(instance, q.Trim(), ct);
                var library = await LibraryIndexAsync(db, ct);
                var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
                var wanted = await watchlist.KeysAsync(current.Id, ct);
                return Results.Ok(page.Results.Where(r => r.MediaType is "movie" or "tv").Select(r => ToDto(r, library, wanted))
                    .Where(t => t.LibraryItemId is not null || !hidden.Hides(KindOf(t.MediaType))).ToList());
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("Seerr didn’t answer the search ({status}).", new { status = (int?)ex.StatusCode ?? 0 }, status: 502);
            }
        });

        group.MapGet("/title/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db,
            Watchlist.WatchlistService watchlist, CurrentProfile current, CancellationToken ct) =>
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance || mediaType is not ("movie" or "tv"))
            {
                return Results.NotFound();
            }

            var library = await LibraryIndexAsync(db, ct);
            var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
            library.TryGetValue($"{mediaType}:{tmdbId.ToString(CultureInfo.InvariantCulture)}", out var itemId);
            if (itemId == Guid.Empty && hidden.Hides(KindOf(mediaType)))
            {
                return Results.NotFound();
            }

            try
            {
                if (await seerr.GetAsync(instance, $"{mediaType}/{tmdbId}", ct) is not { } title)
                {
                    return Results.NotFound();
                }

                var ratings = await SafeGetAsync(seerr, instance, SeerrRatings.PathFor(mediaType == "movie", tmdbId), ct);
                var recommendations = await SafeGetAsync(seerr, instance, $"{mediaType}/{tmdbId}/recommendations", ct);
                var onWatchlist = (await watchlist.MembershipAsync(current.Id, new Watchlist.WatchlistAdd(null, mediaType, tmdbId), ct)).OnWatchlist;
                var wanted = await watchlist.KeysAsync(current.Id, ct);
                return Results.Ok(Detail(mediaType, title, ratings, recommendations, library, hidden, wanted) with { OnWatchlist = onWatchlist });
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("Seerr didn’t answer ({status}).", new { status = (int?)ex.StatusCode ?? 0 }, status: 502);
            }
        });

        group.MapPost("/", async (TitleRequest request, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CurrentProfile current,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(KindOf(request.MediaType)))
            {
                return request.MediaType == "movie"
                    ? Problems.Of("This profile can’t request movies.", status: 403)
                    : Problems.Of("This profile can’t request shows.", status: 403);
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Problems.Of("Requests need a Seerr server. Add one under Settings › Connections.", status: 409);
            }

            if (request.MediaType is not ("movie" or "tv"))
            {
                return Problems.Field("Use \"movie\" or \"tv\".", field: "mediaType");
            }

            if (await OverDailyLimitAsync(db, cache, current.Id, ct))
            {
                return Problems.Of("That’s a lot of requests for one day. Try again tomorrow.", status: 429);
            }

            try
            {
                var seasons = request.MediaType == "tv" && request.Seasons is { Count: > 0 } chosen ? chosen.Where(n => n > 0).Distinct().Order().ToList() : null;
                await PlaceAsync(seerr, instance, options.CurrentValue, db, current.Id, request.MediaType, request.TmdbId, ct, seasons);
                CountAsk(cache, current.Id);
                RequestRequested.Forget();
                if (seasons is not null)
                {
                    // The show's page asks Sonarr again, so the seasons show as requested.
                    var tmdb = request.TmdbId.ToString(CultureInfo.InvariantCulture);
                    foreach (var showId in await db.MediaItems.Where(i => i.Kind == MediaKind.Series && i.TmdbId == tmdb).Select(i => i.Id).ToListAsync(ct))
                    {
                        ShowMissingEndpoints.Forget(cache, options.CurrentValue, showId);
                    }
                }

                return Results.Accepted();
            }
            catch (HttpRequestException ex)
            {
                return Problems.Of("Seerr didn’t accept the request ({status}). It may already be requested.", new { status = (int?)ex.StatusCode ?? 0 }, status: 502);
            }
            catch (NotRequestableException ex)
            {
                return Results.Problem(ex.Message, statusCode: 409);
            }
        });

        // Every film and show Seerr is waiting for, with how far along each is. Given a library, only titles in it
        // (that library's Requested view).
        group.MapGet("/requested", async (Guid? libraryId, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, ArrClient arr, FlexarrDbContext db,
            Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            Library? library = null;
            if (libraryId is { } id)
            {
                library = await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id && (l.Type == LibraryType.Movies || l.Type == LibraryType.TvShows), ct);
                if (library is null)
                {
                    return Results.NotFound();
                }
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Ok(Array.Empty<TitleRequestedDto>());
            }

            return await RequestRequested.ListAsync(instance, seerr, arr, options.CurrentValue, db, cache, library, ct) is { } listed
                ? Results.Ok(listed)
                : Problems.Of("Seerr isn’t answering. Try again later.", status: 502);
        });

        // Take a film or show request back, for the household: Seerr's request goes, Radarr or Sonarr stop wanting it.
        group.MapPost("/unrequest", async (TitleUndoBody body, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, ArrClient arr, FlexarrDbContext db,
            CurrentProfile current, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (body.MediaType is not ("movie" or "tv"))
            {
                return Problems.Field("Use \"movie\" or \"tv\".", field: "mediaType");
            }

            if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(KindOf(body.MediaType)))
            {
                return body.MediaType == "movie"
                    ? Problems.Of("This profile can’t request movies.", status: 403)
                    : Problems.Of("This profile can’t request shows.", status: 403);
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Problems.Of("Requests need a Seerr server. Add one under Settings › Connections.", status: 409);
            }

            if (MusicRequestEndpoints.UndoRefused(current, cache, titles: true) is { } refused)
            {
                return refused;
            }

            try
            {
                return MusicRequestEndpoints.Counted(current, cache,
                    await RequestUndo.UnrequestAsync(body, instance, seerr, arr, options.CurrentValue, db, cache, ct));
            }
            catch (RequestUndo.ServiceException ex)
            {
                var status = (int?)(ex.InnerException as HttpRequestException)?.StatusCode;
                LogUndoRefused(loggers.CreateLogger("Flexarr.Requests"), ex.InnerException!, ex.Service, status);
                return status is >= 400 and < 500
                    ? Problems.Of("{service} turned that down.", new { service = ex.Service }, status: 502)
                    : Problems.Of("{service} isn’t answering. Try again later.", new { service = ex.Service }, status: 502);
            }
        });

        return api;
    }

    /// <summary>
    /// The most films and shows one profile can ask for in a day (music has its own,
    /// <see cref="MusicRequestEndpoints.DailyLimit"/>): Seerr approves them with Flexarr's key, and Radarr and Sonarr
    /// download what it approves, so without one a guest's device or a teenager's profile could fill the disk.
    /// </summary>
    internal const int DailyLimit = 50;

    /// <summary>
    /// Whether <paramref name="profileId"/> has asked for its day's worth of films and shows: its requests in the last
    /// day, or what it has asked since (asking again for the same title reuses its request but still reaches Seerr).
    /// Never the main profile, which manages Sonarr and Radarr anyway.
    /// </summary>
    internal static async Task<bool> OverDailyLimitAsync(FlexarrDbContext db, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string profileId, CancellationToken ct)
    {
        if (profileId == PlaybackState.DefaultUser)
        {
            return false;
        }

        var since = DateTime.UtcNow.AddDays(-1);
        return AskCount(cache, profileId).Value >= DailyLimit
            || await db.MediaRequests.CountAsync(r => r.ProfileId == profileId && (r.Kind == MediaKind.Movie || r.Kind == MediaKind.Series) && r.RequestedAt > since, ct)
            >= DailyLimit;
    }

    /// <summary>Counts a film or show request that reached Seerr towards <paramref name="profileId"/>'s day.</summary>
    internal static void CountAsk(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string profileId) =>
        Interlocked.Increment(ref AskCount(cache, profileId).Value);

    private static System.Runtime.CompilerServices.StrongBox<int> AskCount(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, string profileId) =>
        Microsoft.Extensions.Caching.Memory.CacheExtensions.GetOrCreate(cache, $"title-ask:{profileId}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            return new System.Runtime.CompilerServices.StrongBox<int>();
        })!;

    /// <summary>
    /// Asks Seerr for a title for a profile, sent to the library it chose, and remembers who asked (Seerr only knows
    /// Flexarr did) so they hear when it's in. Throws <see cref="HttpRequestException"/> when Seerr refuses.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    /// <param name="seasons">For a show, just these seasons; null for every one.</param>
    internal static async Task PlaceAsync(SeerrClient seerr, SeerrInstance instance, FlexarrOptions options, FlexarrDbContext db, string profileId, string mediaType,
        int tmdbId, CancellationToken ct, IReadOnlyList<int>? seasons = null)
    {
        var kind = KindOf(mediaType);
        // Sonarr adds shows by their TheTVDB id. Seerr takes a show without one, then drops it while saying it's
        // requested; such a show is often specials TheTVDB files under another (a Halloween special, say).
        if (mediaType == "tv" && await seerr.GetAsync(instance, $"tv/{tmdbId}", ct) is { } show && LacksTvdbId(show))
        {
            throw new NotRequestableException(Loc.T("Sonarr can’t add this show: TheTVDB doesn’t list it. It may be part of another show."));
        }

        var to = await RequestDestinations.ForRequestAsync(seerr, instance, options.PathMappings, db, profileId, kind, ct);
        await seerr.RequestAsync(instance, mediaType, tmdbId, to, ct, seasons);
        if (!await db.MediaRequests.AnyAsync(r => r.ProfileId == profileId && r.Kind == kind && r.TmdbId == tmdbId && r.FulfilledAt == null, ct))
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = profileId, Kind = kind, TmdbId = tmdbId });
            await db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Seerr's show says it has no TheTVDB id (and not just nothing about its ids).</summary>
    private static bool LacksTvdbId(JsonElement show) =>
        show.ValueKind == JsonValueKind.Object && show.TryGetProperty("externalIds", out var ids) && ids.ValueKind == JsonValueKind.Object
        && !(ids.TryGetProperty("tvdbId", out var tvdb) && tvdb.ValueKind == JsonValueKind.Number && tvdb.GetInt64() > 0);

    /// <summary>What Seerr's status for a title (from <c>mediaInfo.status</c>) means for whether you can have it.</summary>
    internal static AvailabilityDto AvailabilityOf(int? seerrStatus) => seerrStatus switch
    {
        5 => AvailabilityDto.Available,
        4 => AvailabilityDto.PartlyAvailable,
        2 or 3 => AvailabilityDto.Requested,
        _ => AvailabilityDto.None,
    };

    private static async Task<JsonElement?> SafeGetAsync(SeerrClient seerr, SeerrInstance instance, string path, CancellationToken ct)
    {
        try
        {
            return await seerr.GetAsync(instance, path, ct);
        }
        catch (HttpRequestException)
        {
            return null; // Ratings and recommendations are extras.
        }
    }

    private static ExternalDetailDto Detail(string mediaType, JsonElement t, JsonElement? ratings, JsonElement? recommendations, Dictionary<string, Guid> library,
        IReadOnlySet<Core.Libraries.LibraryType> hidden, IReadOnlySet<string>? watchlist = null)
    {
        var movie = mediaType == "movie";
        var summary = ToDto(t.Deserialize<SeerrResult>(Web)! with { MediaType = mediaType }, library);
        IEnumerable<JsonElement> Array(JsonElement e, params string[] path)
        {
            foreach (var name in path)
            {
                if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out e))
                {
                    return [];
                }
            }

            return e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
        }

        // A US certificate, else British; Seerr lists every country's.
        string? certification = movie
            ? CertificateCountries.Select(country => Array(t, "releases", "results").Where(r => Str(r, "iso_3166_1") == country)
                .SelectMany(r => Array(r, "release_dates")).Select(d => Str(d, "certification")).FirstOrDefault(c => !string.IsNullOrEmpty(c))).FirstOrDefault(c => c is not null)
            : CertificateCountries.Select(country => Array(t, "contentRatings", "results").Where(r => Str(r, "iso_3166_1") == country).Select(r => Str(r, "rating")).FirstOrDefault())
                .FirstOrDefault(c => !string.IsNullOrEmpty(c));

        var videos = Array(t, "relatedVideos").Where(v => Str(v, "site") == "YouTube").ToList();
        var trailer = videos.Where(v => Str(v, "type") == "Trailer").OrderByDescending(v => Str(v, "name")?.Contains("Official", StringComparison.OrdinalIgnoreCase) == true)
            .Concat(videos.Where(v => Str(v, "type") == "Teaser")).Select(v => Str(v, "key")).FirstOrDefault();

        var scores = SeerrRatings.Parse(ratings, movie);

        var recs = recommendations is { } rec
            ? Array(rec, "results").Select(e => e.Deserialize<SeerrResult>(Web)!).Where(e => e.MediaType is "movie" or "tv")
                .Select(e => ToDto(e, library, watchlist)).Where(e => e.LibraryItemId is not null || !hidden.Hides(KindOf(e.MediaType))).Take(20).ToList()
            : [];

        return new ExternalDetailDto(
            summary.TmdbId, mediaType, summary.Title, NullIfEmpty(Str(t, "tagline")), summary.Year, summary.Overview, summary.Poster, summary.Backdrop,
            Array(t, "genres").Select(g => Str(g, "name")).OfType<string>().ToList(),
            movie ? (int?)Number(t, "runtime") : Array(t, "episodeRunTime").Select(e => (int?)e.GetDouble()).FirstOrDefault(),
            movie ? null : (int?)Number(t, "numberOfSeasons"),
            certification, Str(t, "status"),
            Number(t, "voteAverage") is > 0 and var vote ? Math.Round(vote, 1) : null,
            scores?.CriticScore,
            scores?.AudienceScore,
            scores?.Imdb,
            trailer,
            Array(t, "credits", "cast").OrderBy(c => Number(c, "order")).Take(20)
                .Select(c => new ExternalPersonDto(Str(c, "name") ?? "", NullIfEmpty(Str(c, "character")), Tmdb(Str(c, "profilePath"), "w185"))).ToList(),
            (movie ? Array(t, "credits", "crew").Where(c => Str(c, "job") == "Director") : Array(t, "createdBy")).Select(c => Str(c, "name")).OfType<string>().Distinct().ToList(),
            (movie ? Array(t, "productionCompanies") : Array(t, "networks")).Select(c => Str(c, "name")).FirstOrDefault(),
            summary.Availability, summary.LibraryItemId, recs)
        {
            RottenTomatoesState = RottenTomatoesDto.Of(scores?.CriticScore, scores?.Certified == true)?.State,
        };
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private static readonly string[] CertificateCountries = ["US", "GB"];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Number(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    internal static MediaKind KindOf(string mediaType) => mediaType == "movie" ? MediaKind.Movie : MediaKind.Series;

    /// <param name="watchlist">The profile's watchlist ("movie:123", "tv:456"), to mark titles on it; null leaves that out.</param>
    internal static ExternalTitleDto ToDto(SeerrResult r, Dictionary<string, Guid> library, IReadOnlySet<string>? watchlist = null)
    {
        var key = $"{r.MediaType}:{r.Id.ToString(CultureInfo.InvariantCulture)}";
        var inLibrary = library.TryGetValue(key, out var itemId);
        var availability = inLibrary ? AvailabilityDto.Available : AvailabilityOf(r.MediaInfo?.Status);
        var date = r.ReleaseDate ?? r.FirstAirDate;
        return new ExternalTitleDto(
            r.Id, r.MediaType, r.Title ?? r.Name ?? "Untitled",
            date is { Length: >= 4 } && int.TryParse(date[..4], CultureInfo.InvariantCulture, out var y) ? y : null,
            r.Overview,
            Tmdb(r.PosterPath, "w500"),
            Tmdb(r.BackdropPath, "w1280"),
            Math.Round(r.VoteAverage, 1),
            availability,
            inLibrary ? itemId : null,
            watchlist?.Contains(key));
    }

    internal static string? Tmdb(string? path, string size) =>
        path is null ? null : ImageEndpoints.RemoteUrl($"https://image.tmdb.org/t/p/{size}{path}");

    /// <summary>"movie:123" / "tv:456" (TMDB ids) → library item.</summary>
    internal static async Task<Dictionary<string, Guid>> LibraryIndexAsync(FlexarrDbContext db, CancellationToken ct)
    {
        var map = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var i in await db.MediaItems.Where(i => i.TmdbId != null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series))
                     .Select(i => new { i.Id, i.Kind, i.TmdbId }).ToListAsync(ct))
        {
            map.TryAdd($"{(i.Kind == MediaKind.Movie ? "movie" : "tv")}:{i.TmdbId}", i.Id);
        }

        return map;
    }

    private static async Task<bool> SafePingAsync(SeerrClient seerr, SeerrInstance instance, CancellationToken ct)
    {
        try
        {
            return await seerr.PingAsync(instance, ct);
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}

/// <summary>A title that can't be requested, and why, in words for whoever asked.</summary>
public sealed class NotRequestableException(string message) : Exception(message);
