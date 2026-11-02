using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Every album Lidarr is waiting for, household-wide: its Wanted › Missing (monitored, out, no files), what its calendar
/// has coming in the next year, and albums asked for in Flexarr it doesn't list yet. Only artists in folders the
/// profile can see; never an album the library has.
/// </summary>
public static class MusicRequested
{
    private static readonly TimeSpan LidarrTimeout = TimeSpan.FromSeconds(15);

    public static async Task<List<MusicRequestedDto>> ListAsync(MusicRequestContext c, CancellationToken ct)
    {
        var owned = await c.OwnedAsync(ct);
        var listed = new List<MusicRequestedDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var now = DateTime.UtcNow;
        foreach (var lidarr in c.Lidarrs)
        {
            IReadOnlyList<LidarrAlbum> wanted, upcoming;
            IReadOnlyList<ArrQueueItem> queue;
            try
            {
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(LidarrTimeout);
                var w = c.Client.GetWantedAlbumsAsync(lidarr, quick.Token);
                var u = c.Client.GetUpcomingAlbumsAsync(lidarr, now.Date, now.Date.AddYears(1), quick.Token);
                var q = c.Client.GetQueueAsync(lidarr, quick.Token);
                (wanted, upcoming, queue) = (await w, await u, await q);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                continue; // a Lidarr that doesn't answer leaves its albums out, not the page
            }

            var artists = (await MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct)).ToDictionary(a => a.Id);
            var queued = queue.Where(q => q.AlbumId is not null).ToLookup(q => q.AlbumId!.Value);
            foreach (var album in wanted.Concat(upcoming).DistinctBy(a => a.Id))
            {
                var artist = artists.GetValueOrDefault(album.ArtistId) ?? album.Artist;
                if (artist is null || album.ForeignAlbumId is not { Length: > 0 } releaseGroup || !album.Monitored
                    || album.Statistics is { TrackFileCount: > 0 } && album.Statistics.TrackFileCount >= album.Statistics.TrackCount
                    || !owned.Sees(MusicMatcher.LocalPath(artist, c.Options.PathMappings))
                    || owned.AlbumId(artist.ArtistName, album.Title) is not null || !seen.Add(releaseGroup))
                {
                    continue;
                }

                var (state, progress) = State(album, queued[album.Id].FirstOrDefault(), now);
                listed.Add(new MusicRequestedDto(releaseGroup, album.Title, artist.ArtistName, artist.ForeignArtistId, Cover(lidarr, album),
                    album.ReleaseDate is { } d ? DateOnly.FromDateTime(d) : null, state, progress, owned.ArtistId(artist.ArtistName)));
            }
        }

        // Asked for in Flexarr but not (yet) on Lidarr's lists: still searching, as far as anyone here knows.
        var since = now - MusicMatcher.RequestedFor;
        var asked = await c.Db.MediaRequests.AsNoTracking().Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.RequestedAt > since && r.MusicBrainzId != null)
            .OrderByDescending(r => r.RequestedAt).ToListAsync(ct);
        foreach (var request in asked)
        {
            if (seen.Add(request.MusicBrainzId!) && owned.AlbumId(request.ArtistName, request.Title) is null)
            {
                listed.Add(new MusicRequestedDto(request.MusicBrainzId!, request.Title ?? "", request.ArtistName ?? "", request.ArtistMusicBrainzId, null, null, "searching", null,
                    owned.ArtistId(request.ArtistName)));
            }
        }

        // What's moving first, then what's being looked for (newest first), then what's coming (soonest first).
        return listed.OrderBy(r => r.State switch { "importing" => 0, "downloading" => 1, "searching" => 2, _ => 3 })
            .ThenBy(r => r.State == "upcoming" ? r.ReleaseDate?.DayNumber ?? int.MaxValue : -(r.ReleaseDate?.DayNumber ?? 0))
            .ThenBy(r => r.Artist, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static (string State, double? Progress) State(LidarrAlbum album, ArrQueueItem? queued, DateTime now)
    {
        if (queued is not null)
        {
            return queued.TrackedDownloadState is "importPending" or "importing" or "imported" ? ("importing", null) : ("downloading", Math.Clamp(queued.Progress, 0, 1));
        }

        return album.ReleaseDate is { } released && released > now ? ("upcoming", null) : ("searching", null);
    }

    /// <summary>The album's cover from Lidarr itself, as a signed link (Lidarr's address and key stay on the server).</summary>
    private static string? Cover(ArrInstance lidarr, LidarrAlbum album)
    {
        var image = album.Images.FirstOrDefault(i => string.Equals(i.CoverType, "cover", StringComparison.OrdinalIgnoreCase));
        if (image?.Url is not { Length: > 0 } url)
        {
            return null;
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? url : url[..query];
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.Length == 0 ? null : ImageEndpoints.ArrUrl(ArtworkUrl.Arr(lidarr.Name, $"mediacover/album/{album.Id}/{file}"));
    }
}
