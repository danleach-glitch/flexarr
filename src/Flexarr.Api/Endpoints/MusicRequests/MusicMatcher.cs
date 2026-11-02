using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
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
public sealed class OwnedMusic(Dictionary<string, Guid> artists, Dictionary<string, Guid> albums, IReadOnlyList<string>? visibleFolders = null)
{
    public OwnedMusic()
        : this([], [])
    {
    }

    /// <summary>Library artists by normalised name. Shared between calls (cached): never changed after it's built.</summary>
    public Dictionary<string, Guid> Artists { get; } = artists;

    /// <summary>Library albums by "artist|title", both normalised. Shared like <see cref="Artists"/>.</summary>
    public Dictionary<string, Guid> Albums { get; } = albums;

    /// <summary>
    /// The music library folders the profile can see, as path keys; null when it can see them all. Lidarr's own state
    /// (who it follows, what it monitors) counts only for artists inside these.
    /// </summary>
    public IReadOnlyList<string>? VisibleFolders { get; } = visibleFolders;

    /// <summary>Whether a Lidarr artist's folder (already mapped to this server's paths) is in a library the profile can see.</summary>
    public bool Sees(string? localPath) =>
        VisibleFolders is null || (localPath is not null && VisibleFolders.Any(f => PathComparison.Key(localPath).StartsWith(f.TrimEnd('/') + "/", StringComparison.Ordinal)));

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
    /// <summary>
    /// How long a Lidarr's artist list is kept: it's dropped after every request Flexarr places, so only changes made in
    /// Lidarr itself wait this long.
    /// </summary>
    private static readonly TimeSpan LidarrArtistsFor = TimeSpan.FromMinutes(5);

    /// <summary>A Lidarr that didn't answer isn't asked again for this long, so every page doesn't wait on it.</summary>
    private static readonly TimeSpan LidarrDownFor = TimeSpan.FromSeconds(30);

    /// <summary>The library's artists and albums are worked out at most once a minute per set of visible libraries.</summary>
    private static readonly TimeSpan LibraryFor = TimeSpan.FromMinutes(1);

    /// <summary>How long an album asked for reads as Requested. After that it can be asked for again (Lidarr searches again).</summary>
    public static readonly TimeSpan RequestedFor = TimeSpan.FromDays(14);

    /// <summary>
    /// The music libraries' artists and albums the profile can see (the context's filters hide the rest), the albums
    /// anyone has asked for lately, and the artists Lidarr follows in libraries the profile can see.
    /// </summary>
    public static async Task<OwnedMusic> OwnedAsync(FlexarrDbContext db, ArrClient client, IReadOnlyList<ArrInstance> lidarrs, IMemoryCache cache,
        IReadOnlyList<PathMapping> mappings, CancellationToken ct)
    {
        var hidden = string.Join(',', db.HiddenLibraryIds.Order());
        var library = await cache.GetOrCreateAsync($"owned-music:{hidden}", async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = LibraryFor;
            entry.AddExpirationToken(new Microsoft.Extensions.Primitives.CancellationChangeToken(Volatile.Read(ref _libraryChanged).Token));
            var artists = new Dictionary<string, Guid>();
            // A followed artist's placeholder isn't music you have: their pages still offer Following.
            foreach (var artist in await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Artist && !i.Placeholder).Select(i => new { i.Id, i.Title }).ToListAsync(ct))
            {
                artists.TryAdd(MusicNames.Normalise(artist.Title), artist.Id);
            }

            var albums = new Dictionary<string, Guid>();
            foreach (var album in await db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Album && i.Parent != null)
                .Select(i => new { i.Id, i.Title, Artist = i.Parent!.Title }).ToListAsync(ct))
            {
                albums.TryAdd(OwnedMusic.AlbumKey(album.Artist, album.Title), album.Id);
            }

