using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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

public sealed record TitleRequest(string MediaType, int TmdbId);

/// <summary>A library requests can be added to, for the "new shows go to" setting.</summary>
/// <param name="MediaType">"movie" or "tv".</param>
/// <param name="IsDefault">Where Seerr adds them when a profile hasn't chosen.</param>
public sealed record RequestDestinationDto(Guid Id, string Name, string MediaType, bool IsDefault);

public sealed record ExternalPersonDto(string Name, string? Character, string? Image);

/// <summary>Everything worth knowing about a film or show you don't have, from Seerr (which gets it from TMDB).</summary>
/// <param name="OnWatchlist">Whether it's on the profile's watchlist (absent from servers before 0.56).</param>
/// <param name="CriticScore">Rotten Tomatoes critics, percent.</param>
/// <param name="AudienceScore">Rotten Tomatoes audience, percent.</param>
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
    bool? OnWatchlist = null);

public static class RequestEndpoints
{
    private static readonly (string Id, string List, string Title)[] Lists =
    [
        ("trending", "trending", "Trending this week"),
        ("movies", "movies", "Popular films"),
        ("tv", "tv", "Popular new shows"),
        ("upcoming", "movies/upcoming", "Coming to cinemas"),
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

        // Whether requests are set up, so clients can hide the feature otherwise. To a profile with an age limit, they aren't.
        api.MapGet("/requests/status", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CancellationToken ct) =>
        {
            var instance = db.IsLimited() ? null : options.CurrentValue.Seerr?.Instance;
            // Books are requested from Readarr, Bookshelf or Chaptarr, not Seerr. Absent from servers before 0.58.
            var books = !db.IsLimited() && Books.BookRequestEndpoints.Instance(options.CurrentValue) is not null
                && await db.Libraries.AnyAsync(l => l.Type == Flexarr.Core.Libraries.LibraryType.Audiobooks, ct);
            return new { configured = instance is not null, reachable = instance is not null && await SafePingAsync(seerr, instance, ct), books };
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
                return Results.Problem($"Seerr didn't say where it can add requests ({(int?)ex.StatusCode ?? 0}).", statusCode: 502);
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
                        rows.Add(new ExternalRowDto(id, title, page.Results.Where(r => r.MediaType is "movie" or "tv").Select(r => ToDto(r, library, wanted))
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
                return Results.Problem($"Seerr didn't answer the search ({(int?)ex.StatusCode ?? 0}).", statusCode: 502);
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

                var ratings = await SafeGetAsync(seerr, instance, mediaType == "movie" ? $"movie/{tmdbId}/ratingscombined" : $"tv/{tmdbId}/ratings", ct);
                var recommendations = await SafeGetAsync(seerr, instance, $"{mediaType}/{tmdbId}/recommendations", ct);
                var onWatchlist = (await watchlist.MembershipAsync(current.Id, new Watchlist.WatchlistAdd(null, mediaType, tmdbId), ct)).OnWatchlist;
                var wanted = await watchlist.KeysAsync(current.Id, ct);
                return Results.Ok(Detail(mediaType, title, ratings, recommendations, library, hidden, wanted) with { OnWatchlist = onWatchlist });
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Seerr didn't answer ({(int?)ex.StatusCode ?? 0}).", statusCode: 502);
            }
        });

        group.MapPost("/", async (TitleRequest request, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CurrentProfile current,
            CancellationToken ct) =>
        {
            if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(KindOf(request.MediaType)))
            {
                return Results.Problem(request.MediaType == "movie" ? "This profile can't request films." : "This profile can't request shows.", statusCode: 403);
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Problem("Requests need a Seerr server. Add one under Settings › Connections.", statusCode: 409);
            }

            if (request.MediaType is not ("movie" or "tv"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use \"movie\" or \"tv\"."] });
            }

            try
            {
                await PlaceAsync(seerr, instance, options.CurrentValue, db, current.Id, request.MediaType, request.TmdbId, ct);
                return Results.Accepted();
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Seerr didn't accept the request ({(int?)ex.StatusCode ?? 0}). It may already be requested.", statusCode: 502);
            }
        });

        return api;
    }

    /// <summary>
    /// Asks Seerr for a title for a profile, sent to the library it chose, and remembers who asked (Seerr only knows
    /// Flexarr did) so they hear when it's in. Throws <see cref="HttpRequestException"/> when Seerr refuses.
    /// </summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    internal static async Task PlaceAsync(SeerrClient seerr, SeerrInstance instance, FlexarrOptions options, FlexarrDbContext db, string profileId, string mediaType,
        int tmdbId, CancellationToken ct)
    {
        var kind = KindOf(mediaType);
        var to = await RequestDestinations.ForRequestAsync(seerr, instance, options.PathMappings, db, profileId, kind, ct);
        await seerr.RequestAsync(instance, mediaType, tmdbId, to, ct);
        if (!await db.MediaRequests.AnyAsync(r => r.ProfileId == profileId && r.Kind == kind && r.TmdbId == tmdbId && r.FulfilledAt == null, ct))
        {
            db.MediaRequests.Add(new MediaRequest { ProfileId = profileId, Kind = kind, TmdbId = tmdbId });
            await db.SaveChangesAsync(ct);
        }
    }

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

        var rt = ratings is { } r ? (movie ? (r.TryGetProperty("rt", out var x) ? x : default) : r) : default;
        var imdb = ratings is { } r2 && movie && r2.TryGetProperty("imdb", out var i) ? Number(i, "criticsScore") : null;

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
            rt.ValueKind == JsonValueKind.Object ? (int?)Number(rt, "criticsScore") : null,
            rt.ValueKind == JsonValueKind.Object ? (int?)Number(rt, "audienceScore") : null,
            imdb,
            trailer,
            Array(t, "credits", "cast").OrderBy(c => Number(c, "order")).Take(20)
                .Select(c => new ExternalPersonDto(Str(c, "name") ?? "", NullIfEmpty(Str(c, "character")), Tmdb(Str(c, "profilePath"), "w185"))).ToList(),
            (movie ? Array(t, "credits", "crew").Where(c => Str(c, "job") == "Director") : Array(t, "createdBy")).Select(c => Str(c, "name")).OfType<string>().Distinct().ToList(),
            (movie ? Array(t, "productionCompanies") : Array(t, "networks")).Select(c => Str(c, "name")).FirstOrDefault(),
            summary.Availability, summary.LibraryItemId, recs);
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

    private static string? Tmdb(string? path, string size) =>
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
