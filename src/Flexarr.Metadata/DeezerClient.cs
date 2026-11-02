using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Metadata;

/// <summary>An artist as Deezer knows them.</summary>
/// <param name="NbAlbum">How many releases Deezer lists for them (albums, EPs and singles).</param>
/// <param name="NbFan">Deezer's followers: a measure of how well known they are.</param>
public sealed record DeezerArtist(long Id, string Name, string? PictureXl = null, string? PictureBig = null, int NbAlbum = 0, int NbFan = 0)
{
    /// <summary>The largest photo there is. Deezer's placeholder for an artist without one has an empty image hash.</summary>
    public string? Picture => RealImage(PictureXl) ?? RealImage(PictureBig);

    internal static string? RealImage(string? url) => string.IsNullOrEmpty(url) || url.Contains("/images/artist//", StringComparison.Ordinal)
        || url.Contains("/images/cover//", StringComparison.Ordinal) ? null : url;
}

/// <summary>A track on a Deezer album.</summary>
/// <param name="Duration">Seconds.</param>
public sealed record DeezerTrack(long Id, string Title, int Duration = 0, int TrackPosition = 0, int DiskNumber = 0, bool ExplicitLyrics = false);

public sealed record DeezerGenre(long Id, string Name, string? PictureXl = null)
{
    public string? Picture => DeezerArtist.RealImage(PictureXl);
}

/// <summary>A release (album, EP or single) as Deezer knows it.</summary>
/// <param name="RecordType">"album", "ep", "single" or "compile".</param>
/// <param name="ReleaseDate">yyyy-MM-dd.</param>
/// <param name="Upc">Its barcode: only on the album itself (<c>album/{id}</c>), not in lists. What ties it to MusicBrainz.</param>
public sealed record DeezerAlbum(
    long Id,
    string Title,
    string? CoverXl = null,
    string? CoverBig = null,
    string? ReleaseDate = null,
    string? RecordType = null,
    DeezerArtist? Artist = null,
    int NbTracks = 0,
    int Duration = 0,
    int GenreId = 0,
    bool ExplicitLyrics = false,
    DeezerList<DeezerTrack>? Tracks = null,
    DeezerList<DeezerGenre>? Genres = null,
    string? Upc = null)
{
    public string? Cover => DeezerArtist.RealImage(CoverXl) ?? DeezerArtist.RealImage(CoverBig);

    public int? Year => ReleaseDate is { Length: >= 4 } d && int.TryParse(d.AsSpan(0, 4), System.Globalization.CultureInfo.InvariantCulture, out var y) && y > 0 ? y : null;
}

/// <summary>Deezer's lists all come as <c>{"data": [...]}</c>.</summary>
public sealed record DeezerList<T>(IReadOnlyList<T> Data);

/// <summary>Deezer answers 200 with an error object when it won't answer (an unknown id, or too many requests).</summary>
public sealed class DeezerException(string message, int code) : Exception(message)
{
    /// <summary>Deezer's code: 4 is its rate limit, 800 an id it doesn't have.</summary>
    public int Code { get; } = code;
}

/// <summary>
/// Deezer's public API, which needs no key: search, artists and the artists like them, albums with their tracks,
/// charts and genres. It backs music discovery; requests go through Lidarr, by MusicBrainz id, once matched. Nothing here
/// caches, so callers do (Deezer allows about 50 requests per 5 seconds).
/// </summary>
public sealed class DeezerClient(HttpClient http)
{
    public static readonly Uri BaseUrl = new("https://api.deezer.com/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public async Task<IReadOnlyList<DeezerArtist>> SearchArtistsAsync(string query, int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerArtist>>($"search/artist?q={Uri.EscapeDataString(query)}&limit={limit}", ct).ConfigureAwait(false)).Data;

    public async Task<IReadOnlyList<DeezerAlbum>> SearchAlbumsAsync(string query, int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerAlbum>>($"search/album?q={Uri.EscapeDataString(query)}&limit={limit}", ct).ConfigureAwait(false)).Data;

    public Task<DeezerArtist> GetArtistAsync(long id, CancellationToken ct = default) => GetAsync<DeezerArtist>($"artist/{id}", ct);

    /// <summary>Every release Deezer lists for the artist, newest first (up to <paramref name="limit"/>).</summary>
    public async Task<IReadOnlyList<DeezerAlbum>> GetArtistAlbumsAsync(long id, int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerAlbum>>($"artist/{id}/albums?limit={limit}", ct).ConfigureAwait(false)).Data;

    /// <summary>Artists Deezer's listeners also play, best match first.</summary>
    public async Task<IReadOnlyList<DeezerArtist>> GetRelatedArtistsAsync(long id, int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerArtist>>($"artist/{id}/related?limit={limit}", ct).ConfigureAwait(false)).Data;

    /// <summary>An album with its tracks and genres.</summary>
    public Task<DeezerAlbum> GetAlbumAsync(long id, CancellationToken ct = default) => GetAsync<DeezerAlbum>($"album/{id}", ct);

    /// <summary>The albums charting now (genre 0 is all of them), in the country Deezer places the server in.</summary>
    public async Task<IReadOnlyList<DeezerAlbum>> GetChartAlbumsAsync(int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerAlbum>>($"chart/0/albums?limit={limit}", ct).ConfigureAwait(false)).Data;

    public async Task<IReadOnlyList<DeezerArtist>> GetChartArtistsAsync(int limit, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerArtist>>($"chart/0/artists?limit={limit}", ct).ConfigureAwait(false)).Data;

    /// <summary>Deezer's music genres, without its catch-all "All" (id 0).</summary>
    public async Task<IReadOnlyList<DeezerGenre>> GetGenresAsync(CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerGenre>>("genre", ct).ConfigureAwait(false)).Data.Where(g => g.Id != 0).ToList();

    /// <summary>A genre's best-known artists.</summary>
    public async Task<IReadOnlyList<DeezerArtist>> GetGenreArtistsAsync(long genreId, CancellationToken ct = default) =>
        (await GetAsync<DeezerList<DeezerArtist>>($"genre/{genreId}/artists", ct).ConfigureAwait(false)).Data;

    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var response = await http.GetAsync(new Uri(BaseUrl, path), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false), cancellationToken: ct).ConfigureAwait(false);
        if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
        {
            var code = error.TryGetProperty("code", out var c) && c.TryGetInt32(out var n) ? n : 0;
            var message = error.TryGetProperty("message", out var m) ? m.GetString() : null;
            throw new DeezerException($"Deezer didn't answer: {message ?? "unknown error"}.", code);
        }

        return doc.RootElement.Deserialize<T>(Json) ?? throw new DeezerException($"Deezer sent nothing for {path}.", 0);
    }
}
