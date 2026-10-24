using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Integrations.Arr;

public sealed record ArrSystemStatus(string AppName, string Version, string? InstanceName);

/// <summary>Client for the Sonarr/Radarr/Lidarr REST APIs.</summary>
public sealed class ArrClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public Task<ArrSystemStatus> GetSystemStatusAsync(ArrInstance instance, CancellationToken cancellationToken = default) =>
        GetAsync<ArrSystemStatus>(instance, "system/status", cancellationToken);

    // Radarr
    public Task<IReadOnlyList<RadarrMovie>> GetMoviesAsync(ArrInstance radarr, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RadarrMovie>>(radarr, "movie", ct);

    /// <summary>Radarr's film series (TMDB collections), each with all its films.</summary>
    public Task<IReadOnlyList<RadarrCollection>> GetCollectionsAsync(ArrInstance radarr, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RadarrCollection>>(radarr, "collection", ct);

    public Task<RadarrMovie> GetMovieAsync(ArrInstance radarr, int movieId, CancellationToken ct = default) =>
        GetAsync<RadarrMovie>(radarr, $"movie/{movieId}", ct);

    public Task<IReadOnlyList<RadarrCredit>> GetCreditsAsync(ArrInstance radarr, int movieId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RadarrCredit>>(radarr, $"credit?movieId={movieId}", ct);

    /// <summary>Radarr proxies TMDB lookups: no TMDB key needed. Accepts "title year", "tmdb:123" or "imdb:tt123".</summary>
    public Task<IReadOnlyList<RadarrMovie>> LookupMoviesAsync(ArrInstance radarr, string term, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<RadarrMovie>>(radarr, $"movie/lookup?term={Uri.EscapeDataString(term)}", ct);

    // Sonarr
    public Task<IReadOnlyList<SonarrSeries>> GetSeriesAsync(ArrInstance sonarr, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SonarrSeries>>(sonarr, "series", ct);

    public Task<IReadOnlyList<SonarrEpisode>> GetEpisodesAsync(ArrInstance sonarr, int seriesId, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SonarrEpisode>>(sonarr, $"episode?seriesId={seriesId}&includeImages=true", ct);

    public Task<SonarrEpisode> GetEpisodeAsync(ArrInstance sonarr, int episodeId, CancellationToken ct = default) =>
        GetAsync<SonarrEpisode>(sonarr, $"episode/{episodeId}", ct);

    public Task<ArrFile> GetEpisodeFileAsync(ArrInstance sonarr, int episodeFileId, CancellationToken ct = default) =>
        GetAsync<ArrFile>(sonarr, $"episodefile/{episodeFileId}", ct);

    /// <summary>Monitored episodes are downloaded when missing, so a deleted episode is unmonitored first.</summary>
    public async Task SetEpisodesMonitoredAsync(ArrInstance sonarr, IReadOnlyList<int> episodeIds, bool monitored, CancellationToken ct = default)
    {
        using var request = Request(sonarr, HttpMethod.Put, "episode/monitor");
        request.Content = JsonContent.Create(new { episodeIds, monitored }, options: Json);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Sonarr proxies TVDB lookups. Accepts a title or "tvdb:123".</summary>
    public Task<IReadOnlyList<SonarrSeries>> LookupSeriesAsync(ArrInstance sonarr, string term, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<SonarrSeries>>(sonarr, $"series/lookup?term={Uri.EscapeDataString(term)}", ct);

    /// <summary>
    /// Adds a show Sonarr will manage from its existing folder: unmonitored and with no search, so nothing is
    /// downloaded. Sonarr fetches the details from TVDB and scans the folder for episodes.
    /// </summary>
    public Task<SonarrSeries> AddSeriesAsync(ArrInstance sonarr, int tvdbId, string title, string path, int qualityProfileId, CancellationToken ct = default) =>
        PostAsync<SonarrSeries>(sonarr, "series", new
        {
            tvdbId,
            title,
            path,
            qualityProfileId,
            languageProfileId = 1, // Sonarr v3 requires one; v4 ignores it.
            monitored = false,
            seasonFolder = true,
            seriesType = "standard",
            addOptions = new { monitor = "none", searchForMissingEpisodes = false, searchForCutoffUnmetEpisodes = false },
        }, ct);

    /// <summary>Adds a film Radarr will manage from its existing folder: unmonitored and with no search.</summary>
    public Task<RadarrMovie> AddMovieAsync(ArrInstance radarr, int tmdbId, string title, string path, int qualityProfileId, CancellationToken ct = default) =>
        PostAsync<RadarrMovie>(radarr, "movie", new
        {
            tmdbId,
            title,
            path,
            qualityProfileId,
            monitored = false,
            minimumAvailability = "released",
            addOptions = new { monitor = "none", searchForMovie = false },
        }, ct);

    // Both
    public Task<IReadOnlyList<ArrQualityProfile>> GetQualityProfilesAsync(ArrInstance instance, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ArrQualityProfile>>(instance, "qualityprofile", ct);

    public Task<IReadOnlyList<ArrRootFolder>> GetRootFoldersAsync(ArrInstance instance, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<ArrRootFolder>>(instance, "rootfolder", ct);

    public Task<IReadOnlyList<T>> GetCalendarAsync<T>(ArrInstance instance, DateTime start, DateTime end, CancellationToken ct = default) =>
        GetAsync<IReadOnlyList<T>>(instance,
            $"calendar?start={start:yyyy-MM-dd}&end={end:yyyy-MM-dd}&includeSeries=true&includeEpisodeImages=true&unmonitored=false", ct);

    public async Task<IReadOnlyList<ArrQueueItem>> GetQueueAsync(ArrInstance instance, CancellationToken ct = default)
    {
        var include = instance.Kind == ArrKind.Radarr ? "includeMovie=true" : "includeSeries=true&includeEpisode=true";
        var page = await GetAsync<ArrPage<ArrQueueItem>>(instance, $"queue?pageSize=200&{include}", ct).ConfigureAwait(false);
        return page.Records;
    }

    /// <summary>
    /// Deletes a film (Radarr), a show (Sonarr) or one episode's file (Sonarr) <b>and its files on disk</b>. Films and
    /// shows are removed from the app entirely; import lists may add them back unless excluded in the app.
    /// </summary>
    public async Task DeleteAsync(ArrInstance instance, string path, CancellationToken ct = default)
    {
        using var request = Request(instance, HttpMethod.Delete, path);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Runs an *arr command, e.g. MoviesSearch or EpisodeSearch.</summary>
    public async Task SendCommandAsync(ArrInstance instance, object command, CancellationToken ct = default)
    {
        using var request = Request(instance, HttpMethod.Post, "command");
        request.Content = JsonContent.Create(command, options: Json);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private async Task<T> GetAsync<T>(ArrInstance instance, string path, CancellationToken ct)
    {
        using var request = Request(instance, HttpMethod.Get, path);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{instance.Name} returned an empty response for {path}.");
    }

    /// <summary>A rejected request throws with the app's own reason (e.g. "Path is already configured for an existing series").</summary>
    private async Task<T> PostAsync<T>(ArrInstance instance, string path, object body, CancellationToken ct)
    {
        using var request = Request(instance, HttpMethod.Post, path);
        request.Content = JsonContent.Create(body, options: Json);
        using var response = await httpClient.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await RejectionAsync(response, ct).ConfigureAwait(false);
            throw new HttpRequestException(reason ?? $"{instance.Name} answered {(int)response.StatusCode}.", null, response.StatusCode);
        }

        return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"{instance.Name} returned an empty response for {path}.");
    }

    /// <summary>Sonarr and Radarr explain a 400 as a list of validation failures, each with an errorMessage.</summary>
    private static async Task<string?> RejectionAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var failures = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
            var messages = failures
                .Select(f => f.ValueKind == JsonValueKind.Object && (f.TryGetProperty("errorMessage", out var m) || f.TryGetProperty("message", out m)) ? m.GetString() : null)
                .OfType<string>().Distinct().ToList();
            return messages.Count > 0 ? string.Join(" ", messages) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static HttpRequestMessage Request(ArrInstance instance, HttpMethod method, string path)
    {
        // Keep any URL base (http://host/radarr) — Uri's relative resolution would drop its last segment.
        var request = new HttpRequestMessage(method, new Uri($"{instance.BaseUrl.ToString().TrimEnd('/')}/api/{instance.ApiVersion}/{path}"));
        request.Headers.Add("X-Api-Key", instance.ApiKey);
        return request;
    }
}
