using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Every album Lidarr is waiting for, household-wide: its Wanted › Missing (monitored, out, no files), what its calendar
/// has coming in the next year, and albums asked for in Flexarr it doesn't list yet. Only artists in folders the
/// profile can see (and, given a library, in that library's folders); never an album the library has.
/// </summary>
public static class MusicRequested
{
    private static readonly TimeSpan LidarrTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Lidarr's lists are kept this long (a page and its revisits), dropped by a request or an unrequest.</summary>
    private static readonly TimeSpan ListsFor = TimeSpan.FromSeconds(30);

    private static CancellationTokenSource _changed = new();

    /// <summary>A request or unrequest changed what Lidarr is waiting for.</summary>
    public static void Forget(IMemoryCache cache) => Interlocked.Exchange(ref _changed, new CancellationTokenSource()).Cancel();

    /// <param name="libraryFolders">Only artists in these folders (one library's Requested view); null for all.</param>
    /// <returns>Null when no Lidarr answered.</returns>
    public static async Task<List<MusicRequestedDto>?> ListAsync(MusicRequestContext c, IReadOnlyList<string>? libraryFolders, CancellationToken ct)
    {
        var owned = await c.OwnedAsync(ct);
        var listed = new List<MusicRequestedDto>();
        // Every album Lidarr knows about, shown or not, so the open-request fallback below never lists it; and the
        // ones shown, so two Lidarrs' copies show once.
        var known = new HashSet<string>(StringComparer.Ordinal);
        var shown = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        var answered = 0;
        foreach (var lidarr in c.Lidarrs)
        {
            var lists = await ListsAsync(c, lidarr, now, ct);
            if (lists is null)
            {
                continue; // a Lidarr that doesn't answer leaves its albums out, not the page
            }

            answered++;

            var (wanted, upcoming, queue) = lists.Value;
            var artists = (await MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct)).ToDictionary(a => a.Id);
            var queued = queue.Where(q => q.AlbumId is not null).ToLookup(q => q.AlbumId!.Value);
            foreach (var album in wanted.Concat(upcoming).DistinctBy(a => a.Id))
            {
                var artist = artists.GetValueOrDefault(album.ArtistId) ?? album.Artist;
                if (artist is null || album.ForeignAlbumId is not { Length: > 0 } releaseGroup)
                {
                    continue;
                }

                known.Add(releaseGroup);

                var path = MusicMatcher.LocalPath(artist, c.Options.PathMappings);
                if (!album.Monitored || album.Statistics is { TrackFileCount: > 0 } && album.Statistics.TrackFileCount >= album.Statistics.TrackCount
                    || !owned.Sees(path) || (libraryFolders is not null && (path is null || !libraryFolders.Any(f => PathComparison.IsUnder(path, f))))
                    || owned.AlbumId(artist.ArtistName, album.Title) is not null || !shown.Add(releaseGroup))
                {
                    continue;
                }

                var (state, progress) = State(album, queued[album.Id].FirstOrDefault(), now);
                listed.Add(new MusicRequestedDto(releaseGroup, album.Title, artist.ArtistName, artist.ForeignArtistId, Cover(lidarr, album) ?? Photo(lidarr, artist),
                    album.ReleaseDate is { } d ? DateOnly.FromDateTime(d) : null, state, progress, owned.ArtistId(artist.ArtistName)));
            }
        }

        // Asked for in Flexarr but not (yet) on Lidarr's lists: still searching, as far as anyone here knows. Only for
        // the whole view (a library's can't tell where these will land), and only artists Lidarr doesn't hide from
        // the profile.
        if (c.Lidarrs.Count > 0 && answered == 0)
        {
            return null;
        }

        // A profile that doesn't see every music library gets requests only for artists Lidarr can place (and shows
        // nothing it can't check while Lidarr is away).
        var partial = answered < c.Lidarrs.Count;
        if (libraryFolders is null && !(owned.VisibleFolders is not null && partial))
        {
            var hiddenArtists = new HashSet<string>(StringComparer.Ordinal);
            foreach (var lidarr in c.Lidarrs)
            {
                foreach (var artist in await MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct))
                {
                    if (artist.ForeignArtistId is { } id && !owned.Sees(MusicMatcher.LocalPath(artist, c.Options.PathMappings)))
                    {
                        hiddenArtists.Add(id);
                    }
                }
            }

