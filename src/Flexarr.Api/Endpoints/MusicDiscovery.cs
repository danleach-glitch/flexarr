using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints;

/// <summary>A track that artist radio could play: whose it is, how close its artist is to the seed, and how much the listener plays it.</summary>
/// <param name="SharedGenres">Genres the track's artist (or their albums) has in common with the seed artist; 0 for the seed's own tracks.</param>
/// <param name="ArtistPlays">The listener's plays of anything by the artist.</param>
/// <param name="TrackPlays">The listener's plays of this track.</param>
public sealed record RadioCandidate(Guid TrackId, Guid ArtistId, int SharedGenres, int ArtistPlays, int TrackPlays);

/// <summary>
/// Music rows built from what the server already records about listening (play counts and when each track was last
/// played, per profile), and the picker behind artist radio.
/// </summary>
public static class MusicDiscovery
{
    public const int RadioLength = 50;

    /// <summary>How long an album counts as recently played, and so isn't suggested again in "More from artists you play".</summary>
    private static readonly TimeSpan RecentlyPlayed = TimeSpan.FromDays(30);

    /// <summary>The share of a radio station that is the seed artist's own music, when they have enough tracks.</summary>
    private const double OwnShare = 0.4;

    /// <summary>
    /// Your playlists, Made for you, Recently played, Most played and More from artists you play, for one music library
    /// or (null) all of them. Rows with nothing in them are left out, so a profile that hasn't played any music gets none.
    /// </summary>
    public static async Task<List<HomeRowDto>> RowsAsync(FlexarrDbContext db, IMemoryCache cache, string profileId, Guid? libraryId, int size, CancellationToken ct)
    {
        var rows = new List<HomeRowDto>();
        // Playlists and mixes span libraries, so every music library's Browse offers them.
        if (await Playlists.PlaylistEndpoints.HomeRowAsync(db, profileId, size, ct) is { } playlists)
        {
            rows.Add(playlists);
        }

        if (await MusicMixes.HomeRowAsync(db, cache, profileId, ct) is { } mixes)
        {
            rows.Add(mixes);
        }

        var played = db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind == MediaKind.Track && (libraryId == null || p.MediaItem.LibraryId == libraryId));

        // Albums, most recently played first: the album is what you'd put on again, rather than the one track.
        var recentAlbums = await played.GroupBy(p => p.MediaItem!.ParentId!.Value)
            .Select(g => new { AlbumId = g.Key, At = g.Max(p => p.LastPlayedAt) })
            .OrderByDescending(a => a.At).Take(size).ToListAsync(ct);
        rows.Add(new HomeRowDto("music-recent", "Recently played", "poster", await InOrderAsync(db, recentAlbums.Select(a => a.AlbumId), profileId, ct)));

        var topTracks = await played.Where(p => p.PlayCount > 0)
            .OrderByDescending(p => p.PlayCount).ThenByDescending(p => p.LastPlayedAt).Take(size)
            .Select(p => p.MediaItemId).ToListAsync(ct);
        rows.Add(new HomeRowDto("music-top", "Most played", "tracks", await InOrderAsync(db, topTracks, profileId, ct)));

        // The artists you play most, taking turns, each offering the albums of theirs you haven't played lately.
        var topArtists = await played.GroupBy(p => p.MediaItem!.Parent!.ParentId!.Value)
            .Select(g => new { ArtistId = g.Key, Plays = g.Sum(p => p.PlayCount) })
            .Where(a => a.Plays > 0).OrderByDescending(a => a.Plays).Take(8).Select(a => a.ArtistId).ToListAsync(ct);
        if (topArtists.Count > 0)
        {
            var since = DateTime.UtcNow - RecentlyPlayed;
            var albums = await db.MediaItems
                .Where(a => a.Kind == MediaKind.Album && topArtists.Contains(a.ParentId!.Value)
                    && !a.Children.Any(t => t.PlaybackStates.Any(p => p.UserId == profileId && p.LastPlayedAt > since)))
                .OrderByDescending(a => a.Year).ThenBy(a => a.SortTitle)
                .Select(a => new { a.Id, ArtistId = a.ParentId!.Value }).ToListAsync(ct);
            var byArtist = topArtists.Select(id => new Queue<Guid>(albums.Where(a => a.ArtistId == id).Select(a => a.Id))).ToList();
            var picks = new List<Guid>();
            while (picks.Count < size && byArtist.Any(q => q.Count > 0))
            {
                foreach (var queue in byArtist.Where(q => q.Count > 0))
                {
                    picks.Add(queue.Dequeue());
                }
            }

            rows.Add(new HomeRowDto("music-artists", "More from artists you play", "poster", await InOrderAsync(db, picks.Take(size), profileId, ct)));
        }

