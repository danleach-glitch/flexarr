using System.Globalization;
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
