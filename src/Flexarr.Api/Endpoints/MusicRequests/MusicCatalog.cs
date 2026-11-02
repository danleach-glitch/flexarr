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

    private DeezerClient Deezer => new(httpFactory.CreateClient(nameof(DeezerClient)));

    public Task<IReadOnlyList<DeezerArtist>> SearchArtistsAsync(string query, int limit, CancellationToken ct) =>
        CachedAsync($"artists?{query.Trim().ToLowerInvariant()}&{limit}", Searches, () => Deezer.SearchArtistsAsync(query, limit, ct));

    public Task<IReadOnlyList<DeezerAlbum>> SearchAlbumsAsync(string query, int limit, CancellationToken ct) =>
        CachedAsync($"albums?{query.Trim().ToLowerInvariant()}&{limit}", Searches, () => Deezer.SearchAlbumsAsync(query, limit, ct));

    public Task<DeezerArtist> ArtistAsync(long id, CancellationToken ct) => CachedAsync($"artist/{id}", Catalogue, () => Deezer.GetArtistAsync(id, ct));

    public Task<IReadOnlyList<DeezerAlbum>> ArtistAlbumsAsync(long id, CancellationToken ct) =>
        CachedAsync($"artist/{id}/albums", Catalogue, () => Deezer.GetArtistAlbumsAsync(id, 100, ct));

    public Task<IReadOnlyList<DeezerArtist>> RelatedAsync(long id, CancellationToken ct) =>
        CachedAsync($"artist/{id}/related", Catalogue, () => Deezer.GetRelatedArtistsAsync(id, 20, ct));

    public Task<DeezerAlbum> AlbumAsync(long id, CancellationToken ct) => CachedAsync($"album/{id}", Catalogue, () => Deezer.GetAlbumAsync(id, ct));

    public Task<IReadOnlyList<DeezerAlbum>> ChartAlbumsAsync(CancellationToken ct) => CachedAsync("chart/albums", Charts, () => Deezer.GetChartAlbumsAsync(30, ct));

    public Task<IReadOnlyList<DeezerArtist>> ChartArtistsAsync(CancellationToken ct) => CachedAsync("chart/artists", Charts, () => Deezer.GetChartArtistsAsync(30, ct));

    public Task<IReadOnlyList<DeezerGenre>> GenresAsync(CancellationToken ct) => CachedAsync("genres", Catalogue, () => Deezer.GetGenresAsync(ct));

    public Task<IReadOnlyList<DeezerArtist>> GenreArtistsAsync(long id, CancellationToken ct) => CachedAsync($"genre/{id}/artists", Charts, () => Deezer.GetGenreArtistsAsync(id, ct));

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
        });
        return found.Artist;
    }

    private sealed record Found(DeezerArtist? Artist);

    private async Task<T> CachedAsync<T>(string key, TimeSpan keep, Func<Task<T>> fetch)
    {
        if (cache.TryGetValue(key, out T? hit) && hit is not null)
        {
            return hit;
        }

        await gate.WaitAsync();
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
    }
}
