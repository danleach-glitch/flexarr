using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Runtime.CompilerServices;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Discovering and requesting music: Deezer for what there is, Lidarr (by MusicBrainz id) for adding it. All under
/// /requests/music, so profiles with an age limit never reach it. See docs/music-requests.md.
/// </summary>
public static partial class MusicRequestEndpoints
{
    private const int RowSize = 20;

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't request the {Kind} {Id}: {Reason}")]
    private static partial void LogRequestRefused(ILogger logger, string kind, string? id, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Lidarr refused or didn't answer a music request ({Status})")]
    private static partial void LogLidarrRefused(ILogger logger, Exception ex, int? status);

    /// <summary>The most albums and follows one profile can ask for in a day.</summary>
    internal const int DailyLimit = 30;

    /// <param name="api">The API root, for the status every profile may ask.</param>
    /// <param name="requests">The /requests group, which turns away profiles with an age limit.</param>
    public static void MapMusicRequestEndpoints(this RouteGroupBuilder api, RouteGroupBuilder requests)
    {
        // Whether music requests are on, so apps can hide them otherwise. To a profile with an age limit, or one that
        // sees no music library, they aren't.
        api.MapGet("/requests/music/status", async (IOptionsMonitor<FlexarrOptions> options, ArrClient client, FlexarrDbContext db, IMemoryCache cache, CancellationToken ct) =>
        {
            var lidarrs = Lidarrs(options.CurrentValue);
            if (lidarrs.Count == 0 || !await SeesMusicAsync(db, ct))
            {
                return new MusicRequestStatusDto(false, false);
            }

            // Every artist page asks: the ping is remembered half a minute.
            var reachable = await cache.GetOrCreateAsync($"lidarr-ping:{lidarrs[0].BaseUrl}", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30);
                // A Lidarr that's away shouldn't hold up every page that asks: five seconds is long enough to say hello.
                using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
                quick.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    return await client.PingAsync(lidarrs[0].BaseUrl, quick.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return false;
                }
            });
            // A Lidarr that answers its ping but just timed out on a real question isn't answering either.
            return new MusicRequestStatusDto(true, reachable && !MusicMatcher.IsDown(cache, lidarrs[0]));
        }).WithTags("Requests");

        var music = requests.MapGroup("/music").AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var db = services.GetRequiredService<FlexarrDbContext>();
            var options = services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;
            if (!options.Arr.Any(a => a.Kind == ArrKind.Lidarr))
            {
                return Results.Problem("Music requests need Lidarr. Add one under Settings › Connections.", statusCode: 409);
            }

            if (!options.MusicRequests.Enabled)
            {
                return Results.Problem("Music requests are turned off.", statusCode: 409);
            }

