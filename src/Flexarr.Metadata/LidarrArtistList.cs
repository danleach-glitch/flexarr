using System.Collections.Concurrent;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Metadata;

/// <summary>
/// Lidarr's whole artist list, kept a few minutes for the refresh after a scan of one folder: the folder watcher makes
/// those routine, and the list is a few KB an artist.
/// </summary>
public sealed class LidarrArtistList(TimeProvider clock)
{
    private static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, (IReadOnlyList<LidarrArtist> Artists, DateTimeOffset At)> _kept = new(StringComparer.Ordinal);

    public async Task<IReadOnlyList<LidarrArtist>> GetAsync(ArrClient arr, ArrInstance lidarr, bool fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arr);
        ArgumentNullException.ThrowIfNull(lidarr);
        var key = $"{lidarr.BaseUrl}|{lidarr.ApiKey}";
        if (!fresh && _kept.TryGetValue(key, out var kept) && clock.GetUtcNow() - kept.At < KeptFor)
        {
            return kept.Artists;
        }

        var artists = await arr.GetArtistsAsync(lidarr, ct).ConfigureAwait(false);
        _kept[key] = (artists, clock.GetUtcNow());
        return artists;
    }
}
