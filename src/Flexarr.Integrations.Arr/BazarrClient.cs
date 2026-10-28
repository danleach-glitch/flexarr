using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Integrations.Arr;

/// <summary>A Bazarr server, which finds subtitles for what Sonarr and Radarr manage.</summary>
public sealed record BazarrInstance(Uri BaseUrl, string ApiKey);

/// <summary>What Bazarr knows about a film or an episode: its file, its languages profile and its subtitles.</summary>
/// <param name="SeriesId">Sonarr's id for an episode's show; null for a film.</param>
/// <param name="ProfileId">Bazarr's languages profile; it won't search without one.</param>
public sealed record BazarrMedia(int Id, int? SeriesId, string Path, int? ProfileId, IReadOnlyList<BazarrSubtitleFile> Subtitles);

/// <summary>A subtitle Bazarr has indexed for a video; <see cref="Path"/> is null for one inside the video.</summary>
public sealed record BazarrSubtitleFile(string? Code2, string? Path, bool Forced, bool Hi);

/// <summary>A subtitle a provider offers, from a manual search, best first.</summary>
/// <param name="Subtitle">Bazarr's id for it, passed back to download it.</param>
/// <param name="Score">Out of 100.</param>
/// <param name="Matches">What matched the file: "hash" means made for this exact release.</param>
public sealed record BazarrSubtitleResult(
    string Provider,
    string Subtitle,
    string Language,
    bool HearingImpaired,
    bool Forced,
    bool OriginalFormat,
    double Score,
    IReadOnlyList<string> ReleaseInfo,
    IReadOnlyList<string> Matches,
    string? Uploader);

