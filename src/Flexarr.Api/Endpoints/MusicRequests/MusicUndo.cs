using Flexarr.Core.Media;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Taking music requests back: unfollowing an artist, and cancelling an album asked for. Lidarr is the household's, so
/// both are for everyone: every profile's request rows close, not only the asker's. Undo is idempotent: nothing to
/// take back (already undone, gone, or in a library the profile can't see) answers 204 and changes nothing. Throws
/// <see cref="HttpRequestException"/> when Lidarr refuses or doesn't answer.
/// </summary>
public static partial class MusicUndo
{
    /// <summary>
    /// Lidarr stops watching for the artist's new releases (the artist stays, so albums already asked for still
    /// download), every follow of theirs closes, and a placeholder of theirs that's still empty leaves the library.
    /// </summary>
    public static async Task<IResult> UnfollowAsync(MusicRequestContext c, MusicUndoBody body, CancellationToken ct)
    {
        string? name;
        var musicBrainzId = body.MusicBrainzId is { Length: > 0 } given ? given : null;
        if (body.ArtistId is { } libraryArtist)
        {
            // The context's filters: an artist in a library the profile can't see isn't found.
            var item = await c.Db.MediaItems.AsNoTracking().Where(i => i.Id == libraryArtist && i.Kind == MediaKind.Artist)
                .Select(i => new { i.Title, i.MusicBrainzId }).FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return Results.NoContent();
            }

            (name, musicBrainzId) = (item.Title, item.MusicBrainzId ?? musicBrainzId);
        }
        else if (body.DeezerId is { } deezerId)
        {
            name = (await c.Catalog.ArtistAsync(deezerId, ct)).Name;
        }
        else if (musicBrainzId is not null)
        {
            name = null;
        }
        else
        {
            return Problems.Field("Give the artist’s artistId, deezerId or musicBrainzId.", field: "artistId");
        }

        // By MusicBrainz id when there is one (a same-named artist is someone else); by name only without one.
        var found = musicBrainzId is not null ? await c.LidarrArtistAsync(musicBrainzId, "", ct)
            : name is { Length: > 0 } ? await c.LidarrArtistNamedAsync(name, ct) : null;
        if (found is null && LidarrDown(c))
        {
            return NotAnswering; // couldn't look: never "done" while Lidarr may still follow them
        }

        var owned = await c.OwnedAsync(ct);
        if (found is { Lidarr: var lidarr, Artist: var artist })
        {
            if (!owned.Sees(MusicMatcher.LocalPath(artist, c.Options.PathMappings)))
            {
                return Results.NoContent(); // in a library the profile can't see: not theirs to undo
            }

            if (MusicMatcher.IsFollowed(artist))
            {
                await c.Client.UnfollowArtistAsync(lidarr, artist.Id, ct);
                MusicMatcher.Forget(c.Cache, lidarr);
            }

            musicBrainzId ??= artist.ForeignArtistId;
            name = artist.ArtistName;
        }

        var key = MusicNames.Normalise(name);
        if (musicBrainzId is null && key.Length == 0)
        {
            return Results.NoContent();
        }

        // Narrowed in SQL (by id or exact name), then matched by normalised name.
        var follows = (await c.Db.MediaRequests.Where(r => r.Kind == MediaKind.Artist && r.FulfilledAt == null
                && ((musicBrainzId != null && r.MusicBrainzId == musicBrainzId) || r.MusicBrainzId == null || r.Title == name)).ToListAsync(ct))
            .Where(r => (musicBrainzId is not null && r.MusicBrainzId == musicBrainzId) || (key.Length > 0 && MusicNames.Normalise(r.Title) == key)).ToList();
        foreach (var follow in follows)
        {
            follow.FulfilledAt = DateTime.UtcNow;
        }

