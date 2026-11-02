using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// What of music the household has and has asked for, by normalised name (see <see cref="MusicNames"/>): what Deezer
/// lists is checked against it to say <i>In your library</i> or <i>Requested</i>.
/// </summary>
public sealed class OwnedMusic
{
    /// <summary>Library artists by normalised name.</summary>
    public Dictionary<string, Guid> Artists { get; } = [];

    /// <summary>Library albums by "artist|title", both normalised.</summary>
    public Dictionary<string, Guid> Albums { get; } = [];

    /// <summary>Albums someone has asked for that haven't arrived, by "artist|title".</summary>
    public HashSet<string> RequestedAlbums { get; } = [];

    /// <summary>Artists Lidarr watches for new releases, by normalised name.</summary>
    public HashSet<string> FollowedArtists { get; } = [];

    /// <summary>Albums Lidarr monitors (so will download, or has), by "artist|title"; only for artists looked at closely.</summary>
    public HashSet<string> MonitoredAlbums { get; } = [];

    public static string AlbumKey(string? artist, string? title) => $"{MusicNames.Normalise(artist)}|{MusicNames.NormaliseTitle(title)}";

    public Guid? ArtistId(string? name) => Artists.TryGetValue(MusicNames.Normalise(name), out var id) ? id : null;

    public Guid? AlbumId(string? artist, string? title) => Albums.TryGetValue(AlbumKey(artist, title), out var id) ? id : null;

    public AvailabilityDto ArtistAvailability(string? name) =>
        ArtistId(name) is not null ? AvailabilityDto.Available : FollowedArtists.Contains(MusicNames.Normalise(name)) ? AvailabilityDto.Requested : AvailabilityDto.None;

    public AvailabilityDto AlbumAvailability(string? artist, string? title)
    {
        var key = AlbumKey(artist, title);
        return Albums.ContainsKey(key) ? AvailabilityDto.Available
            : RequestedAlbums.Contains(key) || MonitoredAlbums.Contains(key) ? AvailabilityDto.Requested
            : AvailabilityDto.None;
    }
}

