using Flexarr.Metadata;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Deezer, remembered: every page of music discovery asks it several things, and Deezer allows about 50 requests per 5
/// seconds per server, so answers are kept a while. Charts change daily, an artist's albums rarely.
/// </summary>
public sealed class MusicCatalog(IHttpClientFactory httpFactory) : IDisposable
{
    /// <summary>
    /// Its own cache, with a limit: every distinct search is an entry, and a loop of them shouldn't grow the server's
    /// memory without end. Each entry counts as one.
    /// </summary>
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 5000 });

    /// <summary>Calls to Deezer at once: a cold Discover asks about 40 things, and Deezer allows about 50 per 5 seconds.</summary>
    private readonly SemaphoreSlim gate = new(6);

    private static readonly TimeSpan Searches = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Charts = TimeSpan.FromHours(6);
    private static readonly TimeSpan Catalogue = TimeSpan.FromDays(1);

    /// <summary>MusicBrainz takes one request a second from each caller: one at a time, spaced out.</summary>
    private readonly SemaphoreSlim musicBrainzGate = new(1);

    private DateTime nextMusicBrainz = DateTime.MinValue;

    /// <summary>A barcode MusicBrainz didn't know is asked again after this: someone may have added it since.</summary>
    private static readonly TimeSpan Unknown = TimeSpan.FromHours(1);

    private DeezerClient Deezer => new(httpFactory.CreateClient(nameof(DeezerClient)));

    private MusicBrainzClient MusicBrainz => new(httpFactory.CreateClient(nameof(MusicBrainzClient)));

    public Task<IReadOnlyList<DeezerArtist>> SearchArtistsAsync(string query, int limit, CancellationToken ct) =>
        CachedAsync($"artists?{query.Trim().ToLowerInvariant()}&{limit}", Searches, () => Deezer.SearchArtistsAsync(query, limit, ct), ct);

    public Task<IReadOnlyList<DeezerAlbum>> SearchAlbumsAsync(string query, int limit, CancellationToken ct) =>
        CachedAsync($"albums?{query.Trim().ToLowerInvariant()}&{limit}", Searches, () => Deezer.SearchAlbumsAsync(query, limit, ct), ct);

    public Task<DeezerArtist> ArtistAsync(long id, CancellationToken ct) => CachedAsync($"artist/{id}", Catalogue, () => Deezer.GetArtistAsync(id, ct), ct);

    public Task<IReadOnlyList<DeezerAlbum>> ArtistAlbumsAsync(long id, CancellationToken ct) =>
        CachedAsync($"artist/{id}/albums", Catalogue, () => Deezer.GetArtistAlbumsAsync(id, 100, ct), ct);

    public Task<IReadOnlyList<DeezerArtist>> RelatedAsync(long id, CancellationToken ct) =>
        CachedAsync($"artist/{id}/related", Catalogue, () => Deezer.GetRelatedArtistsAsync(id, 20, ct), ct);

    public Task<DeezerAlbum> AlbumAsync(long id, CancellationToken ct) => CachedAsync($"album/{id}", Catalogue, () => Deezer.GetAlbumAsync(id, ct), ct);

    public Task<IReadOnlyList<DeezerAlbum>> ChartAlbumsAsync(CancellationToken ct) => CachedAsync("chart/albums", Charts, () => Deezer.GetChartAlbumsAsync(30, ct), ct);

    public Task<IReadOnlyList<DeezerArtist>> ChartArtistsAsync(CancellationToken ct) => CachedAsync("chart/artists", Charts, () => Deezer.GetChartArtistsAsync(30, ct), ct);

    public Task<IReadOnlyList<DeezerGenre>> GenresAsync(CancellationToken ct) => CachedAsync("genres", Catalogue, () => Deezer.GetGenresAsync(ct), ct);

    public Task<IReadOnlyList<DeezerArtist>> GenreArtistsAsync(long id, CancellationToken ct) => CachedAsync($"genre/{id}/artists", Charts, () => Deezer.GetGenreArtistsAsync(id, ct), ct);

    /// <summary>
    /// The first artist Deezer finds by this exact name (case and accents aside), for a library artist that has only a
    /// name: what <i>Like artists you play</i> and <i>Fans also like</i> start from. Null when Deezer has no such artist,
    /// which is remembered too, so it isn't searched for on every visit.
    /// </summary>
    public async Task<DeezerArtist?> ArtistNamedAsync(string name, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        var found = await CachedAsync($"named/{key}", Catalogue, async () =>
        {
            var artists = await Deezer.SearchArtistsAsync(name, 5, ct);
            return new Found(artists.Where(a => MusicNames.Normalise(a.Name) == key).OrderByDescending(a => a.NbFan).FirstOrDefault());
        }, ct);
        return found.Artist;
    }

    private sealed record Found(DeezerArtist? Artist);

    /// <summary>
    /// The MusicBrainz release behind a Deezer album, by its barcode (see <see cref="MusicBrainzClient"/>), or null when
    /// MusicBrainz has none. Throws when MusicBrainz doesn't answer, which isn't remembered.
    /// </summary>
    public async Task<MusicBrainzRelease?> ReleaseByBarcodeAsync(string barcode, string? title, CancellationToken ct)
    {
        // The title too: it chooses between the releases one barcode can have (a box set and its disc, reissues).
        var key = $"musicbrainz/barcode/{barcode}/{MusicNames.NormaliseTitle(title)}";
        if (cache.TryGetValue(key, out Barcoded? hit) && hit is not null)
        {
            return hit.Release;
        }

        await musicBrainzGate.WaitAsync(ct);
        try
        {
            if (cache.TryGetValue(key, out hit) && hit is not null)
            {
                return hit.Release;
            }

            var wait = nextMusicBrainz - DateTime.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct);
            }

            try
            {
                var release = await MusicBrainz.ReleaseByBarcodeAsync(barcode, title, ct);
                cache.Set(key, new Barcoded(release), new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = release is null ? Unknown : Catalogue, Size = 1 });
                return release;
            }
            finally
            {
                nextMusicBrainz = DateTime.UtcNow.AddSeconds(1.1);
            }
        }
        finally
        {
            musicBrainzGate.Release();
        }
    }

    private sealed record Barcoded(MusicBrainzRelease? Release);

    private async Task<T> CachedAsync<T>(string key, TimeSpan keep, Func<Task<T>> fetch, CancellationToken ct)
    {
        if (cache.TryGetValue(key, out T? hit) && hit is not null)
        {
            return hit;
        }

        await gate.WaitAsync(ct);
        try
        {
            // Another call may have fetched it while this one waited.
            if (cache.TryGetValue(key, out hit) && hit is not null)
            {
                return hit;
            }

            var value = await fetch();
            if (value is not null)
            {
                cache.Set(key, value, new MemoryCacheEntryOptions { AbsoluteExpirationRelativeToNow = keep, Size = 1 });
            }

            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        cache.Dispose();
        gate.Dispose();
        musicBrainzGate.Dispose();
    }
}
