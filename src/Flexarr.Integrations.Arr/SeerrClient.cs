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

/// <summary>A request Seerr holds: <see cref="Status"/> 1 pending, 2 approved, 3 declined, 4 failed, 5 completed.</summary>
/// <param name="Type">"movie" or "tv".</param>
/// <param name="Seasons">For a show, the seasons asked for.</param>
/// <param name="RootFolder">Where Seerr told Radarr or Sonarr to put it, as they see it; null for their default.</param>
public sealed record SeerrRequest(int Id, int Status, string Type, SeerrRequestMedia? Media, IReadOnlyList<SeerrRequestSeason>? Seasons, string? RootFolder,
    DateTime? CreatedAt);

/// <summary>
/// The title a request is for, as Seerr tracks it: <see cref="Status"/> 1 unknown, 2 pending, 3 processing, 4 partly
/// available, 5 available.
/// </summary>
public sealed record SeerrRequestMedia(int Id, int TmdbId, int? TvdbId, int Status, string? MediaType);

public sealed record SeerrRequestSeason(int SeasonNumber);

/// <summary>A title's own record in Seerr and the requests it holds for it (from <c>mediaInfo</c>).</summary>
public sealed record SeerrMediaRecord(int Id, int Status, IReadOnlyList<SeerrRequest>? Requests);

public sealed record SeerrPage(int Page, int TotalPages, IReadOnlyList<SeerrResult> Results);

/// <summary>One of the Sonarr or Radarr servers Seerr sends requests to.</summary>
/// <param name="ActiveDirectory">The root folder Seerr adds to unless told otherwise.</param>
public sealed record SeerrService(int Id, string Name, bool Is4k, bool IsDefault, string? ActiveDirectory);

public sealed record SeerrRootFolder(string Path);

/// <summary>Where a request goes: a server and root folder Seerr knows, instead of its defaults.</summary>
public sealed record SeerrDestination(int ServerId, string RootFolder);