        return rows.Where(r => r.HasContent).ToList();
    }

    /// <summary>Everything a radio station seeded from <paramref name="artistId"/> could play, for the picker.</summary>
    public static async Task<List<RadioCandidate>> RadioCandidatesAsync(FlexarrDbContext db, Guid artistId, string profileId, CancellationToken ct)
    {
        // The seed's genres are their own and their albums': Lidarr tags some artists only at the album level.
        var genreIds = await db.MediaItems.Where(i => i.Id == artistId || i.ParentId == artistId)
            .SelectMany(i => i.Genres.Select(g => g.Id)).Distinct().ToListAsync(ct);

        // Each other artist's genres in common with the seed, counted once however many of their albums carry them.
        var related = genreIds.Count == 0
            ? []
            : (await db.MediaItems
                .Where(i => (i.Kind == MediaKind.Artist || i.Kind == MediaKind.Album) && i.Genres.Any(g => genreIds.Contains(g.Id)))
                .Select(i => new { ArtistId = i.Kind == MediaKind.Artist ? i.Id : i.ParentId!.Value, Genres = i.Genres.Where(g => genreIds.Contains(g.Id)).Select(g => g.Id).ToList() })
                .ToListAsync(ct))
                .Where(i => i.ArtistId != artistId)
                .GroupBy(i => i.ArtistId)
                .ToDictionary(g => g.Key, g => g.SelectMany(i => i.Genres).Distinct().Count());

        var plays = await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind == MediaKind.Track)
            .GroupBy(p => p.MediaItem!.Parent!.ParentId!.Value)
            .Select(g => new { ArtistId = g.Key, Plays = g.Sum(p => p.PlayCount) })
            .ToDictionaryAsync(a => a.ArtistId, a => a.Plays, ct);

        // Closest first, then most played: a genre as broad as "Rock" would otherwise bring in everyone.
        var artists = related.OrderByDescending(r => r.Value).ThenByDescending(r => plays.GetValueOrDefault(r.Key)).Take(30).Select(r => r.Key)
            .Prepend(artistId).ToList();
        var tracks = await db.MediaItems.Where(t => t.Kind == MediaKind.Track && artists.Contains(t.Parent!.ParentId!.Value))
            .Select(t => new
            {
                t.Id,
                ArtistId = t.Parent!.ParentId!.Value,
                Plays = t.PlaybackStates.Where(p => p.UserId == profileId).Select(p => p.PlayCount).FirstOrDefault(),
            })
            .ToListAsync(ct);
        return tracks.Select(t => new RadioCandidate(t.Id, t.ArtistId, related.GetValueOrDefault(t.ArtistId), plays.GetValueOrDefault(t.ArtistId), t.Plays)).ToList();
    }

    /// <summary>
    /// Picks a radio station: about two fifths the seed artist's own tracks (more if there's nobody related), the rest
    /// from related artists, favouring artists close in genre and anything the listener plays. The station opens with
    /// the seed artist and is otherwise shuffled; no track plays twice. The same seed always gives the same station.
    /// </summary>
    public static List<Guid> PickRadio(Guid artistId, IReadOnlyList<RadioCandidate> candidates, int seed, int count = RadioLength)
    {
        var random = new Random(seed);
        var own = candidates.Where(c => c.ArtistId == artistId).DistinctBy(c => c.TrackId).ToList();
        var others = candidates.Where(c => c.ArtistId != artistId).DistinctBy(c => c.TrackId).ToList();

        var ownCount = Math.Min(own.Count, Math.Max((int)Math.Round(count * OwnShare), count - others.Count));
        var picks = Draw(own, ownCount, random, c => 1 + Math.Log2(1 + c.TrackPlays));
        picks.AddRange(Draw(others, count - picks.Count, random, c => 1 + c.SharedGenres + Math.Log2(1 + c.ArtistPlays) + Math.Log2(1 + c.TrackPlays)));

        var opener = picks.FirstOrDefault(c => c.ArtistId == artistId);
        var rest = picks.Where(c => c != opener).ToList();
        for (var i = rest.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (rest[i], rest[j]) = (rest[j], rest[i]);
        }

        return (opener is null ? rest : rest.Prepend(opener)).Select(c => c.TrackId).ToList();
    }

    /// <summary>Weighted draws without replacement: heavier candidates come out more often, but each at most once.</summary>
    /// <param name="perArtist">At most this many of any one artist's tracks; the rest of theirs drop out once it's reached.</param>
    internal static List<RadioCandidate> Draw(List<RadioCandidate> pool, int count, Random random, Func<RadioCandidate, double> weight, int? perArtist = null)
    {
        var remaining = pool.Select(c => (Candidate: c, Weight: weight(c))).ToList();
        var drawn = new List<RadioCandidate>();
        var taken = new Dictionary<Guid, int>();
        while (drawn.Count < count && remaining.Count > 0)
        {
            var roll = random.NextDouble() * remaining.Sum(r => r.Weight);
            var at = 0;
            for (; at < remaining.Count - 1 && roll >= remaining[at].Weight; at++)
            {
                roll -= remaining[at].Weight;
            }

            var pick = remaining[at].Candidate;
            drawn.Add(pick);
            remaining.RemoveAt(at);
            if (perArtist is { } cap && (taken[pick.ArtistId] = taken.GetValueOrDefault(pick.ArtistId) + 1) >= cap)
            {
                remaining.RemoveAll(r => r.Candidate.ArtistId == pick.ArtistId);
            }
        }

        return drawn;
    }

    /// <summary>The newest albums by when they were added, in one music library or (null) all of them.</summary>
    public static async Task<List<ItemSummaryDto>> NewAlbumsAsync(FlexarrDbContext db, string profileId, Guid? libraryId, int size, CancellationToken ct) =>
        await db.MediaItems.Where(i => i.Kind == MediaKind.Album && (libraryId == null || i.LibraryId == libraryId))
            .OrderByDescending(i => i.AddedAt).ThenBy(i => i.SortTitle).Take(size).Summaries(profileId).ToListAsync(ct);

    /// <summary>Summaries of these items, in this order (a query alone comes back in the database's order).</summary>
    public static async Task<List<ItemSummaryDto>> InOrderAsync(FlexarrDbContext db, IEnumerable<Guid> ids, string profileId, CancellationToken ct)
    {
        var order = ids.Distinct().ToList();
        if (order.Count == 0)
        {
            return [];
        }

        var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return order.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }
}
