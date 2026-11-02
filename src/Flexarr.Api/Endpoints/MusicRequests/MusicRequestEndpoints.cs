using Flexarr.Api.Configuration;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Discovering and requesting music: Deezer for what there is, Lidarr (by MusicBrainz id) for adding it. All under
/// /requests/music, so profiles with an age limit never reach it. See docs/music-requests.md.
/// </summary>
public static class MusicRequestEndpoints
{
    private const int RowSize = 20;

    /// <param name="api">The API root, for the status every profile may ask.</param>
    /// <param name="requests">The /requests group, which turns away profiles with an age limit.</param>
    public static void MapMusicRequestEndpoints(this RouteGroupBuilder api, RouteGroupBuilder requests)
    {
        // Whether music requests are on, so apps can hide them otherwise. To a profile with an age limit, or one that
        // sees no music library, they aren't.
        api.MapGet("/requests/music/status", async (IOptionsMonitor<FlexarrOptions> options, ArrClient client, FlexarrDbContext db, CancellationToken ct) =>
        {
            var lidarrs = Lidarrs(options.CurrentValue);
            if (db.IsLimited() || lidarrs.Count == 0 || (await LibraryVisibility.HiddenTypesAsync(db, ct)).Hides(MediaKind.Album))
            {
                return new MusicRequestStatusDto(false, false);
            }

            return new MusicRequestStatusDto(true, await client.PingAsync(lidarrs[0].BaseUrl, ct));
        }).WithTags("Requests");

        var music = requests.MapGroup("/music").AddEndpointFilter(async (context, next) =>
        {
            var services = context.HttpContext.RequestServices;
            var db = services.GetRequiredService<FlexarrDbContext>();
            if (Lidarrs(services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue).Count == 0)
            {
                return Results.Problem("Music requests need Lidarr. Add one under Settings › Connections.", statusCode: 409);
            }

            return (await LibraryVisibility.HiddenTypesAsync(db, context.HttpContext.RequestAborted)).Hides(MediaKind.Album)
                ? Results.Problem("This profile can't request music.", statusCode: 403)
                : await next(context);
        });

        music.MapGet("/discover", async (MusicRequestContext c, CurrentProfile current, CancellationToken ct) =>
            await DeezerAsync(async () =>
            {
                var owned = await c.OwnedAsync(ct);
                var rows = new List<MusicRowDto>
                {
                    new("music-like", "Like artists you play", "artists", await LikeYoursAsync(c, owned, current.Id, ct)),
                    new("music-new", "New from your artists", "albums", await NewFromYoursAsync(c, owned, current.Id, ct)),
                    new("music-charts", "Top albums", "albums", (await c.Catalog.ChartAlbumsAsync(ct)).Select(a => Album(a, owned)).ToList()),
                };
                var genres = (await c.Catalog.GenresAsync(ct)).Select(g => new MusicGenreDto(g.Id, g.Name, ImageEndpoints.RemoteUrl(g.Picture))).ToList();
                return Results.Ok(new MusicDiscoverDto(rows.Where(r => r.Items.Count > 0).ToList(), genres));
            }));

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
                var artist = await c.Catalog.ArtistAsync(id, ct);
                var owned = await c.OwnedAsync(ct);
                var lidarrArtist = await c.LidarrArtistNamedAsync(artist.Name, ct);
                if (lidarrArtist is { } had)
                {
                    await c.NoteMonitoredAsync(owned, had, ct);
                }

                var albums = (await c.Catalog.ArtistAlbumsAsync(id, ct)).Select(a => Album(a with { Artist = artist }, owned)).ToList();
                var similar = (await c.Catalog.RelatedAsync(id, ct)).Take(RowSize).Select(a => Artist(a, owned)).ToList();
                return Results.Ok(new MusicArtistDetailDto(artist.Id, artist.Name, ImageEndpoints.RemoteUrl(artist.Picture), artist.NbFan, albums, similar,
                    lidarrArtist is { } a && MusicMatcher.IsFollowed(a), owned.ArtistAvailability(artist.Name), owned.ArtistId(artist.Name)));
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
                    album.Genres?.Data.Select(g => g.Name).ToList() ?? [], tracks, album.Duration, owned.AlbumAvailability(artistName, album.Title), owned.AlbumId(artistName, album.Title)));
            }));

        // A library artist's page: the albums of theirs the library doesn't have, and the artists like them.
        music.MapGet("/library-artist/{id:guid}", async (Guid id, MusicRequestContext c, CancellationToken ct) =>
        {
            var item = await c.Db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.Kind == MediaKind.Artist).Select(i => new { i.Title, i.MusicBrainzId }).FirstOrDefaultAsync(ct);
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
                if (lidarrArtist is { Lidarr: var lidarr, Artist: var artist })
                {
                    // Lidarr's list, as its metadata profile shapes it (usually albums alone), with Deezer's covers where it has them.
                    var byTitle = deezerAlbums.GroupBy(a => MusicNames.NormaliseTitle(a.Title)).ToDictionary(g => g.Key, g => g.First());
                    foreach (var album in (await SafeAsync(() => c.Client.GetArtistAlbumsAsync(lidarr, artist.Id, ct)) ?? []).OrderByDescending(a => a.ReleaseDate))
                    {
                        if (album.Statistics?.TrackFileCount > 0 || owned.AlbumId(item.Title, album.Title) is not null)
                        {
                            continue;
                        }

                        var onDeezer = byTitle.GetValueOrDefault(MusicNames.NormaliseTitle(album.Title));
                        missing.Add(new MusicExternalDto("album", onDeezer?.Id ?? 0, album.Title, item.Title, deezer?.Id, album.ReleaseDate?.Year ?? onDeezer?.Year,
                            ImageEndpoints.RemoteUrl(onDeezer?.Cover), album.Monitored ? AvailabilityDto.Requested : owned.AlbumAvailability(item.Title, album.Title), null,
                            onDeezer?.RecordType ?? album.AlbumType?.ToLowerInvariant(), album.ForeignAlbumId));
                    }
                }
                else
                {
                    missing.AddRange(deezerAlbums.Where(a => a.RecordType == "album" && owned.AlbumId(item.Title, a.Title) is null)
                        .Select(a => Album(a with { Artist = deezer }, owned)));
                }

                var similar = deezer is null ? [] : (await c.Catalog.RelatedAsync(deezer.Id, ct)).Take(RowSize).Select(a => Artist(a, owned)).ToList();
                return Results.Ok(new MusicArtistExtrasDto(missing, similar, lidarrArtist is { Artist: var a } && MusicMatcher.IsFollowed(a), deezer?.Id));
            });
        });

        // The music libraries this profile's requests could go to (every one, for the admin choosing for others).
        music.MapGet("/destinations", async (MusicRequestContext c, CurrentProfile current, CancellationToken ct) =>
        {
            var libraries = current.IsAdmin ? c.Db.Libraries.IgnoreQueryFilters() : c.Db.Libraries;
            var destinations = await MusicDestinations.ListAsync(c.Client, c.Lidarrs, c.Options.PathMappings, libraries, ct);
            return Results.Ok(destinations.Select(d => new MusicDestinationDto(d.LibraryId, d.Name, d.IsDefault)).ToList());
        });

        music.MapPost("/", async (MusicRequestBody body, MusicRequestContext c, CurrentProfile current, CancellationToken ct) =>
        {
            if (!c.Options.MusicRequests.Enabled)
            {
                return Results.Problem("Music requests are off. An admin can turn them on under Settings › Connections.", statusCode: 409);
            }

            if (body.Kind is not ("album" or "artist") || (body.DeezerId is null && body.MusicBrainzId is null && body.ArtistId is null))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["kind"] = ["Use \"album\" or \"artist\", with a deezerId, musicBrainzId or artistId."] });
            }

            try
            {
                return await MusicRequestPlacer.PlaceAsync(c, current.Id, body, ct);
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem($"Lidarr didn't take the request: {ex.Message}", statusCode: 502);
            }
            catch (DeezerException ex)
            {
                return Results.Problem(ex.Message, statusCode: 502);
            }
        });
    }

    internal static List<ArrInstance> Lidarrs(FlexarrOptions options) =>
        options.MusicRequests.Enabled ? options.Arr.Where(a => a.Kind == ArrKind.Lidarr).ToList() : [];

    internal static MusicExternalDto Artist(DeezerArtist artist, OwnedMusic owned) =>
        new("artist", artist.Id, artist.Name, null, artist.Id, null, ImageEndpoints.RemoteUrl(artist.Picture), owned.ArtistAvailability(artist.Name), owned.ArtistId(artist.Name));

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

    internal static async Task<T?> SafeAsync<T>(Func<Task<T>> call) where T : class
    {
        try
        {
            return await call();
        }
        catch (HttpRequestException)
        {
            return null;
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
    private static async Task<List<MusicExternalDto>> LikeYoursAsync(MusicRequestContext c, OwnedMusic owned, string profileId, CancellationToken ct)
    {
        var seeds = await TopArtistsAsync(c.Db, profileId, 8, ct);
        var related = await Task.WhenAll(seeds.Select(async name =>
            await c.Catalog.ArtistNamedAsync(name, ct) is { } seed ? await c.Catalog.RelatedAsync(seed.Id, ct) : []));
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
    private static async Task<List<MusicExternalDto>> NewFromYoursAsync(MusicRequestContext c, OwnedMusic owned, string profileId, CancellationToken ct)
    {
        // The artists played most first, then any others, so a big library doesn't ask Deezer about every artist.
        var names = (await TopArtistsAsync(c.Db, profileId, 12, ct)).Concat(owned.Artists.Count <= 12
            ? await c.Db.MediaItems.AsNoTracking().Where(i => i.Kind == MediaKind.Artist).OrderByDescending(i => i.AddedAt).Select(i => i.Title).Take(12).ToListAsync(ct)
            : []).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToList();
        var since = DateOnly.FromDateTime(DateTime.UtcNow.AddYears(-1)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var albums = await Task.WhenAll(names.Select(async name =>
        {
            if (await c.Catalog.ArtistNamedAsync(name, ct) is not { } artist)
            {
                return Enumerable.Empty<DeezerAlbum>();
            }

            return (await c.Catalog.ArtistAlbumsAsync(artist.Id, ct)).Where(a => string.CompareOrdinal(a.ReleaseDate, since) >= 0 && a.RecordType is "album" or "ep")
                .Select(a => a with { Artist = artist });
        }));
        return albums.SelectMany(a => a).Where(a => owned.AlbumId(a.Artist?.Name, a.Title) is null)
            .DistinctBy(a => OwnedMusic.AlbumKey(a.Artist?.Name, a.Title))
            .OrderByDescending(a => a.ReleaseDate, StringComparer.Ordinal).Take(RowSize).Select(a => Album(a, owned)).ToList();
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

    public Task<OwnedMusic> OwnedAsync(CancellationToken ct) => MusicMatcher.OwnedAsync(db, client, Lidarrs, cache, ct);

    /// <summary>The artist of this name in one of the Lidarrs (from their cached lists), or null.</summary>
    public async Task<LidarrArtist?> LidarrArtistNamedAsync(string name, CancellationToken ct) => (await LidarrArtistAsync(null, name, ct))?.Artist;

    /// <summary>The Lidarr artist with this MusicBrainz id, or failing that this name, and which Lidarr has them.</summary>
    public async Task<(ArrInstance Lidarr, LidarrArtist Artist)?> LidarrArtistAsync(string? musicBrainzId, string name, CancellationToken ct)
    {
        var key = MusicNames.Normalise(name);
        foreach (var lidarr in Lidarrs)
        {
            var artists = await MusicMatcher.LidarrArtistsAsync(client, lidarr, cache, ct);
            var found = (musicBrainzId is not null ? artists.FirstOrDefault(a => a.ForeignArtistId == musicBrainzId) : null)
                ?? artists.FirstOrDefault(a => MusicNames.Normalise(a.ArtistName) == key);
            if (found is not null)
            {
                return (lidarr, found);
            }
        }

        return null;
    }

    /// <summary>Notes which of a Lidarr artist's albums are monitored, so they read as Requested.</summary>
    public async Task NoteMonitoredAsync(OwnedMusic owned, LidarrArtist artist, CancellationToken ct)
    {
        if (await LidarrArtistAsync(artist.ForeignArtistId, artist.ArtistName, ct) is not { Lidarr: var lidarr })
        {
            return;
        }

        foreach (var album in await MusicRequestEndpoints.SafeAsync(() => client.GetArtistAlbumsAsync(lidarr, artist.Id, ct)) ?? [])
        {
            if (album.Monitored)
            {
                owned.MonitoredAlbums.Add(OwnedMusic.AlbumKey(artist.ArtistName, album.Title));
            }
        }
    }

    /// <summary>Records who asked, so they hear when it's in; once per profile for what's still awaited.</summary>
    public async Task RememberAsync(MediaRequest request, CancellationToken ct)
    {
        if (!await db.MediaRequests.AnyAsync(r => r.ProfileId == request.ProfileId && r.Kind == request.Kind && r.MusicBrainzId == request.MusicBrainzId && r.FulfilledAt == null, ct))
        {
            db.MediaRequests.Add(request);
            await db.SaveChangesAsync(ct);
        }
    }
}
