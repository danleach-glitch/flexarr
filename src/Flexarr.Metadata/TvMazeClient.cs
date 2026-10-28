using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Metadata;

public sealed record TvMazeImage(string? Medium, string? Original);

public sealed record TvMazePerson(int Id, string Name, TvMazeImage? Image);

public sealed record TvMazeCharacter(int Id, string Name, TvMazeImage? Image);

public sealed record TvMazeCastMember(TvMazePerson Person, TvMazeCharacter Character);

public sealed record TvMazeCrewMember(string Type, TvMazePerson Person);

public sealed record TvMazeRating(double? Average);

public sealed record TvMazeNetwork(string? Name);

public sealed record TvMazeExternals(int? Thetvdb, string? Imdb);

public sealed record TvMazeShow(
    int Id,
    string Name,
    TvMazeImage? Image,
    string? Summary,
    string? Premiered = null,
    IReadOnlyList<string>? Genres = null,
    TvMazeRating? Rating = null,
    TvMazeNetwork? Network = null,
    TvMazeNetwork? WebChannel = null,
    int? Runtime = null,
    int? AverageRuntime = null,
    string? Status = null,
    TvMazeExternals? Externals = null)
{
    public int Year => Premiered is { Length: >= 4 } p && int.TryParse(p.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture, out var y) ? y : 0;
}

public sealed record TvMazeSearchResult(double Score, TvMazeShow Show);

public sealed record TvMazeEpisode(int Id, string? Name, int? Season, int? Number, string? Airdate, int? Runtime, string? Summary, TvMazeImage? Image);

public sealed record TvMazeSeason(int Id, int Number, TvMazeImage? Image);

public sealed record TvMazeResolution(string Url);

public sealed record TvMazeResolutions(TvMazeResolution? Original);

public sealed record TvMazeShowImage(string Type, bool Main, TvMazeResolutions Resolutions);

/// <summary>
/// Keyless TV metadata from api.tvmaze.com, politely rate limited: cast and crew for every show, and everything else
/// for shows Sonarr doesn't know (or when there's no Sonarr).
/// </summary>
public sealed class TvMazeClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Uri Base = new("https://api.tvmaze.com/");
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _next = DateTime.MinValue;

    public Task<IReadOnlyList<TvMazeCastMember>?> GetCastAsync(int showId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<TvMazeCastMember>>($"shows/{showId}/cast", ct);

    public Task<IReadOnlyList<TvMazeCrewMember>?> GetCrewAsync(int showId, CancellationToken ct) =>
        GetAsync<IReadOnlyList<TvMazeCrewMember>>($"shows/{showId}/crew", ct);

    public Task<TvMazeShow?> LookupByTvdbAsync(int tvdbId, CancellationToken ct) =>
        GetAsync<TvMazeShow>($"lookup/shows?thetvdb={tvdbId}", ct);

    public Task<TvMazeShow?> LookupByImdbAsync(string imdbId, CancellationToken ct) =>
        GetAsync<TvMazeShow>($"lookup/shows?imdb={Uri.EscapeDataString(imdbId)}", ct);

    public Task<TvMazeShow?> GetShowAsync(int showId, CancellationToken ct) =>
        GetAsync<TvMazeShow>($"shows/{showId}", ct);

    public async Task<IReadOnlyList<TvMazeSearchResult>> SearchAsync(string query, CancellationToken ct) =>
        await GetAsync<IReadOnlyList<TvMazeSearchResult>>($"search/shows?q={Uri.EscapeDataString(query)}", ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TvMazeEpisode>> GetEpisodesAsync(int showId, CancellationToken ct) =>
        await GetAsync<IReadOnlyList<TvMazeEpisode>>($"shows/{showId}/episodes?specials=1", ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TvMazeSeason>> GetSeasonsAsync(int showId, CancellationToken ct) =>
        await GetAsync<IReadOnlyList<TvMazeSeason>>($"shows/{showId}/seasons", ct).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<TvMazeShowImage>> GetImagesAsync(int showId, CancellationToken ct) =>
        await GetAsync<IReadOnlyList<TvMazeShowImage>>($"shows/{showId}/images", ct).ConfigureAwait(false) ?? [];

    private async Task<T?> GetAsync<T>(string path, CancellationToken ct) where T : class
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // TVmaze allows ~20 calls per 10 seconds.
                var wait = _next - DateTime.UtcNow;
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, ct).ConfigureAwait(false);
                }

                _next = DateTime.UtcNow.AddMilliseconds(550);
            }
            finally
            {
                Gate.Release();
            }

            using var response = await http.GetAsync(new Uri(Base, path), ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadFromJsonAsync<T>(Json, ct).ConfigureAwait(false);
        }

        return null;
    }
}
