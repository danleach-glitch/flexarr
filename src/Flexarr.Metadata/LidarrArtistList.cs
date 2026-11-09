using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Metadata;

/// <summary>
/// Lidarr's whole artist list, kept a few minutes for the refresh after a scan of one folder: the folder watcher makes
/// those routine, and the list is a few KB an artist. The one copy, shared with Discover's matching, and gone when its
/// time is up.
/// </summary>
public sealed class LidarrArtistList(IMemoryCache memory)
{
    public static readonly TimeSpan KeptFor = TimeSpan.FromMinutes(5);

    public static string KeyFor(ArrInstance lidarr)
    {
        ArgumentNullException.ThrowIfNull(lidarr);
        return $"lidarr-artists:{lidarr.BaseUrl}";
    }

    public async Task<IReadOnlyList<LidarrArtist>> GetAsync(ArrClient arr, ArrInstance lidarr, bool fresh, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arr);
        ArgumentNullException.ThrowIfNull(lidarr);
        if (!fresh && memory.TryGetValue(KeyFor(lidarr), out IReadOnlyList<LidarrArtist>? kept) && kept is not null)
        {
            return kept;
        }

        var artists = await arr.GetArtistsAsync(lidarr, ct).ConfigureAwait(false);
        memory.Set(KeyFor(lidarr), artists, KeptFor);
        return artists;
    }
}