/// <summary>
/// Matches Deezer's artists and albums to MusicBrainz (through Lidarr's lookup, which proxies it), and to what the
/// household already has.
/// </summary>
public static class MusicMatcher
{
    private static readonly TimeSpan LidarrArtistsFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The music libraries' artists and albums the profile can see (the context's filters hide the rest), the albums
    /// anyone has requested, and the artists Lidarr follows.
    /// </summary>
    public static async Task<OwnedMusic> OwnedAsync(FlexarrDbContext db, ArrClient client, IReadOnlyList<ArrInstance> lidarrs, IMemoryCache cache, CancellationToken ct)
    {
        var owned = new OwnedMusic();
        var artists = await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Artist).Select(i => new { i.Id, i.Title }).ToListAsync(ct);
        foreach (var artist in artists)
        {
            owned.Artists.TryAdd(MusicNames.Normalise(artist.Title), artist.Id);
        }

        var albums = await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Album && i.Parent != null)
            .Select(i => new { i.Id, i.Title, Artist = i.Parent!.Title }).ToListAsync(ct);
        foreach (var album in albums)
        {
            owned.Albums.TryAdd(OwnedMusic.AlbumKey(album.Artist, album.Title), album.Id);
        }

        // Every profile's: Lidarr is the household's, so what one asked for is on its way for all.
        var requested = await db.MediaRequests.AsNoTracking().Where(r => r.FulfilledAt == null && r.Kind == MediaKind.Album)
            .Select(r => new { r.ArtistName, r.Title }).ToListAsync(ct);
        foreach (var request in requested)
        {
            owned.RequestedAlbums.Add(OwnedMusic.AlbumKey(request.ArtistName, request.Title));
        }

        foreach (var lidarr in lidarrs)
        {
            foreach (var artist in await LidarrArtistsAsync(client, lidarr, cache, ct))
            {
                if (IsFollowed(artist))
                {
                    owned.FollowedArtists.Add(MusicNames.Normalise(artist.ArtistName));
                }
            }
        }

        return owned;
    }

    /// <summary>Lidarr watches for the artist's new releases.</summary>
    public static bool IsFollowed(LidarrArtist artist) => artist.Monitored && artist.MonitorNewItems is "new" or "all";

    /// <summary>The artists a Lidarr has, kept a minute (several rows on one page ask); none when it doesn't answer.</summary>
    public static async Task<IReadOnlyList<LidarrArtist>> LidarrArtistsAsync(ArrClient client, ArrInstance lidarr, IMemoryCache cache, CancellationToken ct)
    {
        var key = $"lidarr-artists:{lidarr.BaseUrl}";
        if (cache.TryGetValue(key, out IReadOnlyList<LidarrArtist>? hit) && hit is not null)
        {
            return hit;
        }

        try
        {
            var artists = await client.GetArtistsAsync(lidarr, ct);
            cache.Set(key, artists, LidarrArtistsFor);
            return artists;
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    /// <summary>A Lidarr's artists will have changed (one was just added or followed).</summary>
    public static void Forget(IMemoryCache cache, ArrInstance lidarr) => cache.Remove($"lidarr-artists:{lidarr.BaseUrl}");

    /// <summary>
    /// The MusicBrainz artist behind a Deezer artist: Lidarr's lookup results with the same name, preferring one Lidarr
    /// has already; with several of that name, the one an album of theirs on Deezer finds. Null when none matches, or
    /// when it can't tell same-named artists apart. Throws <see cref="HttpRequestException"/> when Lidarr doesn't answer.
    /// </summary>
    public static async Task<LidarrArtist?> MatchArtistAsync(ArrClient client, ArrInstance lidarr, string name, IReadOnlyList<string> knownAlbumTitles, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        var named = (await client.LookupArtistsAsync(lidarr, name, ct)).Where(a => MusicNames.Normalise(a.ArtistName) == key && !string.IsNullOrEmpty(a.ForeignArtistId)).ToList();
        if (named.Count <= 1)
        {
            return named.FirstOrDefault();
        }

        if (named.Where(a => a.Id > 0).ToList() is [var had])
        {
            return had;
        }

        // Several artists share the name: ask for one of the Deezer artist's albums and see whose it is.
        foreach (var title in knownAlbumTitles.Take(2))
        {
            var albums = await client.LookupAlbumsAsync(lidarr, $"{name} {title}", ct);
            var theirs = albums.Where(a => MusicNames.NormaliseTitle(a.Title) == MusicNames.NormaliseTitle(title)).Select(a => a.Artist?.ForeignArtistId).ToHashSet();
            if (named.Where(a => theirs.Contains(a.ForeignArtistId)).ToList() is [var only])
            {
                return only;
            }
        }

        return null;
    }

    /// <summary>
    /// The MusicBrainz release group behind a Deezer album: among Lidarr's albums for the artist (when it has them) or
    /// its lookup's, the one with the same title, nearest in year. Null when none has the title.
    /// </summary>
    public static async Task<LidarrAlbum?> MatchAlbumAsync(ArrClient client, ArrInstance lidarr, LidarrArtist artist, string title, int? year, CancellationToken ct)
    {
        var key = MusicNames.NormaliseTitle(title);
        IEnumerable<LidarrAlbum> candidates = artist.Id > 0 ? await client.GetArtistAlbumsAsync(lidarr, artist.Id, ct) : [];
        var match = Nearest(candidates.Where(a => MusicNames.NormaliseTitle(a.Title) == key), year);
        if (match is not null)
        {
            return match;
        }

        // Lidarr lists only the release types its metadata profile allows (often albums alone): its lookup has the rest.
        var looked = await client.LookupAlbumsAsync(lidarr, $"{artist.ArtistName} {title}", ct);
        return Nearest(looked.Where(a => a.Artist?.ForeignArtistId == artist.ForeignArtistId && MusicNames.NormaliseTitle(a.Title) == key), year);
    }

    private static LidarrAlbum? Nearest(IEnumerable<LidarrAlbum> albums, int? year) =>
        albums.OrderBy(a => year is { } y && a.ReleaseDate is { } d ? Math.Abs(d.Year - y) : 0).FirstOrDefault();
}
