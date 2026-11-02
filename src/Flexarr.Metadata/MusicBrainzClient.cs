using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Flexarr.Metadata;

/// <summary>An artist on a MusicBrainz release, by their MusicBrainz id: what Lidarr adds artists by.</summary>
public sealed record MusicBrainzArtist(string Id, string Name);

/// <summary>A release found by its barcode: the release group (Lidarr's "album") and who it's credited to.</summary>
public sealed record MusicBrainzRelease(string ReleaseGroupId, string Title, IReadOnlyList<MusicBrainzArtist> Artists);

/// <summary>
/// MusicBrainz, the catalogue Lidarr downloads from. Deezer and MusicBrainz share no ids, but both know an album's
/// barcode, which ties a Deezer album to exactly one release group and its artist: names alone can't tell two artists
/// called TeddyBear apart. MusicBrainz asks for one request a second and a User-Agent saying who's asking; callers
/// pace and cache (see <c>MusicCatalog</c>).
/// </summary>
public sealed class MusicBrainzClient(HttpClient http)
{
    public static readonly Uri BaseUrl = new("https://musicbrainz.org/ws/2/");

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// The release with this barcode, or null when MusicBrainz has none. Deezer gives UPCs, MusicBrainz often stores the
    /// same code as a 13-digit EAN (a leading zero), so both spellings are asked for. With several releases (one per
    /// country, say), the one titled like <paramref name="title"/> is preferred.
    /// </summary>
    public async Task<MusicBrainzRelease?> ReleaseByBarcodeAsync(string barcode, string? title = null, CancellationToken ct = default)
    {
        var digits = new string(barcode.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
        if (digits.Length < 8)
        {
            return null;
        }

        var spellings = new[] { digits, "0" + digits, "00" + digits }.Where(s => s.Length is >= 8 and <= 14).Distinct();
        var query = Uri.EscapeDataString($"barcode:({string.Join(" OR ", spellings)})");
        using var response = await http.GetAsync(new Uri(BaseUrl, $"release?query={query}&fmt=json&limit=10"), ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var found = await response.Content.ReadFromJsonAsync<SearchResult>(Json, ct).ConfigureAwait(false);
        var releases = (found?.Releases ?? []).Where(r => r.ReleaseGroup?.Id is { Length: > 0 } && r.Barcode?.TrimStart('0') == digits).ToList();
        var key = title is null ? null : MusicNames.NormaliseTitle(title);
        var best = releases.FirstOrDefault(r => key is not null && MusicNames.NormaliseTitle(r.Title ?? "") == key) ?? releases.FirstOrDefault();
        return best is null
            ? null
            : new MusicBrainzRelease(best.ReleaseGroup!.Id!, best.Title ?? "",
                (best.ArtistCredit ?? []).Where(c => c.Artist?.Id is { Length: > 0 }).Select(c => new MusicBrainzArtist(c.Artist!.Id!, c.Artist.Name ?? c.Name ?? "")).ToList());
    }

    private sealed record SearchResult(List<Release>? Releases);

    private sealed record Release(
        string? Title,
        string? Barcode,
        [property: JsonPropertyName("release-group")] Group? ReleaseGroup,
        [property: JsonPropertyName("artist-credit")] List<Credit>? ArtistCredit);

    private sealed record Group(string? Id);

    private sealed record Credit(string? Name, CreditedArtist? Artist);

    private sealed record CreditedArtist(string? Id, string? Name);
}