            return await SeesMusicAsync(db, context.HttpContext.RequestAborted)
                ? await next(context)
                : Results.Problem("This profile can't request music.", statusCode: 403);
        });

        music.MapGet("/discover", async (MusicRequestContext c, CurrentProfile current, CancellationToken ct) =>
        {
            var owned = await c.OwnedAsync(ct);
            var top = await TopArtistsAsync(c.Db, current.Id, 12, ct);
            // Each row stands alone: one that Deezer doesn't answer for is left out, not the page.
            var rows = new List<MusicRowDto>();
            async Task RowAsync(string id, string title, string shape, Func<Task<List<MusicExternalDto>>> items)
            {
                if (await Quietly(items) is { Count: > 0 } found)
                {
                    rows.Add(new MusicRowDto(id, title, shape, found));
                }
            }

            await RowAsync("music-like", "Like artists you play", "artists", () => LikeYoursAsync(c, owned, top.Take(8).ToList(), ct));
            await RowAsync("music-new", "New from your artists", "albums", () => NewFromYoursAsync(c, owned, top, ct));
            await RowAsync("music-charts", "Top albums", "albums", async () => (await c.Catalog.ChartAlbumsAsync(ct)).Select(a => Album(a, owned)).ToList());
            var genres = await Quietly(async () => (await c.Catalog.GenresAsync(ct)).Select(g => new MusicGenreDto(g.Id, g.Name, ImageEndpoints.RemoteUrl(g.Picture))).ToList());
            return rows.Count == 0 && genres is null
                ? Results.Problem("Deezer didn't answer. Try again in a minute.", statusCode: 502)
                : Results.Ok(new MusicDiscoverDto(rows, genres ?? []));
        });

        music.MapGet("/genres/{id:long}", async (long id, MusicRequestContext c, CancellationToken ct) =>
            await DeezerAsync(async () =>
            {
                var genre = (await c.Catalog.GenresAsync(ct)).FirstOrDefault(g => g.Id == id);
                if (genre is null)
                {
                    return Results.NotFound();
                }

                var owned = await c.OwnedAsync(ct);
                return Results.Ok(new MusicGenreDetailDto(genre.Id, genre.Name, (await c.Catalog.GenreArtistsAsync(id, ct)).Select(a => Artist(a, owned)).ToList()));
            }));

        music.MapGet("/search", async (string? q, MusicRequestContext c, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q) || q.Trim().Length < 2)
            {
                return Results.Ok(new MusicSearchDto([], []));
            }

            // No artist or album needs more; longer ones would only fill the cache.
            q = q.Trim() is { Length: > 100 } longer ? longer[..100] : q.Trim();

            return await DeezerAsync(async () =>
            {
                var owned = await c.OwnedAsync(ct);
                var artists = c.Catalog.SearchArtistsAsync(q, 8, ct);
                var albums = c.Catalog.SearchAlbumsAsync(q, 16, ct);
                return Results.Ok(new MusicSearchDto((await artists).Select(a => Artist(a, owned)).ToList(), (await albums).Select(a => Album(a, owned)).ToList()));
            });
        });

        music.MapGet("/artist/{id:long}", async (long id, MusicRequestContext c, CancellationToken ct) =>
            await DeezerAsync(async () =>
            {
                // Deezer's three answers side by side (each a round trip when it isn't cached), while the library is looked at.
                var artistCall = c.Catalog.ArtistAsync(id, ct);
                var albumsCall = c.Catalog.ArtistAlbumsAsync(id, ct);
                var relatedCall = c.Catalog.RelatedAsync(id, ct);
                var owned = await c.OwnedAsync(ct);
                await Task.WhenAll(artistCall, albumsCall, relatedCall);
                var artist = await artistCall;
                var lidarrArtist = await c.LidarrArtistNamedAsync(artist.Name, ct);
                if (lidarrArtist is { } had)
                {
                    await c.NoteMonitoredAsync(owned, had, ct);
                }

                var albums = (await albumsCall).Select(a => Album(a with { Artist = artist }, owned)).ToList();
                var similar = (await relatedCall).Take(RowSize).Select(a => Artist(a, owned)).ToList();
                return Results.Ok(new MusicArtistDetailDto(artist.Id, artist.Name, ImageEndpoints.RemoteUrl(artist.Picture), artist.NbFan, albums, similar,
                    c.IsFollowed(owned, lidarrArtist), owned.ArtistAvailability(artist.Name), owned.ArtistId(artist.Name), owned.LidarrAnswered));
            }));

        music.MapGet("/album/{id:long}", async (long id, MusicRequestContext c, CancellationToken ct) =>
            await DeezerAsync(async () =>
            {
                var album = await c.Catalog.AlbumAsync(id, ct);
                var owned = await c.OwnedAsync(ct);
                var artistName = album.Artist?.Name ?? "";
                if (await c.LidarrArtistNamedAsync(artistName, ct) is { } had)
                {
                    await c.NoteMonitoredAsync(owned, had, ct);
                }

                var tracks = album.Tracks?.Data.Select((t, i) => new MusicTrackDto(t.Title, t.Duration, t.TrackPosition > 0 ? t.TrackPosition : i + 1, Math.Max(1, t.DiskNumber), t.ExplicitLyrics)).ToList() ?? [];
                return Results.Ok(new MusicAlbumDetailDto(album.Id, album.Title, artistName, album.Artist?.Id, ImageEndpoints.RemoteUrl(album.Cover), album.Year, album.RecordType,
                    album.Genres?.Data.Select(g => g.Name).ToList() ?? [], tracks, album.Duration, owned.AlbumAvailability(artistName, album.Title), owned.AlbumId(artistName, album.Title), owned.LidarrAnswered));
            }));

        // A library artist's page: the albums of theirs the library doesn't have, and the artists like them.
        music.MapGet("/library-artist/{id:guid}", async (Guid id, MusicRequestContext c, CancellationToken ct) =>
        {
            var item = await c.Db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.Kind == MediaKind.Artist)
                .Select(i => new { i.Title, i.MusicBrainzId, HasPoster = i.PosterUrl != null, i.MetadataUpdatedAt, i.ArtworkChangedAt }).FirstOrDefaultAsync(ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            return await DeezerAsync(async () =>
            {
                var owned = await c.OwnedAsync(ct);
                var deezer = await c.Catalog.ArtistNamedAsync(item.Title, ct);
                var deezerAlbums = deezer is null ? [] : await c.Catalog.ArtistAlbumsAsync(deezer.Id, ct);
                var lidarrArtist = await c.LidarrArtistAsync(item.MusicBrainzId, item.Title, ct);
                var missing = new List<MusicExternalDto>();
                // An album with no cover anywhere shows its artist instead: their photo here, else Lidarr's, else Deezer's.
                var artistPhoto = item.HasPoster ? ArtworkUrls.For(id, ArtworkUrls.Version(item.MetadataUpdatedAt, item.ArtworkChangedAt), true, false, false, false).Poster
                    : (lidarrArtist is { Lidarr: var pl, Artist: var pa } ? MusicRequested.Photo(pl, pa) : null) ?? ImageEndpoints.RemoteUrl(deezer?.Picture);
                // Lidarr's list when it has the artist and answers; Deezer's albums otherwise.
                var lidarrAlbums = lidarrArtist is { Lidarr: var l, Artist: var la } ? await c.LidarrAlbumsAsync(l, la.Id, ct) : null;
                if (lidarrAlbums is not null)
                {
                    // Lidarr's list, as its metadata profile shapes it (usually albums alone), with Deezer's covers where it has them.
                    // Each Deezer album lends its cover and id to one Lidarr album at most: the same title, nearest in year.
                    var byTitle = deezerAlbums.ToLookup(a => MusicNames.NormaliseTitle(a.Title));
                    var lent = new HashSet<long>();
                    foreach (var album in lidarrAlbums.OrderByDescending(a => a.ReleaseDate))
                    {
                        if (album.Statistics?.TrackFileCount > 0 || owned.AlbumId(item.Title, album.Title) is not null)
                        {
                            continue;
                        }

                        var onDeezer = byTitle[MusicNames.NormaliseTitle(album.Title)].Where(a => !lent.Contains(a.Id))
                            .OrderBy(a => album.ReleaseDate is { } d && a.Year is { } y ? Math.Abs(d.Year - y) : 0).FirstOrDefault();
                        if (onDeezer is not null)
                        {
                            lent.Add(onDeezer.Id);
                        }

                        missing.Add(new MusicExternalDto("album", onDeezer?.Id ?? 0, album.Title, item.Title, deezer?.Id, album.ReleaseDate?.Year ?? onDeezer?.Year,
                            ImageEndpoints.RemoteUrl(onDeezer?.Cover) ?? MusicRequested.Cover(lidarrArtist!.Value.Lidarr, album) ?? artistPhoto, album.Monitored ? AvailabilityDto.Requested : owned.AlbumAvailability(item.Title, album.Title), null,
                            onDeezer?.RecordType ?? album.AlbumType?.ToLowerInvariant(), album.ForeignAlbumId));
                    }
                }
                else
                {
                    missing.AddRange(deezerAlbums.Where(a => a.RecordType == "album" && owned.AlbumId(item.Title, a.Title) is null)
                        .Select(a => Album(a with { Artist = deezer }, owned)).Select(a => a.Image is null ? a with { Image = artistPhoto } : a));
                }

                var similar = deezer is null ? [] : (await c.Catalog.RelatedAsync(deezer.Id, ct)).Take(RowSize).Select(a => Artist(a, owned)).ToList();
                return Results.Ok(new MusicArtistExtrasDto(missing, similar, c.IsFollowed(owned, lidarrArtist), deezer?.Id, owned.LidarrAnswered && (lidarrArtist is null || lidarrAlbums is not null)));
            });
        });

        // The music libraries this profile's requests could go to (every one, for the admin choosing for others).
        music.MapGet("/destinations", async (MusicRequestContext c, CurrentProfile current, CancellationToken ct) =>
        {
            var libraries = current.IsAdmin ? c.Db.Libraries.IgnoreQueryFilters() : c.Db.Libraries;
            var destinations = await MusicDestinations.ListAsync(c.Client, c.Lidarrs, c.Options.PathMappings, libraries, ct);
            return Results.Ok(destinations.Select(d => new MusicDestinationDto(d.LibraryId, d.Name, d.IsDefault)).ToList());
        });

        // Every album Lidarr is waiting for, with how far along each is.
        // Given a library, only artists in its folders (that library's Requested view).
        music.MapGet("/requested", async (Guid? libraryId, MusicRequestContext c, CancellationToken ct) =>
        {
            List<string>? folders = null;
            if (libraryId is { } id)
            {
                folders = await c.Db.Libraries.AsNoTracking().Where(l => l.Id == id && l.Type == LibraryType.Music).SelectMany(l => l.Folders).Select(f => f.Path).ToListAsync(ct);
                if (folders.Count == 0)
                {
                    return Results.NotFound();
                }
            }

            return await MusicRequested.ListAsync(c, folders, ct) is { } listed
                ? Results.Ok(listed)
                : Results.Problem("Lidarr isn't answering. Try again later.", statusCode: 502);
        });

        // Stop following an artist, for the household: Lidarr stops looking for their new releases.
        music.MapPost("/unfollow", async (MusicUndoBody body, MusicRequestContext c, CurrentProfile current, ILoggerFactory loggers, CancellationToken ct) =>
            UndoRefused(current, c.Cache) ?? Counted(current, c.Cache, await LidarrAsync(() => MusicUndo.UnfollowAsync(c, body, ct), loggers.CreateLogger("Flexarr.MusicRequests"), ct)));

        // Cancel an album asked for, for the household: Lidarr stops wanting it and drops its download.
        music.MapPost("/unrequest", async (MusicUndoBody body, MusicRequestContext c, CurrentProfile current, ILoggerFactory loggers, CancellationToken ct) =>
        {
            var logger = loggers.CreateLogger("Flexarr.MusicRequests");
            return UndoRefused(current, c.Cache) ?? Counted(current, c.Cache, await LidarrAsync(() => MusicUndo.UnrequestAsync(c, body, logger, ct), logger, ct));
        });

        music.MapPost("/", async (MusicRequestBody body, MusicRequestContext c, CurrentProfile current, ILoggerFactory loggers, CancellationToken ct) =>
        {
            if (!c.Options.MusicRequests.Enabled)
            {
                return Results.Problem("Music requests are off. An admin can turn them on under Settings › Connections.", statusCode: 409);
            }

            if (body.Kind is not ("album" or "artist") || (body.DeezerId is null && body.MusicBrainzId is null && body.ArtistId is null))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use \"album\" or \"artist\", with a deezerId, musicBrainzId or artistId."] });
            }

            var logger = loggers.CreateLogger("Flexarr.MusicRequests");
            // Lidarr adds and downloads straight away, with no approval: a cap keeps one profile (or a script with its
            // key) from filling the disk. Asking again for the same album reuses its request but still searches the
            // indexers, so what's asked is counted too, not only the requests.
            var since = DateTime.UtcNow.AddDays(-1);
            var asked = AskCount(current, c.Cache);
            if (asked.Value >= DailyLimit
                || await c.Db.MediaRequests.CountAsync(r => r.ProfileId == current.Id && (r.Kind == MediaKind.Album || r.Kind == MediaKind.Artist) && r.RequestedAt > since, ct)
                >= DailyLimit)
            {
                return Results.Problem("That's a lot of music for one day. Try again tomorrow.", statusCode: 429);
            }

            try
            {
                var placed = await MusicRequestPlacer.PlaceAsync(c, current.Id, body, logger, ct);
                if (placed is IStatusCodeHttpResult { StatusCode: < 400 })
                {
                    Interlocked.Increment(ref asked.Value);
                }

                if (placed is Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult { StatusCode: 409 } refused)
                {
                    LogRequestRefused(logger, body.Kind, body.DeezerId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? body.MusicBrainzId ?? body.ArtistId?.ToString(), refused.ProblemDetails.Detail);
                }

                return placed;
            }
            catch (HttpRequestException ex)
            {
                // Lidarr's own words can name its address or folders: they go to the log, not to whoever asked.
                LogLidarrRefused(logger, ex, (int?)ex.StatusCode);
                return ex.StatusCode is { } status && (int)status is >= 400 and < 500
                    ? Results.Problem("Lidarr turned the request down. It may have it already.", statusCode: 502)
                    : Results.Problem("Lidarr isn't answering. Try again later.", statusCode: 502);
            }
            catch (DeezerException ex) when (ex.Code == 800)
            {
                return Results.NotFound();
            }
            catch (Exception ex) when (ex is DeezerException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
            {
                return Results.Problem("Lidarr or Deezer didn't answer in time. Try again in a minute.", statusCode: 502);
            }
        });
    }

    /// <summary>The most unfollows and unrequests one profile can make in an hour.</summary>
    internal const int UndoHourlyLimit = 60;

    /// <summary>
    /// Why this profile can't undo now, or null when it can. Family guests can ask for music but not take back the
    /// household's (Lidarr is shared); everyone else has an hourly cap, as asking has a daily one.
    /// </summary>
    /// <param name="guests">What a family guest is told.</param>
    internal static IResult? UndoRefused(CurrentProfile current, IMemoryCache cache, string guests = "Guests can't undo music requests.")
    {
        if (current.IsGuest)
        {
            return Results.Problem(guests, statusCode: 403);
        }

        return UndoCount(current, cache).Value >= UndoHourlyLimit
            ? Results.Problem("That's a lot of undoing for one hour. Try again later.", statusCode: 429)
            : null;
    }

    /// <summary>An undo (music's, or a film's or show's) that reached Lidarr (or found nothing to do) counts towards the hour's; a malformed one or a failure doesn't.</summary>
    internal static IResult Counted(CurrentProfile current, IMemoryCache cache, IResult result)
    {
        if (result is IStatusCodeHttpResult { StatusCode: < 400 })
        {
            Interlocked.Increment(ref UndoCount(current, cache).Value);
        }

        return result;
    }

    /// <summary>The music requests this profile has placed in the last day, asking again included.</summary>
    private static StrongBox<int> AskCount(CurrentProfile current, IMemoryCache cache) =>
        cache.GetOrCreate($"music-ask:{current.Id}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(1);
            return new StrongBox<int>();
        })!;

    private static StrongBox<int> UndoCount(CurrentProfile current, IMemoryCache cache) =>
        cache.GetOrCreate($"music-undo:{current.Id}", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromHours(1);
            return new StrongBox<int>();
        })!;

    /// <summary>Lidarr's answer, or a plain 502 saying it refused or didn't answer (its own words go to the log).</summary>
    private static async Task<IResult> LidarrAsync(Func<Task<IResult>> answer, ILogger logger, CancellationToken ct)
    {
        try
        {
            return await answer();
        }
        catch (HttpRequestException ex)
        {
            LogLidarrRefused(logger, ex, (int?)ex.StatusCode);
            return ex.StatusCode is { } status && (int)status is >= 400 and < 500
                ? Results.Problem("Lidarr turned that down.", statusCode: 502)
                : Results.Problem("Lidarr isn't answering. Try again later.", statusCode: 502);
        }
        catch (DeezerException ex) when (ex.Code == 800)
        {
            // Said, so clients don't take it for a server without undo (a bare 404).
            return Results.Problem("Deezer doesn't know that any more.", statusCode: 404);
        }
        catch (Exception ex) when (ex is DeezerException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return Results.Problem("Lidarr or Deezer didn't answer in time. Try again in a minute.", statusCode: 502);
        }
    }

    /// <summary>
    /// Whether the profile may have music requests: no age limit, and at least one music library it can see (what
    /// Lidarr downloads has to show up somewhere).
    /// </summary>
    internal static async Task<bool> SeesMusicAsync(FlexarrDbContext db, CancellationToken ct) =>
        !db.IsLimited() && await db.Libraries.AnyAsync(l => l.Type == LibraryType.Music, ct);

    internal static List<ArrInstance> Lidarrs(FlexarrOptions options) =>
        options.MusicRequests.Enabled ? options.Arr.Where(a => a.Kind == ArrKind.Lidarr).ToList() : [];

    internal static MusicExternalDto Artist(DeezerArtist artist, OwnedMusic owned) =>
        new("artist", artist.Id, artist.Name, null, artist.Id, null, ImageEndpoints.RemoteUrl(artist.Picture), owned.ArtistAvailability(artist.Name), owned.ArtistId(artist.Name),
            Followed: owned.IsFollowed(artist.Name));

    internal static MusicExternalDto Album(DeezerAlbum album, OwnedMusic owned) =>
        new("album", album.Id, album.Title, album.Artist?.Name, album.Artist?.Id, album.Year, ImageEndpoints.RemoteUrl(album.Cover),
            owned.AlbumAvailability(album.Artist?.Name, album.Title), owned.AlbumId(album.Artist?.Name, album.Title), album.RecordType);

    /// <summary>Deezer's answers, or a 502 saying it didn't give one.</summary>
    private static async Task<IResult> DeezerAsync(Func<Task<IResult>> answer)
    {
        try
        {
            return await answer();
        }
        catch (Exception ex) when (ex is DeezerException or HttpRequestException || (ex is TaskCanceledException && ex.InnerException is TimeoutException))
        {
            return Results.Problem("Deezer didn't answer. Try again in a minute.", statusCode: 502);
        }
    }

    /// <summary>The artists this profile plays most, by plays, with their names.</summary>
    private static async Task<List<string>> TopArtistsAsync(FlexarrDbContext db, string profileId, int count, CancellationToken ct)
    {
        var top = await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Kind == MediaKind.Track && p.PlayCount > 0)
            .GroupBy(p => p.MediaItem!.Parent!.ParentId!.Value)
            .Select(g => new { ArtistId = g.Key, Plays = g.Sum(p => p.PlayCount) })
            .OrderByDescending(a => a.Plays).Take(count).Select(a => a.ArtistId).ToListAsync(ct);
        var names = await db.MediaItems.AsNoTracking().Where(i => top.Contains(i.Id)).Select(i => new { i.Id, i.Title }).ToDictionaryAsync(i => i.Id, i => i.Title, ct);
        return top.Where(names.ContainsKey).Select(id => names[id]).ToList();
    }

    /// <summary>Artists Deezer relates to the ones this profile plays most, that the library doesn't have, taking turns.</summary>
    private static async Task<List<MusicExternalDto>> LikeYoursAsync(MusicRequestContext c, OwnedMusic owned, List<string> seeds, CancellationToken ct)
    {
        // A seed Deezer doesn't answer for is just left out.
        var related = await Task.WhenAll(seeds.Select(async name => await Quietly(async () =>
            await c.Catalog.ArtistNamedAsync(name, ct) is { } seed ? await c.Catalog.RelatedAsync(seed.Id, ct) : []) ?? []));
        var picked = new List<MusicExternalDto>();
        var seen = new HashSet<long>();
        for (var round = 0; picked.Count < RowSize && related.Any(r => r.Count > round); round++)
        {
            foreach (var list in related.Where(r => r.Count > round))
            {
                var artist = list[round];
                if (owned.ArtistId(artist.Name) is null && seen.Add(artist.Id) && picked.Count < RowSize)
                {
                    picked.Add(Artist(artist, owned));
                }
            }
        }

        return picked;
    }

    /// <summary>Releases from the last year by artists in the library, that it doesn't have yet, newest first.</summary>
    private static async Task<List<MusicExternalDto>> NewFromYoursAsync(MusicRequestContext c, OwnedMusic owned, List<string> top, CancellationToken ct)
    {
        // The artists played most first, then the newest in the library, so a big library doesn't ask Deezer about every artist.
        var recent = await c.Db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Artist).OrderByDescending(i => i.AddedAt).Select(i => i.Title).Take(12).ToListAsync(ct);
        var names = top.Concat(recent).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var albums = await Task.WhenAll(names.Select(async name => await Quietly(async () =>
        {
            if (await c.Catalog.ArtistNamedAsync(name, ct) is not { } artist)
            {
                return Enumerable.Empty<DeezerAlbum>();
            }

            return (await c.Catalog.ArtistAlbumsAsync(artist.Id, ct)).Where(a => string.CompareOrdinal(a.ReleaseDate, since) >= 0 && a.RecordType is "album" or "ep")
                .Select(a => a with { Artist = artist });
        }) ?? []));
        return albums.SelectMany(a => a).Where(a => owned.AlbumId(a.Artist?.Name, a.Title) is null)
            // A joint album turns up under each of its artists you have: once is enough.
            .DistinctBy(a => a.Id)
            .DistinctBy(a => OwnedMusic.AlbumKey(a.Artist?.Name, a.Title))
            .OrderByDescending(a => a.ReleaseDate, StringComparer.Ordinal).Take(RowSize).Select(a => Album(a, owned)).ToList();
    }

    /// <summary>Deezer's answer, or null when it didn't give one (an error, its rate limit, a timeout).</summary>
    private static async Task<T?> Quietly<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is DeezerException or HttpRequestException || (ex is TaskCanceledException && ex.InnerException is TimeoutException))
        {
            return null;
        }
    }
}