        await c.Db.SaveChangesAsync(ct);
        await MusicPlaceholders.RemoveAsync(c.Db, name ?? "", musicBrainzId, ct);
        return Results.NoContent();
    }

    /// <summary>
    /// Lidarr stops wanting the album (unmonitored, and out of its download queue), every request for it closes, and
    /// an artist a request added to Lidarr goes again when nothing else of theirs is wanted, followed or on disk.
    /// </summary>
    public static async Task<IResult> UnrequestAsync(MusicRequestContext c, MusicUndoBody body, MusicImportRetry retry, ILogger logger, CancellationToken ct)
    {
        if (await FindAskedAlbumAsync(c, body, logger, ct) is not { } asked)
        {
            return Problems.Field("Give the album’s musicBrainzId or deezerId.", field: "musicBrainzId");
        }

        var (found, releaseGroupId, title, artistName) = asked;
        if (found is null && LidarrDown(c))
        {
            return NotAnswering;
        }

        var owned = await c.OwnedAsync(ct);
        if (found is { } f && !owned.Sees(MusicMatcher.LocalPath(f.Artist, c.Options.PathMappings)))
        {
            return Results.NoContent(); // in a library the profile can't see: not theirs to undo
        }

        if (found is { Lidarr: var lidarr, Artist: var artist, Album: var lidarrAlbum })
        {
            if (lidarrAlbum.Monitored)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, [lidarrAlbum.Id], monitored: false, ct);
            }

            await DropDownloadsAsync(c, lidarr, lidarrAlbum.Id, ct);
            c.ForgetAlbums(lidarr, artist.Id);
            retry.StartAfresh(lidarr, lidarrAlbum.Id, new[] { releaseGroupId, lidarrAlbum.ForeignAlbumId }.OfType<string>().ToList()); // asking again later gets its full share of tries
        }

        var groups = new[] { releaseGroupId, found?.Album.ForeignAlbumId }.OfType<string>().Distinct().ToList();
        (title, artistName) = found is { Album: var fa2, Artist: var fr } ? (fa2.Title, fr.ArtistName) : (title, artistName);
        var requests = await c.Db.MediaRequests.Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null
            && ((r.MusicBrainzId != null && groups.Contains(r.MusicBrainzId)) || (title != null && r.Title == title && r.ArtistName == artistName))).ToListAsync(ct);
        foreach (var request in requests)
        {
            request.FulfilledAt = DateTime.UtcNow;
        }

        await c.Db.SaveChangesAsync(ct);

        if (found is { Lidarr: var home, Artist: var added } && added.ForeignArtistId is { } artistMbid
            && await c.Db.MediaRequests.AnyAsync(r => r.Kind == MediaKind.Album && r.AddedArtist && r.ArtistMusicBrainzId == artistMbid, ct))
        {
            await TidyAsync(c, home, added, logger, ct);
        }

        if (found is { Lidarr: var changed })
        {
            MusicMatcher.Forget(c.Cache, changed);
            MusicRequested.Forget(c.Cache);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// Tries a stuck album again: its count of tries starts over, the copies Lidarr gave up on for missing tracks are
    /// blocklisted (a complete one that scored low stays for importing by hand, and one shared with another album stays, as
    /// for an unrequest) and it's searched for. Only for an album somebody has an open request for, so this can't set off a
    /// search for anything else, and only if it's stuck: a failed copy in the queue with no other on its way, or nothing in
    /// the queue and no files.
    /// Nothing to do, or an album this profile can't see, answers 204; one that isn't stuck, or was tried just now, 409.
    /// A search by id doesn't ask whether an album is monitored, so one Lidarr has unmonitored stays so, until a copy of it
    /// is blocklisted: then, as in the sweep, it's monitored, or nothing goes on looking if this search finds nothing.
    /// </summary>
    /// <param name="c">The services.</param>
    /// <param name="body">Which album.</param>
    /// <param name="retry">The sweep, which shares what's known about the album's tries.</param>
    /// <param name="searched">Called once the indexers have been asked, which is what counts against the day's asking.</param>
    /// <param name="logger">The log.</param>
    /// <param name="ct">Stops it.</param>
    public static async Task<IResult> RetryAsync(MusicRequestContext c, MusicUndoBody body, MusicImportRetry retry, Action searched, ILogger logger, CancellationToken ct)
    {
        if (await FindAskedAlbumAsync(c, body, logger, ct) is not { } asked)
        {
            return Problems.Field("Give the album’s musicBrainzId or deezerId.", field: "musicBrainzId");
        }

        if (asked.Found is not { Lidarr: var lidarr, Artist: var artist, Album: var album })
        {
            return LidarrDown(c) ? NotAnswering : Results.NoContent();
        }

        var owned = await c.OwnedAsync(ct);
        var groups = new[] { asked.ReleaseGroupId, album.ForeignAlbumId }.OfType<string>().Distinct().ToList();
        var requested = await c.Db.MediaRequests.Where(r => r.Kind == MediaKind.Album && r.FulfilledAt == null && r.MusicBrainzId != null && groups.Contains(r.MusicBrainzId))
            .Select(r => r.MusicBrainzId).FirstOrDefaultAsync(ct);
        if (!owned.Sees(MusicMatcher.LocalPath(artist, c.Options.PathMappings)) || requested is null)
        {
            return Results.NoContent();
        }

        // Looked at before anything's changed: asking about an album that's fine must leave it as it was.
        var mine = await AlbumQueueAsync(c, lidarr, album.Id, ct);
        var failed = mine.Where(q => q.TrackedDownloadState is "importBlocked" or "importFailed").ToList();
        var nothing = mine.Count == 0 && album.Statistics is not { TrackFileCount: > 0 };
        if ((failed.Count == 0 && !nothing) || mine.Any(MusicRequested.IsMoving))
        {
            return Problems.Of("That album isn’t stuck.", status: 409);
        }

        // With no failed copy, the last search's answer may simply not have come in yet.
        if ((failed.Count == 0 && retry.SearchedLately(lidarr, album.Id)) || !retry.TakeTurn(lidarr, album.Id))
        {
            return Problems.Of("That album was tried just now. Try again in a few minutes.", status: 409);
        }

        var done = false;
        try
        {
            retry.StartAfresh(lidarr, album.Id, groups);
            var hopeless = failed.Where(MusicImportRetry.SaysTracksAreMissing).ToList();
            var shared = await SharedDownloadsAsync(c, lidarr, album.Id, hopeless, ct);
            var blocklisting = hopeless.Where(q => q.DownloadId is null || !shared.Contains(q.DownloadId)).ToList();
            if (blocklisting.Count > 0 && !album.Monitored)
            {
                await c.Client.SetAlbumsMonitoredAsync(lidarr, [album.Id], monitored: true, ct);
            }

            foreach (var copy in blocklisting)
            {
                await c.Client.BlocklistFromQueueAsync(lidarr, copy.Id, ct);
                retry.OweSearch(lidarr, album.Id, requested); // should the search fail, nothing else will bring this back to mind
            }

            await c.Client.SearchAlbumsAsync(lidarr, [album.Id], ct);
            done = true;
        }
        finally
        {
            if (!done)
            {
                retry.GiveBackTurn(lidarr, album.Id);
            }
        }

        retry.Searched(lidarr, album.Id);
        searched();
        c.ForgetAlbums(lidarr, artist.Id);
        MusicMatcher.Forget(c.Cache, lidarr);
        MusicRequested.Forget(c.Cache);
        LogTriedAgain(logger, album.Title);
        return Results.NoContent();
    }

    /// <summary>What's being asked about: in Lidarr if <see cref="Found"/> isn't null, with what's known of it otherwise.</summary>
    private sealed record AskedAlbum((ArrInstance Lidarr, LidarrArtist Artist, LidarrAlbum Album)? Found, string? ReleaseGroupId, string? Title, string? ArtistName);

    /// <summary>The album a body names, as far as it can be found; null when the body doesn't name one.</summary>
    private static async Task<AskedAlbum?> FindAskedAlbumAsync(MusicRequestContext c, MusicUndoBody body, ILogger logger, CancellationToken ct)
    {
        (ArrInstance Lidarr, LidarrArtist Artist, LidarrAlbum Album)? found = null;
        string? releaseGroupId;
        if (body.MusicBrainzId is { Length: > 0 } releaseGroup)
        {
            releaseGroupId = releaseGroup;
        }
        else if (body.DeezerId is { } deezerId)
        {
            // The release group the request itself would have found, by the album's barcode; the exact title (edition
            // and all) under Lidarr's artist of that name only when MusicBrainz can't say.
            var deezer = await c.Catalog.AlbumAsync(deezerId, ct);
            releaseGroupId = (await MusicRequestPlacer.ReleaseAsync(c, deezer, logger, ct))?.ReleaseGroupId;
            if (releaseGroupId is null && await c.LidarrArtistNamedAsync(deezer.Artist?.Name ?? "", ct) is { Lidarr: var l, Artist: var a }
                && await c.LidarrAlbumsAsync(l, a.Id, ct) is { } albums
                && albums.Where(x => MusicNames.Normalise(x.Title) == MusicNames.Normalise(deezer.Title))
                    .OrderBy(x => deezer.Year is { } y && x.ReleaseDate is { } d ? Math.Abs(d.Year - y) : 0).FirstOrDefault() is { } album)
            {
                found = (l, a, album);
                releaseGroupId = album.ForeignAlbumId;
            }
        }
        else
        {
            return null;
        }

        if (found is null && releaseGroupId is not null)
        {
            found = await ByReleaseGroupAsync(c, releaseGroupId, ct);
        }

        // Lidarr may hold it under another release group (asked for by name when MusicBrainz didn't answer): the exact
        // title under Lidarr's artist of that name.
        string? title = null, artistName = null;
        if (found is null && body.DeezerId is { } byDeezer)
        {
            var deezer = await c.Catalog.AlbumAsync(byDeezer, ct);
            (title, artistName) = (deezer.Title, deezer.Artist?.Name);
            if (await c.LidarrArtistNamedAsync(deezer.Artist?.Name ?? "", ct) is { Lidarr: var fl, Artist: var fa }
                && await c.LidarrAlbumsAsync(fl, fa.Id, ct) is { } exact
                && exact.Where(x => MusicNames.Normalise(x.Title) == MusicNames.Normalise(deezer.Title))
                    .OrderBy(x => deezer.Year is { } y && x.ReleaseDate is { } d ? Math.Abs(d.Year - y) : 0).FirstOrDefault() is { } byTitle)
            {
                found = (fl, fa, byTitle);
            }
        }

        return new AskedAlbum(found, releaseGroupId, title, artistName);
    }

    /// <summary>
    /// Takes the album's downloads out of Lidarr's queue. A download that also carries other albums (a discography,
    /// say) stays in the download client, only this album's entry leaving Lidarr's queue.
    /// </summary>
    private static async Task DropDownloadsAsync(MusicRequestContext c, ArrInstance lidarr, int albumId, CancellationToken ct)
    {
        var mine = await AlbumQueueAsync(c, lidarr, albumId, ct);
        if (mine.Count == 0)
        {
            return;
        }

        // A download that carries other albums too is left alone: removing it (even from Lidarr's queue only) stops
        // them importing, and Lidarr won't import an album it no longer monitors anyway.
        var shared = await SharedDownloadsAsync(c, lidarr, albumId, mine, ct);
        foreach (var queued in mine.Where(q => q.DownloadId is null || !shared.Contains(q.DownloadId)))
        {
            await c.Client.RemoveFromQueueAsync(lidarr, queued.Id, ct);
        }
    }

    private static async Task<List<ArrQueueItem>> AlbumQueueAsync(MusicRequestContext c, ArrInstance lidarr, int albumId, CancellationToken ct)
    {
        IReadOnlyList<ArrQueueItem> queue;
        try
        {
            queue = await c.Client.GetAlbumQueueAsync(lidarr, [albumId], ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            queue = (await c.Client.GetQueueAsync(lidarr, ct)).Where(q => q.AlbumId == albumId).ToList();
        }

        return queue.Where(q => q.AlbumId == albumId).ToList();
    }

    /// <summary>Which of these downloads also carry another album.</summary>
    private static async Task<HashSet<string>> SharedDownloadsAsync(MusicRequestContext c, ArrInstance lidarr, int albumId, List<ArrQueueItem> mine, CancellationToken ct) =>
        mine.Any(q => q.DownloadId is not null)
            ? (await c.Client.GetQueueAsync(lidarr, ct)).Where(q => q.AlbumId != albumId && q.DownloadId is not null).Select(q => q.DownloadId!).ToHashSet()
            : [];

    /// <summary>
    /// Takes out of Lidarr an artist a cancelled request had added, when nothing else of theirs is monitored, they
    /// aren't followed, Lidarr has none of their files and no other request is waiting on them. Their files (none) stay.
    /// </summary>
    private static async Task TidyAsync(MusicRequestContext c, ArrInstance lidarr, LidarrArtist artist, ILogger logger, CancellationToken ct)
    {
        if (MusicMatcher.IsFollowed(artist))
        {
            return;
        }

        var albums = await c.Client.GetArtistAlbumsAsync(lidarr, artist.Id, ct);
        if (albums.Any(a => a.Monitored || a.Statistics is { TrackFileCount: > 0 }))
        {
            return;
        }

        var mbid = artist.ForeignArtistId;
        if (mbid is not null && await c.Db.MediaRequests.AnyAsync(r => r.FulfilledAt == null
                && ((r.Kind == MediaKind.Album && r.ArtistMusicBrainzId == mbid) || (r.Kind == MediaKind.Artist && r.MusicBrainzId == mbid)), ct))
        {
            return;
        }

        await c.Client.DeleteAsync(lidarr, $"artist/{artist.Id}?deleteFiles=false&addImportListExclusion=false", ct);
        LogTidied(logger, artist.ArtistName);
    }

    /// <summary>The album with this release group in whichever Lidarr has it, with its artist.</summary>
    private static async Task<(ArrInstance, LidarrArtist, LidarrAlbum)?> ByReleaseGroupAsync(MusicRequestContext c, string releaseGroup, CancellationToken ct)
    {
        foreach (var lidarr in c.Lidarrs)
        {
            IReadOnlyList<LidarrAlbum> albums;
            try
            {
                albums = await c.Client.GetAlbumsByForeignIdAsync(lidarr, releaseGroup, ct);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                continue;
            }

            if (albums.FirstOrDefault(a => a.ForeignAlbumId == releaseGroup) is { } album)
            {
                var artist = (await MusicMatcher.LidarrArtistsAsync(c.Client, lidarr, c.Cache, ct)).FirstOrDefault(a => a.Id == album.ArtistId) ?? album.Artist;
                if (artist is not null)
                {
                    return (lidarr, artist, album);
                }
            }
        }

        return null;
    }

    private static IResult NotAnswering => Problems.Of("Lidarr isn’t answering. Try again later.", status: 502);

    private static bool LidarrDown(MusicRequestContext c) => c.Lidarrs.Any(l => MusicMatcher.IsDown(c.Cache, l));

    [LoggerMessage(Level = LogLevel.Information, Message = "Trying {Album} again, as asked")]
    private static partial void LogTriedAgain(ILogger logger, string album);

    [LoggerMessage(Level = LogLevel.Information, Message = "Took {Artist} out of Lidarr: the only request that added them was cancelled")]
    private static partial void LogTidied(ILogger logger, string artist);
}