            // A profile that hides a music library sees Lidarr's follows only for the folders it can see.
            List<string>? visible = null;
            if (db.HiddenLibraryIds.Count > 0)
            {
                visible = await db.Libraries.AsNoTracking().Where(l => l.Type == LibraryType.Music).SelectMany(l => l.Folders.Select(f => f.Path)).ToListAsync(ct);
                visible = visible.Select(PathComparison.Key).ToList();
            }

            return (artists, albums, visible);
        });

        var owned = new OwnedMusic(library.artists, library.albums, library.visible);

        // Every profile's: Lidarr is the household's, so what one asked for lately is on its way for all.
        var since = DateTime.UtcNow - RequestedFor;
        var requested = await db.MediaRequests.AsNoTracking().Where(r => r.FulfilledAt == null && r.Kind == MediaKind.Album && r.RequestedAt > since)
            .Select(r => new { r.ArtistName, r.Title }).ToListAsync(ct);
        foreach (var request in requested)
        {
            owned.RequestedAlbums.Add(OwnedMusic.AlbumKey(request.ArtistName, request.Title));
        }

        foreach (var lidarr in lidarrs)
        {
            foreach (var artist in await LidarrArtistsAsync(client, lidarr, cache, ct))
            {
                if (IsFollowed(artist) && owned.Sees(LocalPath(artist, mappings)))
                {
                    owned.FollowedArtists.Add(MusicNames.Normalise(artist.ArtistName));
                }
            }
        }

        return owned;
    }

    /// <summary>A Lidarr artist's folder as this server sees it.</summary>
    public static string? LocalPath(LidarrArtist artist, IReadOnlyList<PathMapping> mappings) => artist.Path is { } path ? PathMapper.Map(path, mappings) : null;

    /// <summary>Lidarr watches for the artist's new releases.</summary>
    public static bool IsFollowed(LidarrArtist artist) => artist.Monitored && artist.MonitorNewItems is "new" or "all";

    /// <summary>
    /// The artists a Lidarr has, kept a few minutes (every music page asks); none when it doesn't answer, which is
    /// remembered for half a minute rather than waited on by every page.
    /// </summary>
    public static async Task<IReadOnlyList<LidarrArtist>> LidarrArtistsAsync(ArrClient client, ArrInstance lidarr, IMemoryCache cache, CancellationToken ct)
    {
        var key = $"lidarr-artists:{lidarr.BaseUrl}";
        if (cache.TryGetValue(key, out IReadOnlyList<LidarrArtist>? hit) && hit is not null)
        {
            return hit;
        }

        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(10));
            var artists = await client.GetArtistsAsync(lidarr, quick.Token);
            cache.Set(key, artists, LidarrArtistsFor);
            return artists;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            cache.Set(key, (IReadOnlyList<LidarrArtist>)[], LidarrDownFor);
            return [];
        }
    }

    private static CancellationTokenSource _libraryChanged = new();

    /// <summary>The library's artists or albums changed here (a placeholder added or removed, a delete): work them out afresh.</summary>
    public static void ForgetLibrary()
    {
        var old = Interlocked.Exchange(ref _libraryChanged, new CancellationTokenSource());
        old.Cancel();
        old.Dispose();
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
    public static async Task<LidarrAlbum?> MatchAlbumAsync(ArrClient client, ArrInstance lidarr, LidarrArtist artist, string title, int? year, CancellationToken ct,
        string? releaseGroup = null)
    {
        var key = MusicNames.NormaliseTitle(title);
        IReadOnlyList<LidarrAlbum> candidates = artist.Id > 0 ? await client.GetArtistAlbumsAsync(lidarr, artist.Id, ct) : [];

        // Known exactly: Lidarr's own copy if it lists it, otherwise its lookup's (an EP or single its metadata profile leaves out).
        if (releaseGroup is not null)
        {
            return candidates.FirstOrDefault(a => a.ForeignAlbumId == releaseGroup)
                ?? (await client.LookupAlbumsAsync(lidarr, $"lidarr:{releaseGroup}", ct)).FirstOrDefault(a => a.ForeignAlbumId == releaseGroup);
        }

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