/// <summary>What every music endpoint needs, in one parameter.</summary>
public sealed class MusicRequestContext(FlexarrDbContext db, ArrClient client, MusicCatalog catalog, IMemoryCache cache, IOptionsMonitor<FlexarrOptions> options)
{
    public FlexarrDbContext Db => db;
    public ArrClient Client => client;
    public MusicCatalog Catalog => catalog;
    public IMemoryCache Cache => cache;
    public FlexarrOptions Options => options.CurrentValue;
    public List<ArrInstance> Lidarrs => MusicRequestEndpoints.Lidarrs(options.CurrentValue);

    public Task<OwnedMusic> OwnedAsync(CancellationToken ct) => MusicMatcher.OwnedAsync(db, client, Lidarrs, cache, options.CurrentValue.PathMappings, ct);

    /// <summary>The artist of this name in one of the Lidarrs (from their cached lists), or null.</summary>
    public Task<(ArrInstance Lidarr, LidarrArtist Artist)?> LidarrArtistNamedAsync(string name, CancellationToken ct) => LidarrArtistAsync(null, name, ct);

    /// <summary>
    /// The Lidarr artist with this MusicBrainz id in any Lidarr, and which Lidarr has them; without an id, the one with
    /// this name. An id that no Lidarr has finds nothing: a same-named artist is someone else.
    /// </summary>
    public async Task<(ArrInstance Lidarr, LidarrArtist Artist)?> LidarrArtistAsync(string? musicBrainzId, string name, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        foreach (var lidarr in Lidarrs)
        {
            var artists = await MusicMatcher.LidarrArtistsAsync(client, lidarr, cache, ct);
            var found = musicBrainzId is not null
                ? artists.FirstOrDefault(a => a.ForeignArtistId == musicBrainzId)
                : artists.FirstOrDefault(a => MusicNames.Normalise(a.ArtistName) == key);
            if (found is not null)
            {
                return (lidarr, found);
            }
        }

        return null;
    }

