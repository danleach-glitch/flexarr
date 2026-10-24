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

    // Both
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

    private static HttpRequestMessage Request(ArrInstance instance, HttpMethod method, string path)
    {
        // Keep any URL base (http://host/radarr) — Uri's relative resolution would drop its last segment.
        var request = new HttpRequestMessage(method, new Uri($"{instance.BaseUrl.ToString().TrimEnd('/')}/api/{instance.ApiVersion}/{path}"));
        request.Headers.Add("X-Api-Key", instance.ApiKey);
        return request;
    }
}
