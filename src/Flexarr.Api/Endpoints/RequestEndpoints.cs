using System.Globalization;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
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
    Guid? LibraryItemId);

public sealed record ExternalRowDto(string Id, string Title, IReadOnlyList<ExternalTitleDto> Items);

public sealed record TitleRequest(string MediaType, int TmdbId);

public sealed record ExternalPersonDto(string Name, string? Character, string? Image);

/// <summary>Everything worth knowing about a film or show you don't have, from Seerr (which gets it from TMDB).</summary>
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
    IReadOnlyList<ExternalTitleDto> Recommendations);

public static class RequestEndpoints
{
    private static readonly (string Id, string List, string Title)[] Lists =
    [
        ("trending", "trending", "Trending this week"),
        ("movies", "movies", "Popular films"),
        ("tv", "tv", "Popular shows"),
        ("upcoming", "movies/upcoming", "Coming to cinemas"),
    ];

    public static RouteGroupBuilder MapRequestEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/requests").WithTags("Requests");

        // Whether requests are set up, so clients can hide the feature otherwise.
        group.MapGet("/status", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, CancellationToken ct) =>
        {
            var instance = options.CurrentValue.Seerr?.Instance;
            return new { configured = instance is not null, reachable = instance is not null && await SafePingAsync(seerr, instance, ct) };
        });

        group.MapGet("/discover", async (IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Ok(Array.Empty<ExternalRowDto>());
            }

            var library = await LibraryIndexAsync(db, ct);
            var hidden = await LibraryVisibility.HiddenTypesAsync(db, ct);
            var rows = new List<ExternalRowDto>();
            foreach (var (id, list, title) in Lists)
            {
                try
                {
                    var page = await seerr.DiscoverAsync(instance, list, 1, ct);
                    rows.Add(new ExternalRowDto(id, title, page.Results.Where(r => r.MediaType is "movie" or "tv").Select(r => ToDto(r, library))
                        .Where(t => t.LibraryItemId is not null || !hidden.Hides(KindOf(t.MediaType))).ToList()));
                }
                catch (HttpRequestException)
                {
                    // One list failing shouldn't hide the others.
                }
            }

            return Results.Ok(rows);
        });

        group.MapGet("/title/{mediaType}/{tmdbId:int}", async (string mediaType, int tmdbId, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CancellationToken ct) =>
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
                return Results.Ok(Detail(mediaType, title, ratings, recommendations, library, hidden));
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Seerr didn't answer ({(int?)ex.StatusCode ?? 0}).", statusCode: 502);
            }
        });

        group.MapPost("/", async (TitleRequest request, IOptionsMonitor<FlexarrOptions> options, SeerrClient seerr, FlexarrDbContext db, CancellationToken ct) =>
        {
            if ((await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(KindOf(request.MediaType)))
            {
                return Results.Problem(request.MediaType == "movie" ? "This profile can't request films." : "This profile can't request shows.", statusCode: 403);
            }

            if (options.CurrentValue.Seerr?.Instance is not { } instance)
            {
                return Results.Problem("Requests need a Seerr server. Add one under Settings › Sonarr and Radarr.", statusCode: 409);
            }

            if (request.MediaType is not ("movie" or "tv"))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["mediaType"] = ["Use \"movie\" or \"tv\"."] });
            }

            try
            {
                await seerr.RequestAsync(instance, request.MediaType, request.TmdbId, ct);
                return Results.Accepted();
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Seerr didn't accept the request ({(int?)ex.StatusCode ?? 0}). It may already be requested.", statusCode: 502);
            }
        });

        return api;
    }

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
        IReadOnlySet<Core.Libraries.LibraryType> hidden)
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
                .Select(e => ToDto(e, library)).Where(e => e.LibraryItemId is not null || !hidden.Hides(KindOf(e.MediaType))).Take(20).ToList()
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

    private static MediaKind KindOf(string mediaType) => mediaType == "movie" ? MediaKind.Movie : MediaKind.Series;

    private static ExternalTitleDto ToDto(SeerrResult r, Dictionary<string, Guid> library)
    {
        var key = $"{r.MediaType}:{r.Id.ToString(CultureInfo.InvariantCulture)}";
        var inLibrary = library.TryGetValue(key, out var itemId);
        var availability = inLibrary ? AvailabilityDto.Available : r.MediaInfo?.Status switch
        {
            5 => AvailabilityDto.Available,
            4 => AvailabilityDto.PartlyAvailable,
            2 or 3 => AvailabilityDto.Requested,
            _ => AvailabilityDto.None,
        };
        var date = r.ReleaseDate ?? r.FirstAirDate;
        return new ExternalTitleDto(
            r.Id, r.MediaType, r.Title ?? r.Name ?? "Untitled",
            date is { Length: >= 4 } && int.TryParse(date[..4], CultureInfo.InvariantCulture, out var y) ? y : null,
            r.Overview,
            Tmdb(r.PosterPath, "w500"),
            Tmdb(r.BackdropPath, "w1280"),
            Math.Round(r.VoteAverage, 1),
            availability,
            inLibrary ? itemId : null);
    }

    private static string? Tmdb(string? path, string size) =>
        path is null ? null : $"/api/v1/images/remote?url={Uri.EscapeDataString($"https://image.tmdb.org/t/p/{size}{path}")}";

    /// <summary>"movie:123" / "tv:456" (TMDB ids) → library item.</summary>
    private static async Task<Dictionary<string, Guid>> LibraryIndexAsync(FlexarrDbContext db, CancellationToken ct)
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