    /// <summary>Whether Lidarr follows this artist, counting only artists in libraries the profile can see.</summary>
    public bool IsFollowed(OwnedMusic owned, (ArrInstance Lidarr, LidarrArtist Artist)? artist) =>
        artist is { Artist: var a } && MusicMatcher.IsFollowed(a) && owned.Sees(MusicMatcher.LocalPath(a, options.CurrentValue.PathMappings));

    /// <summary>
    /// An artist's albums as a Lidarr lists them, kept a minute (an artist page and its album pages ask in turn); null
    /// when Lidarr doesn't answer in time.
    /// </summary>
    public async Task<IReadOnlyList<LidarrAlbum>?> LidarrAlbumsAsync(ArrInstance lidarr, int artistId, CancellationToken ct)
    {
        var key = $"lidarr-albums:{lidarr.BaseUrl}:{artistId}";
        if (cache.TryGetValue(key, out IReadOnlyList<LidarrAlbum>? hit) && hit is not null)
        {
            return hit;
        }

        try
        {
            using var quick = CancellationTokenSource.CreateLinkedTokenSource(ct);
            quick.CancelAfter(TimeSpan.FromSeconds(10));
            var albums = await client.GetArtistAlbumsAsync(lidarr, artistId, quick.Token);
            cache.Set(key, albums, TimeSpan.FromMinutes(1));
            return albums;
        }
        catch (Exception ex) when (ex is HttpRequestException || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>A request changed what this Lidarr artist's albums are: forget the list.</summary>
    public void ForgetAlbums(ArrInstance lidarr, int artistId) => cache.Remove($"lidarr-albums:{lidarr.BaseUrl}:{artistId}");

    /// <summary>
    /// Notes which of a Lidarr artist's albums are monitored, so they read as Requested; not for an artist in a
    /// library the profile can't see.
    /// </summary>
    public async Task NoteMonitoredAsync(OwnedMusic owned, (ArrInstance Lidarr, LidarrArtist Artist) found, CancellationToken ct)
    {
        if (!owned.Sees(MusicMatcher.LocalPath(found.Artist, options.CurrentValue.PathMappings)))
        {
            return;
        }

        foreach (var album in await LidarrAlbumsAsync(found.Lidarr, found.Artist.Id, ct) ?? [])
        {
            if (album.Monitored)
            {
                owned.MonitoredAlbums.Add(OwnedMusic.AlbumKey(found.Artist.ArtistName, album.Title));
            }
        }
    }

    /// <summary>Records who asked, so they hear when it's in; once per profile for what's still awaited.</summary>
    public async Task RememberAsync(MediaRequest request, CancellationToken ct)
    {
        // Asked again (after Requested lapsed, say): the same request, counted from now.
        if (await db.MediaRequests.FirstOrDefaultAsync(r => r.ProfileId == request.ProfileId && r.Kind == request.Kind && r.MusicBrainzId == request.MusicBrainzId
                && r.FulfilledAt == null, ct) is { } open)
        {
            open.RequestedAt = DateTime.UtcNow;
        }
        else
        {
            db.MediaRequests.Add(request);
        }

        await db.SaveChangesAsync(ct);
    }
}
