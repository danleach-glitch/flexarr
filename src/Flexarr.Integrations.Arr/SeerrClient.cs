using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Integrations.Arr;

/// <summary>A Seerr (or Overseerr/Jellyseerr) server, used for discovery and requests.</summary>
public sealed record SeerrInstance(Uri BaseUrl, string ApiKey);

public sealed record SeerrMediaInfo(int Status);

public sealed record SeerrResult
{
    public int Id { get; init; }
    public string MediaType { get; init; } = "";
    public string? Title { get; init; }
    public string? Name { get; init; }
    public string? Overview { get; init; }
    public string? PosterPath { get; init; }
    public string? BackdropPath { get; init; }
    public string? ReleaseDate { get; init; }
    public string? FirstAirDate { get; init; }
    public double VoteAverage { get; init; }
    public SeerrMediaInfo? MediaInfo { get; init; }
}

public sealed record SeerrPage(int Page, int TotalPages, IReadOnlyList<SeerrResult> Results);

/// <summary>Talks to Seerr's API: trending and popular lists (from TMDB, via Seerr) and requests.</summary>
public sealed class SeerrClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="list">trending, movies, tv or movies/upcoming.</param>
    public async Task<SeerrPage> DiscoverAsync(SeerrInstance seerr, string list, int page, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Get, $"discover/{list}?page={page}");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SeerrPage>(Json, ct).ConfigureAwait(false) ?? new SeerrPage(page, 0, []);
    }

    /// <summary>Requests a film, or every season of a show; Seerr passes it to Radarr or Sonarr.</summary>
    public async Task RequestAsync(SeerrInstance seerr, string mediaType, int tmdbId, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Post, "request");
        request.Content = JsonContent.Create(mediaType == "tv"
            ? new { mediaType, mediaId = tmdbId, seasons = "all" }
            : (object)new { mediaType, mediaId = tmdbId }, options: Json);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task<bool> PingAsync(SeerrInstance seerr, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Get, "settings/main");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private static HttpRequestMessage Request(SeerrInstance seerr, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri($"{seerr.BaseUrl.ToString().TrimEnd('/')}/api/v1/{path}"));
        request.Headers.Add("X-Api-Key", seerr.ApiKey);
        return request;
    }
}