/// <summary>Talks to Seerr's API: trending and popular lists (from TMDB, via Seerr) and requests.</summary>
public sealed class SeerrClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <param name="list">trending, movies, tv or movies/upcoming.</param>
    /// <param name="filter">Extra query parameters Seerr passes to TMDB, such as "firstAirDateGte=2025-01-01"; empty for none.</param>
    public async Task<SeerrPage> DiscoverAsync(SeerrInstance seerr, string list, int page, CancellationToken ct, string filter = "")
    {
        var extra = filter.Length > 0 ? $"&{filter}" : "";
        using var request = Request(seerr, HttpMethod.Get, $"discover/{list}?page={page}{extra}");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SeerrPage>(Json, ct).ConfigureAwait(false) ?? new SeerrPage(page, 0, []);
    }

    /// <summary>Searches every film, show and person TMDB knows, through Seerr.</summary>
    public async Task<SeerrPage> SearchAsync(SeerrInstance seerr, string query, CancellationToken ct)
    {
        // Seerr is strict about encoding: spaces must be %20, and some punctuation must be escaped too.
        using var request = Request(seerr, HttpMethod.Get, $"search?query={Uri.EscapeDataString(query)}&page=1");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SeerrPage>(Json, ct).ConfigureAwait(false) ?? new SeerrPage(1, 0, []);
    }

    /// <summary>Requests a film, or a show's seasons; Seerr passes it to Radarr or Sonarr.</summary>
    /// <param name="to">Where it goes; null, Seerr's default server and root folder.</param>
    /// <param name="seasons">For a show, just these seasons; null or empty for every one.</param>
    public async Task RequestAsync(SeerrInstance seerr, string mediaType, int tmdbId, SeerrDestination? to, CancellationToken ct, IReadOnlyList<int>? seasons = null)
    {
        using var request = Request(seerr, HttpMethod.Post, "request");
        var body = new Dictionary<string, object> { ["mediaType"] = mediaType, ["mediaId"] = tmdbId };
        if (mediaType == "tv")
        {
            body["seasons"] = seasons is { Count: > 0 } ? seasons : "all";
        }

        if (to is not null)
        {
            body["serverId"] = to.ServerId;
            body["rootFolder"] = to.RootFolder;
        }

        request.Content = JsonContent.Create(body, options: Json);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Every request Seerr holds (any status), newest first, up to <paramref name="limit"/>.</summary>
    public async Task<IReadOnlyList<SeerrRequest>> RequestsAsync(SeerrInstance seerr, CancellationToken ct, int limit = 500)
    {
        const int PageSize = 100;
        var all = new List<SeerrRequest>();
        for (var skip = 0; skip < limit; skip += PageSize)
        {
            using var request = Request(seerr, HttpMethod.Get, $"request?take={PageSize}&skip={skip}&filter=all&sort=added");
            using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var page = await response.Content.ReadFromJsonAsync<SeerrRequestPage>(Json, ct).ConfigureAwait(false);
            all.AddRange(page?.Results ?? []);
            if (page is null || page.Results.Count < PageSize)
            {
                break;
            }
        }

        return all;
    }

    private sealed record SeerrRequestPage(IReadOnlyList<SeerrRequest> Results);

    /// <summary>A title's record in Seerr with its requests; null when Seerr has none (nobody asked, nothing synced).</summary>
    /// <param name="mediaType">"movie" or "tv".</param>
    public async Task<SeerrMediaRecord?> MediaAsync(SeerrInstance seerr, string mediaType, int tmdbId, CancellationToken ct)
    {
        if (await GetAsync(seerr, $"{mediaType}/{tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct) is not { } title
            || title.ValueKind != JsonValueKind.Object || !title.TryGetProperty("mediaInfo", out var info) || info.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return info.Deserialize<SeerrMediaRecord>(Json);
    }

    /// <summary>Deletes a request (Seerr itself; Radarr and Sonarr aren't told). Gone already counts as done.</summary>
    public Task DeleteRequestAsync(SeerrInstance seerr, int requestId, CancellationToken ct) =>
        DeleteAsync(seerr, $"request/{requestId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);

    /// <summary>
    /// Forgets Seerr's record of a title (its status and requests), so it reads as not requested; Radarr, Sonarr and the
    /// files aren't touched, and Seerr's next sync with them brings back whatever they still have.
    /// </summary>
    public Task DeleteMediaAsync(SeerrInstance seerr, int mediaId, CancellationToken ct) =>
        DeleteAsync(seerr, $"media/{mediaId.ToString(System.Globalization.CultureInfo.InvariantCulture)}", ct);

    private async Task DeleteAsync(SeerrInstance seerr, string path, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Delete, path);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound)
        {
            response.EnsureSuccessStatusCode();
        }
    }

    /// <summary>Any read-only Seerr API path (e.g. "movie/603"), as JSON; null when Seerr has nothing there.</summary>
    public async Task<JsonElement?> GetAsync(SeerrInstance seerr, string path, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json, ct).ConfigureAwait(false);
    }

    /// <param name="service">sonarr or radarr.</param>
    public async Task<IReadOnlyList<SeerrService>> ServicesAsync(SeerrInstance seerr, string service, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Get, $"service/{service}");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<List<SeerrService>>(Json, ct).ConfigureAwait(false) ?? [];
    }

    /// <summary>The root folders of one of Seerr's Sonarr or Radarr servers, as that server sees them.</summary>
    public async Task<IReadOnlyList<SeerrRootFolder>> RootFoldersAsync(SeerrInstance seerr, string service, int serverId, CancellationToken ct)
    {
        using var request = Request(seerr, HttpMethod.Get, $"service/{service}/{serverId.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<SeerrServiceDetail>(Json, ct).ConfigureAwait(false))?.RootFolders ?? [];
    }

    private sealed record SeerrServiceDetail(IReadOnlyList<SeerrRootFolder>? RootFolders);

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