            var since = now - MusicMatcher.RequestedFor;
            var asked = await c.Db.MediaRequests.AsNoTracking().Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.RequestedAt > since && r.MusicBrainzId != null)
                .OrderByDescending(r => r.RequestedAt).ToListAsync(ct);
            foreach (var request in asked)
            {
                var placeable = request.ArtistMusicBrainzId is { } artistId ? !hiddenArtists.Contains(artistId) : owned.VisibleFolders is null;
                if (placeable && !known.Contains(request.MusicBrainzId!) && shown.Add(request.MusicBrainzId!) && owned.AlbumId(request.ArtistName, request.Title) is null)
                {
                    listed.Add(new MusicRequestedDto(request.MusicBrainzId!, request.Title ?? "", request.ArtistName ?? "", request.ArtistMusicBrainzId, null, null, "searching", null,
                        owned.ArtistId(request.ArtistName)));
                }
            }
        }

        // What's moving first, then what's being looked for (newest first), then what's coming (soonest first).
        return listed.OrderBy(r => r.State switch { "importing" => 0, "downloading" => 1, "searching" => 2, _ => 3 })
            .ThenBy(r => r.State == "upcoming" ? r.ReleaseDate?.DayNumber ?? int.MaxValue : -(r.ReleaseDate?.DayNumber ?? 0))
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// A Lidarr's wanted albums, its next year's calendar and its queue, asked side by side (each with its own time
    /// limit) and kept half a minute when all three came; null when the wanted list didn't. A slow list here doesn't
    /// mark Lidarr as away for every other page.
    /// </summary>
    private static async Task<(IReadOnlyList<LidarrAlbum>, IReadOnlyList<LidarrAlbum>, IReadOnlyList<ArrQueueItem>)?> ListsAsync(MusicRequestContext c, ArrInstance lidarr,
        DateTime now, CancellationToken ct)
    {
        var key = $"lidarr-requested:{lidarr.BaseUrl}";
        if (c.Cache.TryGetValue(key, out (IReadOnlyList<LidarrAlbum>, IReadOnlyList<LidarrAlbum>, IReadOnlyList<ArrQueueItem>) hit))
        {
            return hit;
        }

        async Task<T?> Within<T>(Func<CancellationToken, Task<T>> call) where T : class
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(LidarrTimeout);
            try
            {
                return await call(quick.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return null;
            }
        }

        var wanted = Within(t => c.Client.GetWantedAlbumsAsync(lidarr, t));
        var upcoming = Within(t => c.Client.GetUpcomingAlbumsAsync(lidarr, now.Date, now.Date.AddYears(1), t));
        var queue = Within(t => c.Client.GetQueueAsync(lidarr, t));
        var artists = MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct); // alongside, cached for what follows
        if (await wanted is not { } w)
        {
            await artists;
            return null;
        }

        var (u, q) = (await upcoming, await queue);
        var lists = (w, u ?? [], q ?? []);
        await artists;
        if (u is null || q is null)
        {
            return lists; // partly answered: shown, but asked afresh next time
        }

        using var entry = c.Cache.CreateEntry(key);
        entry.Value = lists;
        entry.AbsoluteExpirationRelativeToNow = ListsFor;
        entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(Volatile.Read(ref _changed).Token));
        return lists;
    }

    private static (string State, double? Progress) State(LidarrAlbum album, ArrQueueItem? queued, DateTime now)
    {
        if (queued is not null)
        {
            // Finished downloading, whether it's importing or stuck there (blocked, failed): the download's done.
            return queued.TrackedDownloadState is "importPending" or "importing" or "imported" or "importBlocked" or "importFailed" or "failedPending" or "failed"
                || queued.Status is "completed" or "failed" or "warning"
                ? ("importing", null)
                : ("downloading", Math.Clamp(queued.Progress, 0, 1));
        }

        return album.ReleaseDate is { } released && released > now ? ("upcoming", null) : ("searching", null);
    }

    /// <summary>
    /// The album's cover from Lidarr itself, as a signed link (Lidarr's address and key stay on the server), with its
    /// ?lastWrite stamp kept so a replaced cover is a new link.
    /// </summary>
    internal static string? Cover(ArrInstance lidarr, LidarrAlbum album) => Image(lidarr, album.Images, "cover", $"mediacover/album/{album.Id}/");

    /// <summary>The artist's photo from Lidarr, for an album with no cover of its own.</summary>
    internal static string? Photo(ArrInstance lidarr, LidarrArtist artist) => artist.Id > 0 ? Image(lidarr, artist.Images, "poster", $"mediacover/artist/{artist.Id}/") : null;

    private static string? Image(ArrInstance lidarr, IReadOnlyList<ArrImage> images, string coverType, string folder)
    {
        var image = images.FirstOrDefault(i => string.Equals(i.CoverType, coverType, StringComparison.OrdinalIgnoreCase));
        if (image?.Url is not { Length: > 0 } url)
        {
            return null;
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? url : url[..query];
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.Length == 0 ? null : ImageEndpoints.ArrUrl(ArtworkUrl.Arr(lidarr.Name, folder + file + (query < 0 ? string.Empty : url[query..])));
    }
}
