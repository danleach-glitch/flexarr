using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Puts a music request to Lidarr: an album on its own (the artist added unmonitored if Lidarr hasn't got them), or an
/// artist followed for their new releases. Throws <see cref="HttpRequestException"/> when Lidarr refuses or doesn't answer.
/// </summary>
public static class MusicRequestPlacer
{
    public static async Task<IResult> PlaceAsync(MusicRequestContext c, string profileId, MusicRequestBody body, CancellationToken ct)
    {
        if (await MusicDestinations.ForRequestAsync(c.Client, c.Lidarrs, c.Options.PathMappings, c.Db, profileId, ct) is not { } target)
        {
            return Results.Problem("Lidarr didn't answer, or has no root folder to add music to.", statusCode: 502);
        }

        var result = body.Kind == "album" ? await AlbumAsync(c, target, profileId, body, ct) : await ArtistAsync(c, target, profileId, body, ct);
        MusicMatcher.Forget(c.Cache, target.Lidarr);
        return result;
    }

    private static async Task<IResult> AlbumAsync(MusicRequestContext c, MusicTarget target, string profileId, MusicRequestBody body, CancellationToken ct)
    {
        string title, artistName;
        int? year;
        List<string> known;
        LidarrAlbum? album = null;
        if (body.DeezerId is { } deezerId)
        {
            var deezer = await c.Catalog.AlbumAsync(deezerId, ct);
            (title, artistName, year, known) = (deezer.Title, deezer.Artist?.Name ?? "", deezer.Year, [deezer.Title]);
        }
        else if (body.MusicBrainzId is { Length: > 0 } releaseGroup)
        {
            album = (await c.Client.LookupAlbumsAsync(target.Lidarr, $"lidarr:{releaseGroup}", ct)).FirstOrDefault(a => a.ForeignAlbumId == releaseGroup);
            if (album?.Artist is null)
            {
                return Results.Problem("Lidarr doesn't know this album.", statusCode: 409);
            }

            (title, artistName, year, known) = (album.Title, album.Artist.ArtistName, album.ReleaseDate?.Year, [album.Title]);
        }
        else
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["deezerId"] = ["An album needs its deezerId or musicBrainzId."] });
        }

        if ((await c.OwnedAsync(ct)).AlbumId(artistName, title) is not null)
        {
            return Results.Problem("It's already in your library.", statusCode: 409);
        }

        var artist = album?.Artist is { ForeignArtistId.Length: > 0 } lookedUp
            ? (await c.Client.LookupArtistsAsync(target.Lidarr, $"lidarr:{lookedUp.ForeignArtistId}", ct)) is [var full, ..] ? full : lookedUp
            : await MusicMatcher.MatchArtistAsync(c.Client, target.Lidarr, artistName, known, ct);
        if (artist?.ForeignArtistId is not { Length: > 0 } artistMbid)
        {
            return Results.Problem($"Lidarr doesn't know {artistName}, or knows more than one artist of that name.", statusCode: 409);
        }

        // Lidarr's own copy of the album, when it has the artist: its id is what monitoring and searching take.
        var match = await MusicMatcher.MatchAlbumAsync(c.Client, target.Lidarr, artist, title, year, ct);
        if (match?.ForeignAlbumId is not { Length: > 0 } albumMbid)
        {
            return Results.Problem($"Lidarr doesn't know {title} by {artist.ArtistName}.", statusCode: 409);
        }

        if (match.Id > 0)
        {
            await c.Client.SetAlbumsMonitoredAsync(target.Lidarr, [match.Id], monitored: true, ct);
            await c.Client.SearchAlbumsAsync(target.Lidarr, [match.Id], ct);
        }
        else
        {
            await c.Client.AddAlbumAsync(target.Lidarr, albumMbid, match.Title, artistMbid, artist.ArtistName, target.Placement, ct);
        }

        await c.RememberAsync(new MediaRequest
        {
            ProfileId = profileId, Kind = MediaKind.Album, MusicBrainzId = albumMbid, ArtistMusicBrainzId = artistMbid, Title = title, ArtistName = artistName,
        }, ct);
        return Results.Accepted();
    }

    private static async Task<IResult> ArtistAsync(MusicRequestContext c, MusicTarget target, string profileId, MusicRequestBody body, CancellationToken ct)
    {
        string name;
        string? musicBrainzId = null;
        List<string> known = [];
        if (body.DeezerId is { } deezerId)
        {
            var deezer = await c.Catalog.ArtistAsync(deezerId, ct);
            name = deezer.Name;
            known = (await c.Catalog.ArtistAlbumsAsync(deezerId, ct)).Where(a => a.RecordType == "album").Select(a => a.Title).Take(2).ToList();
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

        var artist = musicBrainzId is not null
            ? (await c.Client.LookupArtistsAsync(target.Lidarr, $"lidarr:{musicBrainzId}", ct)) is [var found, ..] ? found : null
            : await MusicMatcher.MatchArtistAsync(c.Client, target.Lidarr, name, known, ct);
        if (artist?.ForeignArtistId is not { Length: > 0 } artistMbid)
        {
            return Results.Problem($"Lidarr doesn't know {name}, or knows more than one artist of that name.", statusCode: 409);
        }

        // Any Lidarr may already have them (not only the one new music goes to): follow them there.
        if (await c.LidarrArtistAsync(artistMbid, artist.ArtistName, ct) is { Lidarr: var lidarr, Artist: var had })
        {
            await c.Client.FollowArtistAsync(lidarr, had.Id, ct);
            MusicMatcher.Forget(c.Cache, lidarr);
        }
        else
        {
            await c.Client.AddFollowedArtistAsync(target.Lidarr, artistMbid, artist.ArtistName, target.Placement, ct);
        }

        await c.RememberAsync(new MediaRequest { ProfileId = profileId, Kind = MediaKind.Artist, MusicBrainzId = artistMbid, Title = artist.ArtistName }, ct);
        return Results.Accepted();
    }
}