/// <summary>Bazarr's API, for searching and downloading subtitles and re-syncing them to the audio.</summary>
public sealed class BazarrClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<bool> PingAsync(BazarrInstance bazarr, CancellationToken ct)
    {
        using var request = Request(bazarr, HttpMethod.Get, "system/status");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode;
    }

    /// <summary>A film by Radarr's id; null when Bazarr hasn't got it (yet).</summary>
    public async Task<BazarrMedia?> MovieAsync(BazarrInstance bazarr, int radarrId, CancellationToken ct)
    {
        var movies = await GetListAsync<MovieDto>(bazarr, $"movies?radarrid[]={Id(radarrId)}", ct).ConfigureAwait(false);
        return movies.FirstOrDefault(m => m.RadarrId == radarrId) is { } m
            ? new BazarrMedia(m.RadarrId, null, m.Path ?? "", m.ProfileId, Files(m.Subtitles))
            : null;
    }

    /// <summary>An episode by Sonarr's id; null when Bazarr hasn't got it (yet).</summary>
    public async Task<BazarrMedia?> EpisodeAsync(BazarrInstance bazarr, int sonarrEpisodeId, CancellationToken ct)
    {
        var episodes = await GetListAsync<EpisodeDto>(bazarr, $"episodes?episodeid[]={Id(sonarrEpisodeId)}", ct).ConfigureAwait(false);
        if (episodes.FirstOrDefault(e => e.SonarrEpisodeId == sonarrEpisodeId) is not { } e)
        {
            return null;
        }

        // An episode's languages profile is its show's.
        var shows = await GetListAsync<SeriesDto>(bazarr, $"series?seriesid[]={Id(e.SonarrSeriesId)}", ct).ConfigureAwait(false);
        return new BazarrMedia(e.SonarrEpisodeId, e.SonarrSeriesId, e.Path ?? "", shows.FirstOrDefault()?.ProfileId, Files(e.Subtitles));
    }

    /// <summary>Bazarr's languages profiles, by id, in the order they were made.</summary>
    public async Task<IReadOnlyList<int>> ProfilesAsync(BazarrInstance bazarr, CancellationToken ct)
    {
        using var request = Request(bazarr, HttpMethod.Get, "system/languages/profiles");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var profiles = await response.Content.ReadFromJsonAsync<List<ProfileDto>>(Json, ct).ConfigureAwait(false) ?? [];
        return profiles.Select(p => p.ProfileId).Order().ToList();
    }

    /// <summary>Gives a film, or an episode's whole show, a languages profile.</summary>
    public async Task SetProfileAsync(BazarrInstance bazarr, BazarrMedia media, int profileId, CancellationToken ct)
    {
        var (path, field, id) = media.SeriesId is { } series ? ("series", "seriesid", series) : ("movies", "radarrid", media.Id);
        await SendFormAsync(bazarr, HttpMethod.Post, path, [new(field, Id(id)), new("profileid", Id(profileId))], ct).ConfigureAwait(false);
    }

    /// <summary>Asks every provider for subtitles in the languages of the video's profile. Slow: often 10–60 seconds.</summary>
    public async Task<IReadOnlyList<BazarrSubtitleResult>> SearchAsync(BazarrInstance bazarr, BazarrMedia media, CancellationToken ct)
    {
        var path = media.SeriesId is null ? $"providers/movies?radarrid={Id(media.Id)}" : $"providers/episodes?episodeid={Id(media.Id)}";
        using var request = Request(bazarr, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
        var results = await response.Content.ReadFromJsonAsync<Envelope<ResultDto>>(Json, ct).ConfigureAwait(false);
        return (results?.Data ?? []).Where(r => r.Provider is { Length: > 0 } && r.Subtitle is { Length: > 0 }).Select(r => new BazarrSubtitleResult(
            r.Provider!, r.Subtitle!, r.Language ?? "", Truthy(r.HearingImpaired), Truthy(r.Forced), Truthy(r.OriginalFormat), r.Score,
            r.ReleaseInfo ?? [], r.Matches ?? [], r.Uploader)).ToList();
    }

    /// <summary>Downloads one search result; Bazarr saves it beside the video.</summary>
    public async Task DownloadAsync(BazarrInstance bazarr, BazarrMedia media, BazarrSubtitleResult result, CancellationToken ct)
    {
        List<KeyValuePair<string, string>> form = media.SeriesId is { } series
            ? [new("seriesid", Id(series)), new("episodeid", Id(media.Id))]
            : [new("radarrid", Id(media.Id))];
        form.AddRange([
            new("hi", result.HearingImpaired ? "True" : "False"),
            new("forced", result.Forced ? "True" : "False"),
            new("original_format", result.OriginalFormat ? "True" : "False"),
            new("provider", result.Provider),
            new("subtitle", result.Subtitle),
        ]);
        await SendFormAsync(bazarr, HttpMethod.Post, media.SeriesId is null ? "providers/movies" : "providers/episodes", form, ct).ConfigureAwait(false);
    }

    /// <summary>Re-times a subtitle file to the video's audio. Slow: a minute or more for a film.</summary>
    /// <param name="subtitlePath">The file as Bazarr sees it (from <see cref="BazarrMedia.Subtitles"/>).</param>
    public async Task SyncAsync(BazarrInstance bazarr, BazarrMedia media, BazarrSubtitleFile subtitle, string subtitlePath, CancellationToken ct)
    {
        await SendFormAsync(bazarr, HttpMethod.Patch, "subtitles", [
            new("action", "sync"),
            new("language", subtitle.Code2 ?? ""),
            new("path", subtitlePath),
            new("type", media.SeriesId is null ? "movie" : "episode"),
            new("id", Id(media.Id)),
            new("forced", subtitle.Forced ? "True" : "False"),
            new("hi", subtitle.Hi ? "True" : "False"),
        ], ct).ConfigureAwait(false);
    }

    private static List<BazarrSubtitleFile> Files(List<SubtitleDto>? subtitles) =>
        (subtitles ?? []).Select(s => new BazarrSubtitleFile(s.Code2, s.Path, s.Forced, s.Hi)).ToList();

    private async Task<List<T>> GetListAsync<T>(BazarrInstance bazarr, string path, CancellationToken ct)
    {
        using var request = Request(bazarr, HttpMethod.Get, path);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Envelope<T>>(Json, ct).ConfigureAwait(false))?.Data ?? [];
    }

    private async Task SendFormAsync(BazarrInstance bazarr, HttpMethod method, string path, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        using var request = Request(bazarr, method, path);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        await EnsureSuccessAsync(response, ct).ConfigureAwait(false);
    }

    /// <summary>Bazarr explains most failures in a short plain-text body ("Episode file not found. Path mapping issue?").</summary>
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = (await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Trim().Trim('"');
        var reason = body.Length is > 0 and <= 200 && !body.StartsWith('{') && !body.StartsWith('<') ? body : null;
        throw new HttpRequestException(reason ?? $"Bazarr answered {(int)response.StatusCode}.", null, response.StatusCode);
    }

    private static string Id(int id) => id.ToString(CultureInfo.InvariantCulture);

    // Bazarr sends some flags as the strings "True" and "False".
    private static bool Truthy(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.String => string.Equals(value.GetString(), "True", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    private static HttpRequestMessage Request(BazarrInstance bazarr, HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri($"{bazarr.BaseUrl.ToString().TrimEnd('/')}/api/{path}"));
        request.Headers.Add("X-API-KEY", bazarr.ApiKey);
        return request;
    }

    private sealed record Envelope<T>(List<T>? Data);

    private sealed record SubtitleDto(string? Code2, string? Path, bool Forced, bool Hi);

    private sealed record MovieDto(int RadarrId, string? Path, int? ProfileId, List<SubtitleDto>? Subtitles);

    private sealed record EpisodeDto(int SonarrEpisodeId, int SonarrSeriesId, string? Path, List<SubtitleDto>? Subtitles);

    private sealed record SeriesDto(int SonarrSeriesId, int? ProfileId);

    private sealed record ProfileDto(int ProfileId);

    private sealed record ResultDto(
        string? Provider,
        string? Subtitle,
        string? Language,
        [property: JsonPropertyName("hearing_impaired")] JsonElement HearingImpaired,
        JsonElement Forced,
        [property: JsonPropertyName("original_format")] JsonElement OriginalFormat,
        double Score,
        [property: JsonPropertyName("release_info")] List<string>? ReleaseInfo,
        List<string>? Matches,
        string? Uploader);
}
