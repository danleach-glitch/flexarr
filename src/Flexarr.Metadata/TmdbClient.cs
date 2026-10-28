using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Metadata;

public sealed record TmdbMovieResult(int Id, string? Title, string? OriginalTitle, string? ReleaseDate, string? Overview, string? PosterPath)
{
    public int Year => TmdbClient.YearOf(ReleaseDate);
}

public sealed record TmdbPage(IReadOnlyList<TmdbMovieResult> Results);

public sealed record TmdbFind(IReadOnlyList<TmdbMovieResult> MovieResults);

public sealed record TmdbGenre(int Id, string Name);

public sealed record TmdbCompany(string Name);

public sealed record TmdbCast(int Id, string Name, string? Character, string? ProfilePath, int Order);

public sealed record TmdbCrew(int Id, string Name, string? Job, string? ProfilePath);

public sealed record TmdbCredits(IReadOnlyList<TmdbCast> Cast, IReadOnlyList<TmdbCrew> Crew);

public sealed record TmdbReleaseDate(string? Certification, int Type);

public sealed record TmdbCountryReleases([property: JsonPropertyName("iso_3166_1")] string Iso31661, IReadOnlyList<TmdbReleaseDate> ReleaseDates);

public sealed record TmdbReleases(IReadOnlyList<TmdbCountryReleases> Results);

public sealed record TmdbVideo(string? Site, string? Key, string? Type, bool Official);

public sealed record TmdbVideos(IReadOnlyList<TmdbVideo> Results);

public sealed record TmdbImage(string FilePath, [property: JsonPropertyName("iso_639_1")] string? Iso6391, double VoteAverage);

public sealed record TmdbImages(IReadOnlyList<TmdbImage> Posters, IReadOnlyList<TmdbImage> Backdrops, IReadOnlyList<TmdbImage> Logos);

public sealed record TmdbMovie(
    int Id,
    string? ImdbId,
    string? Title,
    string? OriginalTitle,
    string? Overview,
    string? ReleaseDate,
    int? Runtime,
    string? Status,
    double VoteAverage,
    double Popularity,
    string? PosterPath,
    string? BackdropPath,
    IReadOnlyList<TmdbGenre>? Genres,
    IReadOnlyList<TmdbCompany>? ProductionCompanies,
    TmdbCredits? Credits,
    TmdbReleases? ReleaseDates,
    TmdbVideos? Videos,
    TmdbImages? Images)
{
    public int Year => TmdbClient.YearOf(ReleaseDate);
}

/// <summary>
/// Films from The Movie Database (api.themoviedb.org), with the key the server's owner pasted into Settings: for
/// films Radarr doesn't know (or when there's no Radarr). Either kind of key TMDB gives out works: the API key, or the
/// longer read access token.
/// </summary>
public sealed class TmdbClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };
    private static readonly Uri Base = new("https://api.themoviedb.org/3/");

    /// <summary>Where TMDB keeps the full-size copy of an image.</summary>
    public static string? ImageUrl(string? path) => string.IsNullOrEmpty(path) ? null : $"https://image.tmdb.org/t/p/original{path}";

    public static int YearOf(string? date) =>
        date is { Length: >= 4 } && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year) ? year : 0;

    public async Task<IReadOnlyList<TmdbMovieResult>> SearchMoviesAsync(string apiKey, string query, int? year, CancellationToken ct)
    {
        var path = $"search/movie?include_adult=false&query={Uri.EscapeDataString(query)}" + (year is { } y ? $"&year={y}" : "");
        return (await GetAsync<TmdbPage>(apiKey, path, ct).ConfigureAwait(false))?.Results ?? [];
    }

    public async Task<TmdbMovieResult?> FindByImdbAsync(string apiKey, string imdbId, CancellationToken ct) =>
        (await GetAsync<TmdbFind>(apiKey, $"find/{Uri.EscapeDataString(imdbId)}?external_source=imdb_id", ct).ConfigureAwait(false))?.MovieResults is [var first, ..] ? first : null;

    /// <summary>A film with its cast, age ratings, trailers and artwork (English or wordless).</summary>
    public Task<TmdbMovie?> GetMovieAsync(string apiKey, int id, CancellationToken ct) =>
        GetAsync<TmdbMovie>(apiKey, $"movie/{id}?append_to_response=credits,release_dates,videos,images&include_image_language=en,null", ct);

    /// <summary>Whether TMDB takes the key.</summary>
    /// <exception cref="HttpRequestException">TMDB couldn't be reached.</exception>
    public async Task<bool> CheckKeyAsync(string apiKey, CancellationToken ct)
    {
        using var response = await SendAsync(apiKey, "configuration", ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    private async Task<T?> GetAsync<T>(string apiKey, string path, CancellationToken ct) where T : class
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await SendAsync(apiKey, path, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                await Task.Delay(response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
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

    private async Task<HttpResponseMessage> SendAsync(string apiKey, string path, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        // The read access token is a JWT and goes in the header; the shorter API key goes in the query.
        var token = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
        var uri = new Uri(Base, token ? path : $"{path}{(path.Contains('?', StringComparison.Ordinal) ? '&' : '?')}api_key={Uri.EscapeDataString(apiKey)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (token)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        return await http.SendAsync(request, ct).ConfigureAwait(false);
    }
}
