using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Puts a music request to Lidarr: an album on its own (the artist added unmonitored if Lidarr hasn't got them), or an
/// artist followed for their new releases. Throws <see cref="HttpRequestException"/> when Lidarr refuses or doesn't answer.
/// </summary>
public static partial class MusicRequestPlacer
{
    public static async Task<IResult> PlaceAsync(MusicRequestContext c, string profileId, MusicRequestBody body, ILogger logger, CancellationToken ct)
    {
        var (target, problem) = await MusicDestinations.ForRequestAsync(c.Client, c.Lidarrs, c.Options.PathMappings, c.Db, profileId, c.Cache, ct);
        if (target is null)
        {
            return problem is null
                ? Results.Problem("Lidarr isn't answering. Try again later.", statusCode: 502)
                : Results.Problem(problem, statusCode: 409);
        }

        var result = body.Kind == "album" ? await AlbumAsync(c, target, profileId, body, logger, ct) : await ArtistAsync(c, target, profileId, body, logger, ct);
        MusicMatcher.Forget(c.Cache, target.Lidarr);
        return result;
    }

    private static async Task<IResult> AlbumAsync(MusicRequestContext c, MusicTarget target, string profileId, MusicRequestBody body, ILogger logger, CancellationToken ct)
    {
        // The album's MusicBrainz release group, which is what Lidarr downloads: given, or found from the Deezer
        // album's barcode. Names are only the fallback, for an album MusicBrainz can't be asked about.
        DeezerAlbum? deezer = null;
        string? releaseGroup;
        if (body.DeezerId is { } deezerId)
        {
            deezer = await c.Catalog.AlbumAsync(deezerId, ct);
            releaseGroup = (await ReleaseAsync(c, deezer, logger, ct))?.ReleaseGroupId;
        }
        else if (body.MusicBrainzId is { Length: > 0 } given)
        {
            releaseGroup = given;
        }
        else
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["deezerId"] = ["An album needs its deezerId or musicBrainzId."] });
        }

        var album = releaseGroup is null ? null
            : (await c.Client.LookupAlbumsAsync(target.Lidarr, $"lidarr:{releaseGroup}", ct)).FirstOrDefault(a => a.ForeignAlbumId == releaseGroup && a.Artist is not null);
        if (album is null && deezer is null)
        {
            return Results.Problem("Lidarr can't find this album.", statusCode: 409);
        }

        var (title, artistName, year) = album is not null
            ? (album.Title, album.Artist!.ArtistName, album.ReleaseDate?.Year)
            : (deezer!.Title, deezer.Artist?.Name ?? "", deezer.Year);
        var owned = await c.OwnedAsync(ct);
        if (owned.AlbumId(artistName, title) is not null || (deezer is not null && owned.AlbumId(deezer.Artist?.Name, deezer.Title) is not null))
        {
            return Results.Problem("It's already in your library.", statusCode: 409);
        }

        var artist = album?.Artist is { ForeignArtistId.Length: > 0 } lookedUp
            ? (await c.Client.LookupArtistsAsync(target.Lidarr, $"lidarr:{lookedUp.ForeignArtistId}", ct)) is [var full, ..] ? full : lookedUp
            : await MusicMatcher.MatchArtistAsync(c.Client, target.Lidarr, artistName, [title], ct);
        if (artist?.ForeignArtistId is not { Length: > 0 } artistMbid)
        {
            return Results.Problem($"Lidarr can't find {title} by {artistName}.", statusCode: 409);
        }

        // A Lidarr that has the artist already keeps them: the album goes there, not to a second Lidarr.
        var (lidarr, knownArtist) = await c.LidarrArtistAsync(artistMbid, artist.ArtistName, ct) is { } home ? (home.Lidarr, home.Artist) : (target.Lidarr, artist);

        // Lidarr's own copy of the album, when it has the artist: its id is what monitoring and searching take.
        var match = await MusicMatcher.MatchAlbumAsync(c.Client, lidarr, knownArtist, title, year, ct, album?.ForeignAlbumId);
        if (match?.ForeignAlbumId is not { Length: > 0 } albumMbid)
        {
            return Results.Problem($"Lidarr can't find {title} by {artist.ArtistName}.", statusCode: 409);
        }

        if (match.Id > 0)
        {
            await c.Client.SetAlbumsMonitoredAsync(lidarr, [match.Id], monitored: true, ct);
            await c.Client.SearchAlbumsAsync(lidarr, [match.Id], ct);
        }
        else
        {
            await c.Client.AddAlbumAsync(lidarr, albumMbid, match.Title, artistMbid, artist.ArtistName, target.Placement, ct);
        }

        MusicMatcher.Forget(c.Cache, lidarr);
        if (knownArtist.Id > 0)
        {
            c.ForgetAlbums(lidarr, knownArtist.Id);
        }

        await c.RememberAsync(new MediaRequest
        {
            ProfileId = profileId, Kind = MediaKind.Album, MusicBrainzId = albumMbid, ArtistMusicBrainzId = artistMbid, Title = title, ArtistName = artistName,
            // Lidarr hadn't the artist: cancelling this can take them out again.
            AddedArtist = knownArtist.Id == 0,
        }, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ArtistAsync(MusicRequestContext c, MusicTarget target, string profileId, MusicRequestBody body, ILogger logger, CancellationToken ct)
    {
        string name;
        string? musicBrainzId = null;
        List<string> known = [];
        string? photo = null;
        if (body.DeezerId is { } deezerId)
        {
            var deezer = await c.Catalog.ArtistAsync(deezerId, ct);
            photo = deezer.Picture;
            name = deezer.Name;
            var albums = await c.Catalog.ArtistAlbumsAsync(deezerId, ct);
            known = albums.Where(a => a.RecordType == "album").Select(a => a.Title).Take(2).ToList();
            musicBrainzId = await ArtistByBarcodeAsync(c, deezer, albums, logger, ct);
        }
        else if (body.ArtistId is { } libraryArtist
            && await c.Db.MediaItems.AsNoTracking().Where(i => i.Id == libraryArtist && i.Kind == MediaKind.Artist).Select(i => new { i.Title, i.MusicBrainzId }).FirstOrDefaultAsync(ct) is { } item)
        {
            (name, musicBrainzId) = (item.Title, item.MusicBrainzId);
            known = await c.Db.MediaItems.AsNoTracking().Where(i => i.ParentId == libraryArtist).Select(i => i.Title).Take(2).ToListAsync(ct);
        }
        else
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["deezerId"] = ["An artist needs its deezerId or artistId."] });
        }

        // By MusicBrainz id when there is one; by name (and their albums, for a name several artists share) otherwise.
        var artist = (musicBrainzId is not null
                ? (await c.Client.LookupArtistsAsync(target.Lidarr, $"lidarr:{musicBrainzId}", ct)).FirstOrDefault(a => a.ForeignArtistId == musicBrainzId)
                : null)
            ?? await MusicMatcher.MatchArtistAsync(c.Client, target.Lidarr, name, known, ct);
        if (artist?.ForeignArtistId is not { Length: > 0 } artistMbid)
        {
            return Results.Problem($"Lidarr can't find {name}.", statusCode: 409);
        }

        // Any Lidarr may already have them (not only the one new music goes to): follow them there.
        (ArrInstance Lidarr, int Id) followed;
        if (await c.LidarrArtistAsync(artistMbid, artist.ArtistName, ct) is { Lidarr: var lidarr, Artist: var had })
        {
            await c.Client.FollowArtistAsync(lidarr, had.Id, ct);
            await GetLatestAlbumAsync(c, lidarr, had.Id, ct);
            MusicMatcher.Forget(c.Cache, lidarr);
            followed = (lidarr, had.Id);
        }
        else
        {
            var added = await c.Client.AddFollowedArtistAsync(target.Lidarr, artistMbid, artist.ArtistName, target.Placement, ct);
            followed = (target.Lidarr, added?.Id ?? 0);
        }

        // In the Artists list from now on, even before any of their music arrives.
        await MusicPlaceholders.AddAsync(c.Db, target.LibraryId, artist.ArtistName, artistMbid, photo ?? artist.RemotePoster, followed.Lidarr.Name, followed.Id, ct);

        await c.RememberAsync(new MediaRequest { ProfileId = profileId, Kind = MediaKind.Artist, MusicBrainzId = artistMbid, Title = artist.ArtistName }, ct);
        return Results.Accepted();
    }

    /// <summary>
    /// Following an artist Lidarr already has gets their latest album too (a new artist gets it from Lidarr's own
    /// "latest" option): the newest studio album out so far, monitored and searched for unless it's here already.
    /// </summary>
    private static async Task GetLatestAlbumAsync(MusicRequestContext c, ArrInstance lidarr, int artistId, CancellationToken ct)
    {
        var latest = (await c.Client.GetArtistAlbumsAsync(lidarr, artistId, ct))
            .Where(a => a.AlbumType == "Album" && a.SecondaryTypes.Count == 0 && a.ReleaseDate is { } d && d <= DateTime.UtcNow)
            .MaxBy(a => a.ReleaseDate);
        if (latest is null || latest.Statistics is { TrackFileCount: > 0 } && latest.Statistics.TrackFileCount >= latest.Statistics.TrackCount)
        {
            return;
        }

        if (!latest.Monitored)
        {
            await c.Client.SetAlbumsMonitoredAsync(lidarr, [latest.Id], monitored: true, ct);
        }

        await c.Client.SearchAlbumsAsync(lidarr, [latest.Id], ct);
        c.ForgetAlbums(lidarr, artistId);
    }

    /// <summary>
    /// The MusicBrainz release behind a Deezer album, by its barcode. Null when it has no barcode, MusicBrainz doesn't
    /// know it, or MusicBrainz doesn't answer (then names have to do).
    /// </summary>
    private static async Task<MusicBrainzRelease?> ReleaseAsync(MusicRequestContext c, DeezerAlbum album, ILogger logger, CancellationToken ct)
    {
        if (album.Upc is not { Length: > 0 } upc)
        {
            return null;
        }

        try
        {
            return await c.Catalog.ReleaseByBarcodeAsync(upc, album.Title, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            LogMusicBrainzFailed(logger, ex, album.Title);
            return null;
        }
    }

    /// <summary>
    /// A Deezer artist's MusicBrainz id, from the barcode of one of their releases (albums first, at most three tried):
    /// the artist credited on it under their name, or its only artist.
    /// </summary>
    private static async Task<string?> ArtistByBarcodeAsync(MusicRequestContext c, DeezerArtist artist, IReadOnlyList<DeezerAlbum> albums, ILogger logger, CancellationToken ct)
    {
        var key = MusicNames.Normalise(artist.Name);
        foreach (var listed in albums.OrderBy(a => a.RecordType == "album" ? 0 : 1).Take(3))
        {
            DeezerAlbum full;
            try
            {
                full = await c.Catalog.AlbumAsync(listed.Id, ct);
            }
            catch (Exception ex) when (ex is DeezerException or HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                continue; // its barcode is only a shortcut: names are the fallback
            }

            var release = await ReleaseAsync(c, full, logger, ct);
            var credited = release?.Artists.FirstOrDefault(a => MusicNames.Normalise(a.Name) == key)
                ?? (release?.Artists is [var only] && only.Id != VariousArtists ? only : null);
            if (credited is not null)
            {
                return credited.Id;
            }
        }

        return null;
    }

    /// <summary>MusicBrainz's "Various Artists": a compilation's credit, never one artist.</summary>
    private const string VariousArtists = "89ad4ac3-39f7-470e-963a-56509c546377";

    [LoggerMessage(Level = LogLevel.Warning, Message = "MusicBrainz didn't answer about {Album}; matching it to Lidarr by name")]
    private static partial void LogMusicBrainzFailed(ILogger logger, Exception ex, string album);
}
