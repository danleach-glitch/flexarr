using System.Globalization;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Metadata;

public sealed record MetadataRefreshResult(int FromArr, int FromLookup, int Unmatched);

/// <summary>
/// Fills in titles, artwork, overviews, genres, ratings, trailers and cast for a library.
/// Radarr and Sonarr are the primary source (they already hold metadata for everything they manage, with no
/// API keys needed); items they don't manage are looked up through their search endpoints; TV cast comes from TVmaze.
/// </summary>
public sealed partial class MetadataRefresher(
    FlexarrDbContext db,
    ArrClient arr,
    TvMazeClient tvmaze,
    ImageCache images,
    IMetadataSettings settings,
    ILogger<MetadataRefresher> logger)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryUnmatchedAfter = TimeSpan.FromDays(1);
    private const int MaxCast = 24;

    /// <summary>The source of a film's or show's details when they came from searching for its folder's name.</summary>
    public const string RadarrLookup = "Radarr lookup", SonarrLookup = "Sonarr lookup";

    /// <summary>The source of an artist's or album's details when nothing but its files' tags supplied them.</summary>
    public const string Tags = "Tags";

    private Dictionary<string, Genre>? _genres;
    private Dictionary<string, Person>? _peopleByTmdb;
    private Dictionary<string, Person>? _peopleByTvMaze;

    public Task<MetadataRefreshResult> RefreshLibraryAsync(Library library, bool force, CancellationToken ct) =>
        RefreshLibraryAsync(library, force, [], ct);

    public Task<MetadataRefreshResult> RefreshLibraryAsync(Library library, bool force, IReadOnlyCollection<string> within, CancellationToken ct) =>
        RefreshLibraryAsync(library, force, within, null, null, ct);

    /// <param name="library">The library to refresh.</param>
    /// <param name="force">Refresh everything, not just what's new or stale.</param>
    /// <param name="within">Folders just scanned: the films and series in them are refreshed even if they're not stale.</param>
    /// <param name="tmdbId">
    /// The TMDB id Radarr's webhook gave for the film in <paramref name="within"/>, for asking Radarr for just that one
    /// when Flexarr doesn't know its id yet. Only used to fetch: what comes back must still match the film's folder.
    /// </param>
    /// <param name="tvdbId">The same for a show, with the TVDB id from Sonarr's webhook.</param>
    /// <param name="ct">Stops the refresh.</param>
    public async Task<MetadataRefreshResult> RefreshLibraryAsync(Library library, bool force, IReadOnlyCollection<string> within, int? tmdbId, int? tvdbId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(within);
        return library.Type switch
        {
            LibraryType.Movies => await RefreshMoviesAsync(library.Id, force, within, tmdbId, ct).ConfigureAwait(false),
            LibraryType.TvShows => await RefreshSeriesAsync(library.Id, force, within, tvdbId, ct).ConfigureAwait(false),
            LibraryType.Music => await RefreshMusicAsync(library.Id, force, within, ct).ConfigureAwait(false),
            _ => new MetadataRefreshResult(0, 0, 0),
        };
    }

    /// <summary>
    /// Refreshes one film or show now (e.g. after Fix match), cast included. The caller loads it with its genres and
    /// credits, and saves.
    /// </summary>
    /// <returns>true when matched in Radarr or Sonarr, false when found by lookup, null when unmatched.</returns>
    public async Task<bool?> RefreshItemAsync(MediaItem item, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        // A match fixed by hand is fetched by its id alone; otherwise the folder decides, which needs the whole list.
        IReadOnlyList<MediaItem>? only = item.MatchLocked ? [item] : null;
        bool? result;
        if (item.Kind == MediaKind.Movie)
        {
            var radarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Radarr).ToList();
            var known = (await KnownAsync(radarrs, only, i => i.TmdbId, arr.GetMoviesAsync, arr.GetMovieAsync, arr.GetMoviesByTmdbIdAsync, ct).ConfigureAwait(false))
                .Select(k => (k.Instance, Movie: k.Entry)).ToList();
            result = await RefreshMovieAsync(item, Index(known, k => MapPath(k.Movie.Path)), Index(known, k => k.Movie.ImdbId),
                Index(known, k => k.Movie.TmdbId > 0 ? k.Movie.TmdbId.ToString(CultureInfo.InvariantCulture) : null), radarrs, force: true, ct).ConfigureAwait(false);
        }
        else if (item.Kind == MediaKind.Series)
        {
            var sonarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Sonarr).ToList();
            var known = (await KnownAsync(sonarrs, only, i => i.TvdbId, arr.GetSeriesAsync, arr.GetOneSeriesAsync, arr.GetSeriesByTvdbIdAsync, ct).ConfigureAwait(false))
                .Select(k => (k.Instance, Series: k.Entry)).ToList();
            result = await RefreshOneSeriesAsync(item, Index(known, k => MapPath(k.Series.Path)),
                Index(known, k => k.Series.TvdbId > 0 ? k.Series.TvdbId.ToString(CultureInfo.InvariantCulture) : null), Index(known, k => k.Series.ImdbId),
                sonarrs, force: true, ct).ConfigureAwait(false);
        }
        else
        {
            throw new ArgumentException($"Only films and shows are matched, not {item.Kind}.", nameof(item));
        }

        item.MetadataUpdatedAt = DateTime.UtcNow;
        return result;
    }

    // ------------------------------------------------------------------ Movies

    private async Task<MetadataRefreshResult> RefreshMoviesAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, int? tmdbId, CancellationToken ct)
    {
        // Nothing due: don't download Radarr's whole library to find out.
        var also = await WithinAsync(libraryId, MediaKind.Movie, within, ct).ConfigureAwait(false);
        var items = await Stale(libraryId, MediaKind.Movie, force, also).Include(i => i.Genres).Include(i => i.Credits).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var radarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Radarr).ToList();
        var onlyWithin = !force && items.All(i => also.Contains(i.Id)); // Just the webhook's: ask for just those.
        var known = (await KnownAsync(radarrs, onlyWithin ? items : null, Hinted(also, tmdbId, i => i.TmdbId),
                arr.GetMoviesAsync, arr.GetMovieAsync, arr.GetMoviesByTmdbIdAsync, ct).ConfigureAwait(false))
            .Select(k => (k.Instance, Movie: k.Entry)).ToList();

        var byPath = Index(known, k => MapPath(k.Movie.Path));
        var byImdb = Index(known, k => k.Movie.ImdbId);
        var byTmdb = Index(known, k => k.Movie.TmdbId > 0 ? k.Movie.TmdbId.ToString(CultureInfo.InvariantCulture) : null);
        int fromArr = 0, fromLookup = 0, unmatched = 0;

        foreach (var item in items)
        {
            try
            {
                switch (await RefreshMovieAsync(item, byPath, byImdb, byTmdb, radarrs, force, ct).ConfigureAwait(false))
                {
                    case true: fromArr++; break;
                    case false: fromLookup++; break;
                    default: unmatched++; break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, item.Title);
                unmatched++;
            }

            item.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + fromLookup + unmatched, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, fromLookup, unmatched);
    }

    /// <returns>true when matched in Radarr, false when found by lookup, null when unmatched.</returns>
    private async Task<bool?> RefreshMovieAsync(
        MediaItem item,
        Dictionary<string, (ArrInstance, RadarrMovie)> byPath,
        Dictionary<string, (ArrInstance, RadarrMovie)> byImdb,
        Dictionary<string, (ArrInstance, RadarrMovie)> byTmdb,
        List<ArrInstance> radarrs,
        bool force,
        CancellationToken ct)
    {
        // A match fixed by hand is found by its TMDB id only: the folder may be one Radarr has as a different film.
        var hit = item.MatchLocked
            ? Find(byTmdb, item.TmdbId)
            : Find(byPath, item.Path) ?? Find(byImdb, item.ImdbId) ?? Find(byTmdb, item.TmdbId);
        if (hit is (var instance, var movie))
        {
            await ApplyMovieAsync(item, movie, instance, ct).ConfigureAwait(false);
            if (force || item.Credits.Count == 0)
            {
                await ApplyMovieCreditsAsync(item, instance, movie.Id, ct).ConfigureAwait(false);
            }

            return true;
        }

        if (radarrs.Count > 0 && await LookupMovieAsync(radarrs[0], item, ct).ConfigureAwait(false) is { } found)
        {
            await ApplyMovieAsync(item, found, null, ct).ConfigureAwait(false);
            return false;
        }

        return null;
    }

    private async Task<RadarrMovie?> LookupMovieAsync(ArrInstance radarr, MediaItem item, CancellationToken ct)
    {
        var term = item.MatchLocked && item.TmdbId is { } locked ? $"tmdb:{locked}"
            : item.ImdbId is { } imdb ? $"imdb:{imdb}"
            : item.TmdbId is { } tmdb ? $"tmdb:{tmdb}"
            : $"{item.Title} {item.Year}".Trim();
        try
        {
            var results = await arr.LookupMoviesAsync(radarr, term, ct).ConfigureAwait(false);
            return item.MatchLocked ? (results.Count > 0 ? results[0] : null) : BestByTitle(results, item, m => m.Title, m => m.Year);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            LogLookupFailed(term, ex.Message);
            return null;
        }
    }

    private async Task ApplyMovieAsync(MediaItem item, RadarrMovie movie, ArrInstance? instance, CancellationToken ct)
    {
        item.Title = movie.Title;
        item.SortTitle = SortTitle.For(movie.Title);
        item.OriginalTitle = movie.OriginalTitle != movie.Title ? movie.OriginalTitle : null;
        item.Year = movie.Year > 0 ? movie.Year : item.Year;
        item.Overview = NullIfEmpty(movie.Overview);
        item.ContentRating = NullIfEmpty(movie.Certification);
        item.Studio = NullIfEmpty(movie.Studio);
        item.RuntimeMinutes = movie.Runtime > 0 ? movie.Runtime : null;
        item.Status = movie.Status;
        item.ReleaseDate = DateOnlyOf(movie.InCinemas ?? movie.DigitalRelease ?? movie.PhysicalRelease ?? movie.ReleaseDate);
        item.CommunityRating = movie.Ratings?.Imdb?.Value is > 0 ? movie.Ratings.Imdb.Value : movie.Ratings?.Tmdb?.Value is > 0 ? movie.Ratings.Tmdb.Value : null;
        item.CriticRating = movie.Ratings?.RottenTomatoes?.Value is > 0 and var rt ? (int)Math.Round(rt) : null;
        item.Popularity = movie.Popularity;
        item.TrailerYouTubeId = NullIfEmpty(movie.YouTubeTrailerId);
        item.ImdbId = NullIfEmpty(movie.ImdbId) ?? item.ImdbId;
        item.TmdbId = movie.TmdbId > 0 ? movie.TmdbId.ToString(CultureInfo.InvariantCulture) : item.TmdbId;
        item.ArrId = instance is null ? null : movie.Id;
        item.MetadataSource = instance?.Name ?? RadarrLookup;
        await ApplyImagesAsync(item, movie.Images, movie.RemotePoster, ct).ConfigureAwait(false);
        await SetGenresAsync(item, movie.Genres, ct).ConfigureAwait(false);
    }

    private async Task ApplyMovieCreditsAsync(MediaItem item, ArrInstance radarr, int movieId, CancellationToken ct)
    {
        IReadOnlyList<RadarrCredit> credits;
        try
        {
            credits = await arr.GetCreditsAsync(radarr, movieId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            LogArrUnavailable(radarr.Name, ex.Message);
            return;
        }

        var wanted = credits.Where(c => c.Type == "cast").OrderBy(c => c.Order).Take(MaxCast)
            .Select(c => (Credit: c, Role: CreditRole.Actor))
            .Concat(credits.Where(c => c.Type == "crew").Select(c => (Credit: c, Role: c.Job switch
            {
                "Director" => CreditRole.Director,
                "Screenplay" or "Writer" => CreditRole.Writer,
                _ => (CreditRole?)null,
            })).Where(c => c.Role is not null).Select(c => (c.Credit, Role: c.Role!.Value)))
            .DistinctBy(c => (c.Credit.PersonTmdbId, c.Role))
            .ToList();

        var people = await PeopleAsync(tmdb: true, ct).ConfigureAwait(false);

        db.Credits.RemoveRange(item.Credits);
        item.Credits.Clear();
        var order = 0;
        foreach (var (credit, role) in wanted)
        {
            var tmdbId = credit.PersonTmdbId.ToString(CultureInfo.InvariantCulture);
            if (!people.TryGetValue(tmdbId, out var person))
            {
                person = new Person { Name = credit.PersonName, TmdbId = tmdbId };
                db.People.Add(person);
                people[tmdbId] = person;
            }

            person.ImageUrl = credit.Images.FirstOrDefault(i => i.CoverType == "headshot")?.RemoteUrl ?? person.ImageUrl;
            item.Credits.Add(new Credit { PersonId = person.Id, Role = role, Character = NullIfEmpty(credit.Character), Order = order++ });
        }
    }

    // ------------------------------------------------------------------ Music

    /// <summary>
    /// The scanner builds artists and albums from tags and whatever artwork sits in their folders. Lidarr, when
    /// connected, knows better: the canonical names and years, and artwork for every artist and album. Without it, the
    /// tags stand and this only adds ambient colours.
    /// </summary>
    private async Task<MetadataRefreshResult> RefreshMusicAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var lidarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Lidarr).ToList();
        if (lidarrs.Count == 0)
        {
            return await RefreshMusicFromTagsAsync(libraryId, force, ct).ConfigureAwait(false);
        }

        // Albums in the folders just scanned (a Lidarr import) make their artist due, so new albums get their cover
        // straight away.
        var albumsWithin = await WithinAsync(libraryId, MediaKind.Album, within, ct).ConfigureAwait(false);
        var also = albumsWithin.Count == 0 ? []
            : await db.MediaItems.Where(i => albumsWithin.Contains(i.Id) && i.ParentId != null).Select(i => i.ParentId!.Value).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var artists = await Stale(libraryId, MediaKind.Artist, force, also).Include(i => i.Genres).ToListAsync(ct).ConfigureAwait(false);
        if (artists.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        // Lidarr's whole artist list once (it's a few KB an artist, and there are rarely thousands), rather than a
        // search per artist; albums and files are asked for per matched artist.
        var known = new List<(ArrInstance Instance, LidarrArtist Artist)>();
        var answered = false;
        foreach (var lidarr in lidarrs)
        {
            try
            {
                known.AddRange((await arr.GetArtistsAsync(lidarr, ct).ConfigureAwait(false)).Select(a => (lidarr, a)));
                answered = true;
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                LogArrUnavailable(lidarr.Name, ex.Message);
            }
        }

        int fromArr = 0, fromTags = 0, unmatched = 0;
        foreach (var artist in artists)
        {
            var albums = await db.MediaItems.Where(i => i.ParentId == artist.Id && i.Kind == MediaKind.Album).Include(i => i.Genres).ToListAsync(ct).ConfigureAwait(false);
            try
            {
                var hit = lidarrs
                    .Select(l => (Instance: l, Artist: LidarrMatcher.MatchArtist(artist, albums.Select(a => a.Path), known.Where(k => k.Instance == l).Select(k => k.Artist).ToList(), settings.PathMappings)))
                    .FirstOrDefault(h => h.Artist is not null);
                if (hit.Artist is { } lidarrArtist)
                {
                    await ApplyArtistAsync(artist, albums, lidarrArtist, hit.Instance, ct).ConfigureAwait(false);
                    fromArr++;
                }
                else
                {
                    await ApplyTagsAsync(artist, albums, ct).ConfigureAwait(false);
                    if (answered)
                    {
                        unmatched++;
                    }
                    else
                    {
                        fromTags++; // Lidarr couldn't be asked: not this artist's fault, and it's retried as stale anyway.
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, artist.Title);
                unmatched++;
            }

            artist.MetadataUpdatedAt = DateTime.UtcNow;
            foreach (var album in albums)
            {
                album.MetadataUpdatedAt = DateTime.UtcNow;
            }

            await SaveEveryAsync(fromArr + fromTags + unmatched, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, fromTags, unmatched);
    }

    /// <summary>No Lidarr: the tags stand, and the artwork the scanner found gets its ambient colour.</summary>
    private async Task<MetadataRefreshResult> RefreshMusicFromTagsAsync(Guid libraryId, bool force, CancellationToken ct)
    {
        var items = await db.MediaItems
            .Where(i => i.LibraryId == libraryId && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist) && i.PosterUrl != null && (force || i.AccentColor == null))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            item.AccentColor = await images.AccentColorAsync(item.PosterUrl!, ct).ConfigureAwait(false);
            item.MetadataUpdatedAt = DateTime.UtcNow;
            item.MetadataSource = Tags;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(0, items.Count, 0);
    }

    private async Task ApplyArtistAsync(MediaItem artist, List<MediaItem> albums, LidarrArtist lidarrArtist, ArrInstance lidarr, CancellationToken ct)
    {
        IReadOnlyList<LidarrAlbum>? lidarrAlbums = null;
        IReadOnlyList<LidarrTrackFile> files = [];
        try
        {
            lidarrAlbums = await arr.GetArtistAlbumsAsync(lidarr, lidarrArtist.Id, ct).ConfigureAwait(false);
            files = await arr.GetTrackFilesAsync(lidarr, lidarrArtist.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            // The artist is still worth naming; its albums are left as they are (not handed back to their tags, which
            // would strip covers over a hiccup) and wait for the next refresh.
            LogArrUnavailable(lidarr.Name, ex.Message);
        }

        Rename(artist, lidarrArtist.ArtistName);
        artist.MusicBrainzId = NullIfEmpty(lidarrArtist.ForeignArtistId) ?? artist.MusicBrainzId;
        artist.Overview = NullIfEmpty(lidarrArtist.Overview);
        artist.Status = NullIfEmpty(lidarrArtist.Status);
        artist.CommunityRating = lidarrArtist.Ratings?.Value is > 0 ? lidarrArtist.Ratings.Value : null;
        artist.ArrId = lidarrArtist.Id;
        artist.MetadataSource = lidarr.Name;
        await SetGenresAsync(artist, lidarrArtist.Genres, ct).ConfigureAwait(false);

        // Lidarr's artwork over whatever the scanner found: a real artist photo rather than an album's cover, a cover
        // for albums whose files have none embedded. What the scanner found stays as the fallback.
        var poster = ArrImage(lidarr, lidarrArtist.Images, "poster", $"mediacover/artist/{lidarrArtist.Id}/");
        var backdrop = ArrImage(lidarr, lidarrArtist.Images, "fanart", $"mediacover/artist/{lidarrArtist.Id}/");
        var changed = poster is not null && poster != artist.PosterUrl;
        artist.PosterUrl = poster ?? (ArtworkUrl.IsArr(artist.PosterUrl) ? null : artist.PosterUrl);
        artist.BackdropUrl = backdrop;
        artist.LogoUrl = ArrImage(lidarr, lidarrArtist.Images, "clearlogo", $"mediacover/artist/{lidarrArtist.Id}/");

        foreach (var album in albums)
        {
            if (lidarrAlbums is not null)
            {
                if (LidarrMatcher.MatchAlbum(album, lidarrAlbums, files, settings.PathMappings) is { } lidarrAlbum)
                {
                    await ApplyAlbumAsync(album, lidarrAlbum, lidarr, ct).ConfigureAwait(false);
                }
                else
                {
                    await ApplyTagsAsync(album, ct).ConfigureAwait(false);
                }
            }

            album.BackdropUrl ??= artist.BackdropUrl;
        }

        // The artist's cover is their first album's when they have no photo of their own, as the scanner does.
        artist.PosterUrl ??= albums.OrderBy(a => a.Year ?? int.MaxValue).ThenBy(a => a.SortTitle).Select(a => a.PosterUrl).FirstOrDefault(p => p is not null);
        if ((changed || artist.AccentColor is null) && artist.PosterUrl is { } source)
        {
            artist.AccentColor = await images.AccentColorAsync(source, ct).ConfigureAwait(false) ?? artist.AccentColor;
        }
    }

    private async Task ApplyAlbumAsync(MediaItem album, LidarrAlbum lidarrAlbum, ArrInstance lidarr, CancellationToken ct)
    {
        Rename(album, lidarrAlbum.Title);
        album.Year = lidarrAlbum.ReleaseDate?.Year ?? album.Year;
        album.ReleaseDate = DateOnlyOf(lidarrAlbum.ReleaseDate);
        album.MusicBrainzId = NullIfEmpty(lidarrAlbum.ForeignAlbumId) ?? album.MusicBrainzId;
        album.AlbumType = AlbumKind.From(lidarrAlbum.AlbumType, lidarrAlbum.SecondaryTypes);
        album.Overview = NullIfEmpty(lidarrAlbum.Overview);
        album.RuntimeMinutes = lidarrAlbum.Duration > 0 ? (int)Math.Max(1, Math.Round(lidarrAlbum.Duration / 60_000.0)) : null;
        album.CommunityRating = lidarrAlbum.Ratings?.Value is > 0 ? lidarrAlbum.Ratings.Value : null;
        album.ArrId = lidarrAlbum.Id;
        album.MetadataSource = lidarr.Name;
        await SetGenresAsync(album, lidarrAlbum.Genres, ct).ConfigureAwait(false);

        var cover = ArrImage(lidarr, lidarrAlbum.Images, "cover", $"mediacover/album/{lidarrAlbum.Id}/");
        var changed = cover is not null && cover != album.PosterUrl;
        album.PosterUrl = cover ?? (ArtworkUrl.IsArr(album.PosterUrl) ? null : album.PosterUrl);
        if ((changed || album.AccentColor is null) && album.PosterUrl is { } source)
        {
            album.AccentColor = await images.AccentColorAsync(source, ct).ConfigureAwait(false) ?? album.AccentColor;
        }
    }

    /// <summary>An artist Lidarr doesn't have, and its albums: the tags stand; the scanner's artwork gets its colour.</summary>
    private async Task ApplyTagsAsync(MediaItem artist, List<MediaItem> albums, CancellationToken ct)
    {
        foreach (var album in albums)
        {
            await ApplyTagsAsync(album, ct).ConfigureAwait(false);
        }

        await ApplyTagsAsync(artist, ct).ConfigureAwait(false);
    }

    private async Task ApplyTagsAsync(MediaItem item, CancellationToken ct)
    {
        // Artwork from a Lidarr that no longer has the item (or is no longer configured) can't be fetched any more.
        if (ArtworkUrl.IsArr(item.PosterUrl))
        {
            item.PosterUrl = null;
            item.AccentColor = null;
        }

        if (ArtworkUrl.IsArr(item.BackdropUrl))
        {
            item.BackdropUrl = null;
        }

        if (ArtworkUrl.IsArr(item.LogoUrl))
        {
            item.LogoUrl = null;
        }

        if (item.MetadataSource != Tags)
        {
            // Back to the tags' name: the one Lidarr gave is only right while Lidarr vouches for it.
            if (item.OriginalTitle is { } tagged)
            {
                item.Title = tagged;
                item.SortTitle = SortTitle.For(tagged);
                item.OriginalTitle = null;
            }

            item.ArrId = null;
            item.MetadataSource = Tags;
        }

        if (item.AccentColor is null && item.PosterUrl is { } poster)
        {
            item.AccentColor = await images.AccentColorAsync(poster, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Gives an artist or album Lidarr's name, keeping the tags' spelling as the original title so the scanner still
    /// recognises its files (it groups them by the tags) and a later refresh can hand the name back.
    /// </summary>
    private static void Rename(MediaItem item, string title)
    {
        var name = NullIfEmpty(title);
        if (name is null || name == item.Title)
        {
            return;
        }

        item.OriginalTitle ??= item.Title;
        if (item.OriginalTitle == name)
        {
            item.OriginalTitle = null;
        }

        item.Title = name;
        item.SortTitle = SortTitle.For(name);
    }

    /// <summary>
    /// An arr:// link to one of Lidarr's images (see <see cref="ArtworkUrl.Arr"/>). Lidarr's <c>url</c> is a UI path
    /// (<c>/MediaCover/Albums/1/cover.jpg?lastWrite=…</c>) that needs a signed-in browser; the same file is served to
    /// the API under <paramref name="apiFolder"/>. The lastWrite stamp stays on, so a replaced image is fetched anew.
    /// </summary>
    private static string? ArrImage(ArrInstance lidarr, IReadOnlyList<ArrImage> lidarrImages, string coverType, string apiFolder)
    {
        var image = lidarrImages.FirstOrDefault(i => string.Equals(i.CoverType, coverType, StringComparison.OrdinalIgnoreCase));
        if (image?.Url is not { Length: > 0 } url)
        {
            return null;
        }

        var query = url.IndexOf('?', StringComparison.Ordinal);
        var path = query < 0 ? url : url[..query];
        var file = path[(path.LastIndexOf('/') + 1)..];
        return file.Length == 0 ? null : ArtworkUrl.Arr(lidarr.Name, apiFolder + file + (query < 0 ? string.Empty : url[query..]));
    }

    // ------------------------------------------------------------------ TV

    private async Task<MetadataRefreshResult> RefreshSeriesAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, int? tvdbId, CancellationToken ct)
    {
        var also = await WithinAsync(libraryId, MediaKind.Series, within, ct).ConfigureAwait(false);
        var items = await Stale(libraryId, MediaKind.Series, force, also).Include(i => i.Genres).Include(i => i.Credits).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var sonarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        var onlyWithin = !force && items.All(i => also.Contains(i.Id)); // Just the webhook's: ask for just those.
        var known = (await KnownAsync(sonarrs, onlyWithin ? items : null, Hinted(also, tvdbId, i => i.TvdbId),
                arr.GetSeriesAsync, arr.GetOneSeriesAsync, arr.GetSeriesByTvdbIdAsync, ct).ConfigureAwait(false))
            .Select(k => (k.Instance, Series: k.Entry)).ToList();

        var byPath = Index(known, k => MapPath(k.Series.Path));
        var byTvdb = Index(known, k => k.Series.TvdbId > 0 ? k.Series.TvdbId.ToString(CultureInfo.InvariantCulture) : null);
        var byImdb = Index(known, k => k.Series.ImdbId);
        int fromArr = 0, fromLookup = 0, unmatched = 0;

        foreach (var item in items)
        {
            try
            {
                switch (await RefreshOneSeriesAsync(item, byPath, byTvdb, byImdb, sonarrs, force, ct).ConfigureAwait(false))
                {
                    case true: fromArr++; break;
                    case false: fromLookup++; break;
                    default: unmatched++; break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, item.Title);
                unmatched++;
            }

            item.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + fromLookup + unmatched, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, fromLookup, unmatched);
    }

    /// <returns>true when matched in Sonarr, false when found by lookup, null when unmatched.</returns>
    private async Task<bool?> RefreshOneSeriesAsync(
        MediaItem item,
        Dictionary<string, (ArrInstance, SonarrSeries)> byPath,
        Dictionary<string, (ArrInstance, SonarrSeries)> byTvdb,
        Dictionary<string, (ArrInstance, SonarrSeries)> byImdb,
        List<ArrInstance> sonarrs,
        bool force,
        CancellationToken ct)
    {
        bool? result = null;
        // A match fixed by hand is found by its TVDB id only: the folder may be one Sonarr has as a different show.
        var hit = item.MatchLocked
            ? Find(byTvdb, item.TvdbId)
            : Find(byPath, item.Path) ?? Find(byTvdb, item.TvdbId) ?? Find(byImdb, item.ImdbId);
        if (hit is (var instance, var series))
        {
            await ApplySeriesAsync(item, series, instance, ct).ConfigureAwait(false);
            await ApplyEpisodesAsync(item, series, instance, ct).ConfigureAwait(false);
            result = true;
        }
        else if (sonarrs.Count > 0 && await LookupSeriesAsync(sonarrs[0], item, ct).ConfigureAwait(false) is { } found)
        {
            await ApplySeriesAsync(item, found, null, ct).ConfigureAwait(false);
            await ApplySeasonsAsync(item, found, ct).ConfigureAwait(false);
            result = false;
        }

        if (force || item.Credits.Count == 0)
        {
            await ApplySeriesCastAsync(item, ct).ConfigureAwait(false);
        }

        return result;
    }

    private async Task<SonarrSeries?> LookupSeriesAsync(ArrInstance sonarr, MediaItem item, CancellationToken ct)
    {
        var term = item.TvdbId is { } tvdb ? $"tvdb:{tvdb}" : item.Title;
        try
        {
            var results = await arr.LookupSeriesAsync(sonarr, term, ct).ConfigureAwait(false);
            return item.MatchLocked ? (results.Count > 0 ? results[0] : null) : BestByTitle(results, item, s => s.Title, s => s.Year);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            LogLookupFailed(term, ex.Message);
            return null;
        }
    }

    private async Task ApplySeriesAsync(MediaItem item, SonarrSeries series, ArrInstance? instance, CancellationToken ct)
    {
        item.Title = series.Title;
        item.SortTitle = SortTitle.For(series.Title);
        item.Year = series.Year > 0 ? series.Year : item.Year;
        item.Overview = NullIfEmpty(series.Overview);
        item.ContentRating = NullIfEmpty(series.Certification);
        item.Studio = NullIfEmpty(series.Network);
        item.RuntimeMinutes = series.Runtime > 0 ? series.Runtime : null;
        item.Status = series.Status;
        item.ReleaseDate = DateOnlyOf(series.FirstAired);
        item.CommunityRating = series.Ratings?.Value is > 0 ? series.Ratings.Value : null;
        item.TvdbId = series.TvdbId > 0 ? series.TvdbId.ToString(CultureInfo.InvariantCulture) : item.TvdbId;
        item.TmdbId = series.TmdbId > 0 ? series.TmdbId.ToString(CultureInfo.InvariantCulture) : item.TmdbId;
        item.ImdbId = NullIfEmpty(series.ImdbId) ?? item.ImdbId;
        item.TvMazeId = series.TvMazeId > 0 ? series.TvMazeId.ToString(CultureInfo.InvariantCulture) : item.TvMazeId;
        item.ArrId = instance is null ? null : series.Id;
        item.MetadataSource = instance?.Name ?? SonarrLookup;
        await ApplyImagesAsync(item, series.Images, series.RemotePoster, ct).ConfigureAwait(false);
        await SetGenresAsync(item, series.Genres, ct).ConfigureAwait(false);
    }

    private async Task<List<MediaItem>> ApplySeasonsAsync(MediaItem series, SonarrSeries arrSeries, CancellationToken ct)
    {
        var seasons = await db.MediaItems.Where(i => i.ParentId == series.Id && i.Kind == MediaKind.Season).ToListAsync(ct).ConfigureAwait(false);
        foreach (var season in seasons)
        {
            var arrSeason = arrSeries.Seasons.FirstOrDefault(s => s.SeasonNumber == season.SeasonNumber);
            season.PosterUrl = arrSeason?.Images?.FirstOrDefault(i => i.CoverType == "poster")?.RemoteUrl ?? series.PosterUrl;
            season.BackdropUrl = series.BackdropUrl;
            season.AccentColor = series.AccentColor;
            season.MetadataUpdatedAt = DateTime.UtcNow;
        }

        return seasons;
    }

    private async Task ApplyEpisodesAsync(MediaItem series, SonarrSeries arrSeries, ArrInstance sonarr, CancellationToken ct)
    {
        var seasons = await ApplySeasonsAsync(series, arrSeries, ct).ConfigureAwait(false);
        var seasonIds = seasons.Select(s => s.Id).ToList();
        var episodes = await db.MediaItems.Where(i => i.ParentId != null && seasonIds.Contains(i.ParentId.Value)).ToListAsync(ct).ConfigureAwait(false);
        if (episodes.Count == 0)
        {
            return;
        }

        IReadOnlyList<SonarrEpisode> arrEpisodes;
        try
        {
            arrEpisodes = await arr.GetEpisodesAsync(sonarr, arrSeries.Id, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            LogArrUnavailable(sonarr.Name, ex.Message);
            return;
        }

        var byNumber = arrEpisodes.GroupBy(e => (e.SeasonNumber, e.EpisodeNumber)).ToDictionary(g => g.Key, g => g.First());
        var byDate = arrEpisodes.Where(e => e.AirDate is not null).GroupBy(e => e.AirDate!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        foreach (var episode in episodes)
        {
            var match = episode.AirDate is { } date
                ? byDate.GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : byNumber.GetValueOrDefault((episode.SeasonNumber ?? 0, episode.EpisodeNumber ?? 0));
            if (match is null)
            {
                continue;
            }

            if (NullIfEmpty(match.Title) is { } title && title != "TBA")
            {
                // Multi-episode files: "Part One / Part Two".
                var end = episode.EpisodeNumberEnd is { } last
                    ? Enumerable.Range(match.EpisodeNumber + 1, Math.Max(0, last - match.EpisodeNumber))
                        .Select(n => byNumber.GetValueOrDefault((match.SeasonNumber, n))?.Title).Where(t => !string.IsNullOrWhiteSpace(t))
                    : [];
                episode.Title = string.Join(" / ", end.Prepend(title).Distinct());
            }

            episode.Overview = NullIfEmpty(match.Overview);
            episode.ReleaseDate = match.AirDate is { } airDate && DateOnly.TryParse(airDate, CultureInfo.InvariantCulture, out var d) ? d : null;
            episode.RuntimeMinutes = match.Runtime > 0 ? match.Runtime : series.RuntimeMinutes;
            episode.ThumbUrl = match.Images.FirstOrDefault(i => i.CoverType == "screenshot")?.RemoteUrl;
            episode.BackdropUrl = series.BackdropUrl;
            episode.AccentColor = series.AccentColor;
            episode.ArrId = match.Id;
            episode.MetadataSource = sonarr.Name;
            episode.MetadataUpdatedAt = DateTime.UtcNow;
        }
    }

    private async Task ApplySeriesCastAsync(MediaItem item, CancellationToken ct)
    {
        var showId = int.TryParse(item.TvMazeId, CultureInfo.InvariantCulture, out var id) ? id
            : int.TryParse(item.TvdbId, CultureInfo.InvariantCulture, out var tvdb) && await tvmaze.LookupByTvdbAsync(tvdb, ct).ConfigureAwait(false) is { } show ? show.Id
            : 0;
        if (showId == 0)
        {
            return;
        }

        item.TvMazeId = showId.ToString(CultureInfo.InvariantCulture);
        var cast = await tvmaze.GetCastAsync(showId, ct).ConfigureAwait(false) ?? [];
        var crew = await tvmaze.GetCrewAsync(showId, ct).ConfigureAwait(false) ?? [];

        var wanted = cast.Take(MaxCast).Select(c => (c.Person, Role: CreditRole.Actor, Character: (string?)c.Character.Name))
            .Concat(crew.Where(c => c.Type == "Creator").Select(c => (c.Person, Role: CreditRole.Creator, Character: (string?)null)))
            .DistinctBy(c => (c.Person.Id, c.Role))
            .ToList();

        var people = await PeopleAsync(tmdb: false, ct).ConfigureAwait(false);

        db.Credits.RemoveRange(item.Credits);
        item.Credits.Clear();
        var order = 0;
        foreach (var (person, role, character) in wanted)
        {
            var key = person.Id.ToString(CultureInfo.InvariantCulture);
            if (!people.TryGetValue(key, out var entity))
            {
                entity = new Person { Name = person.Name, TvMazeId = key };
                db.People.Add(entity);
                people[key] = entity;
            }

            entity.ImageUrl = person.Image?.Original ?? person.Image?.Medium ?? entity.ImageUrl;
            item.Credits.Add(new Credit { PersonId = entity.Id, Role = role, Character = character, Order = order++ });
        }
    }

    // ------------------------------------------------------------------ Shared

    private async Task ApplyImagesAsync(MediaItem item, IReadOnlyList<ArrImage> arrImages, string? remotePoster, CancellationToken ct)
    {
        string? Url(string type) => arrImages.FirstOrDefault(i => i.CoverType == type && !string.IsNullOrEmpty(i.RemoteUrl))?.RemoteUrl;

        var backdrop = Url("fanart");
        var poster = Url("poster") ?? remotePoster;
        var changed = backdrop != item.BackdropUrl || poster != item.PosterUrl;

        item.PosterUrl = poster;
        item.BackdropUrl = backdrop;
        item.LogoUrl = Url("clearlogo");

        // Only from artwork fetched over https: a file:// or embedded:// URL from Radarr or Sonarr isn't read off disk.
        var httpsPoster = ArtworkUrl.IsHttps(poster) ? poster : null;
        if ((changed || item.AccentColor is null) && (ArtworkUrl.IsHttps(backdrop) ? backdrop : httpsPoster) is { } source)
        {
            item.AccentColor = await images.AccentColorAsync(source, ct).ConfigureAwait(false)
                ?? (httpsPoster is not null && httpsPoster != source ? await images.AccentColorAsync(httpsPoster, ct).ConfigureAwait(false) : null);
        }
    }

    /// <summary>
    /// People shared across the whole refresh, so an actor in two films processed before the next save
    /// isn't created twice.
    /// </summary>
    private async Task<Dictionary<string, Person>> PeopleAsync(bool tmdb, CancellationToken ct)
    {
        if (tmdb)
        {
            return _peopleByTmdb ??= Unique(await db.People.Where(p => p.TmdbId != null).ToListAsync(ct).ConfigureAwait(false), p => p.TmdbId!);
        }

        return _peopleByTvMaze ??= Unique(await db.People.Where(p => p.TvMazeId != null).ToListAsync(ct).ConfigureAwait(false), p => p.TvMazeId!);

        static Dictionary<string, Person> Unique(List<Person> people, Func<Person, string> key)
        {
            var map = new Dictionary<string, Person>(StringComparer.Ordinal);
            foreach (var person in people)
            {
                map.TryAdd(key(person), person);
            }

            return map;
        }
    }

    private async Task SetGenresAsync(MediaItem item, IReadOnlyList<string> names, CancellationToken ct)
    {
        _genres ??= await db.Genres.ToDictionaryAsync(g => g.Name, StringComparer.OrdinalIgnoreCase, ct).ConfigureAwait(false);

        item.Genres.Clear();
        foreach (var name in names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_genres.TryGetValue(name, out var genre))
            {
                genre = new Genre { Name = name.Trim() };
                db.Genres.Add(genre);
                _genres[name] = genre;
            }

            item.Genres.Add(genre);
        }
    }

    private IQueryable<MediaItem> Stale(Guid libraryId, MediaKind kind, bool force, List<Guid> also)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - StaleAfter;
        var retryCutoff = now - RetryUnmatchedAfter;
        // Items that didn't match last time, or were only found by a title search (or, for music, only have their tags),
        // are retried daily (a rename or new *arr entry may fix them). A new *arr entry also arrives as a webhook, whose
        // folder is refreshed straight away.
        return db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == kind
            && (force || also.Contains(i.Id) || i.MetadataUpdatedAt == null || i.MetadataUpdatedAt < cutoff
                || ((i.PosterUrl == null || ((i.MetadataSource == RadarrLookup || i.MetadataSource == SonarrLookup || i.MetadataSource == Tags) && !i.MatchLocked))
                    && i.MetadataUpdatedAt < retryCutoff)));
    }

    /// <summary>
    /// The search result most likely to be this item: within a year of its year (when known), then the same title
    /// (accents and all, so "Shōgun" isn't taken for "Shogun"), then the same title ignoring accents and punctuation,
    /// then whatever the search ranked first.
    /// </summary>
    public static T? BestByTitle<T>(IReadOnlyList<T> results, MediaItem item, Func<T, string> title, Func<T, int> year) where T : class
    {
        var candidates = results.Where(r => item.Year is null || year(r) == 0 || Math.Abs(year(r) - item.Year.Value) <= 1).ToList();
        var wanted = item.Title.Normalize(System.Text.NormalizationForm.FormC);
        return candidates.FirstOrDefault(r => string.Equals(title(r).Normalize(System.Text.NormalizationForm.FormC), wanted, StringComparison.OrdinalIgnoreCase))
            ?? candidates.FirstOrDefault(r => Loose(title(r)) == Loose(wanted))
            ?? candidates.FirstOrDefault();

        static string Loose(string s) => new(s.Normalize(System.Text.NormalizationForm.FormD)
            .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    }

    /// <summary>The films or series in (or containing) the given folders.</summary>
    private async Task<List<Guid>> WithinAsync(Guid libraryId, MediaKind kind, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        if (within.Count == 0)
        {
            return [];
        }

        var candidates = await db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == kind && i.Path != null)
            .Select(i => new { i.Id, i.Path }).ToListAsync(ct).ConfigureAwait(false);
        return candidates.Where(c => within.Any(w => PathComparison.IsUnder(c.Path!, w) || PathComparison.IsUnder(w, c.Path!)))
            .Select(c => c.Id).ToList();
    }

    /// <summary>
    /// A title's TMDB or TVDB id, or for the one title in the folders just scanned that has none yet, the id its webhook
    /// gave. That one is also preferred over an id a title search found, which may be the wrong title's. Not when those
    /// folders hold more than one title: then which one it belongs to isn't known.
    /// </summary>
    private static Func<MediaItem, string?> Hinted(List<Guid> within, int? hint, Func<MediaItem, string?> own) =>
        hint is > 0 && within.Count == 1
            ? i => i.Id == within[0] && (own(i) is null || (i.MetadataSource is RadarrLookup or SonarrLookup && !i.MatchLocked))
                ? hint.Value.ToString(CultureInfo.InvariantCulture)
                : own(i)
            : own;

    /// <summary>
    /// What Radarr or Sonarr have that may match <paramref name="only"/>. When just a webhook's titles are due, only
    /// those are asked for (by their Radarr/Sonarr id, else their TMDB/TVDB id) rather than the whole library, which can
    /// run to megabytes on every import. A title with neither, or a null <paramref name="only"/>, needs the whole list.
    /// </summary>
    /// <param name="instances">The Radarr or Sonarr instances.</param>
    /// <param name="only">The titles due, if they're all from the folders just scanned; else null.</param>
    /// <param name="externalId">The title's TMDB (films) or TVDB (shows) id.</param>
    /// <param name="all">Fetches an instance's whole library.</param>
    /// <param name="byArrId">Fetches one title by the instance's own id.</param>
    /// <param name="byExternalId">Fetches the titles (none or one) with a TMDB or TVDB id.</param>
    /// <param name="ct">Stops the fetch.</param>
    private async Task<List<(ArrInstance Instance, T Entry)>> KnownAsync<T>(
        List<ArrInstance> instances,
        IReadOnlyList<MediaItem>? only,
        Func<MediaItem, string?> externalId,
        Func<ArrInstance, CancellationToken, Task<IReadOnlyList<T>>> all,
        Func<ArrInstance, int, CancellationToken, Task<T>> byArrId,
        Func<ArrInstance, int, CancellationToken, Task<IReadOnlyList<T>>> byExternalId,
        CancellationToken ct)
    {
        var known = new List<(ArrInstance Instance, T Entry)>();
        if (only is not null && only.All(i => i.ArrId is not null && instances.Any(a => a.Name == i.MetadataSource) || ExternalId(i) is not null))
        {
            foreach (var item in only)
            {
                if (item.ArrId is { } arrId && instances.FirstOrDefault(a => a.Name == item.MetadataSource) is { } source)
                {
                    try
                    {
                        known.Add((source, await byArrId(source, arrId, ct).ConfigureAwait(false)));
                        continue;
                    }
                    catch (Exception ex) when (IsNetworkFailure(ex, ct))
                    {
                        // Gone from that instance (a 404), or it's down: try the other id.
                        LogArrUnavailable(source.Name, ex.Message);
                    }
                }

                if (ExternalId(item) is not { } id)
                {
                    continue;
                }

                foreach (var instance in instances)
                {
                    try
                    {
                        known.AddRange((await byExternalId(instance, id, ct).ConfigureAwait(false)).Select(e => (instance, e)));
                    }
                    catch (Exception ex) when (IsNetworkFailure(ex, ct))
                    {
                        LogArrUnavailable(instance.Name, ex.Message);
                    }
                }
            }

            return known;
        }

        foreach (var instance in instances)
        {
            try
            {
                known.AddRange((await all(instance, ct).ConfigureAwait(false)).Select(e => (instance, e)));
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                LogArrUnavailable(instance.Name, ex.Message);
            }
        }

        return known;

        int? ExternalId(MediaItem item) =>
            int.TryParse(externalId(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    private async Task SaveEveryAsync(int count, CancellationToken ct)
    {
        if (count % 25 == 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // Seasons and episodes of series already done aren't needed again; tracking them all would make every
            // later save check the whole TV library for changes.
            foreach (var entry in db.ChangeTracker.Entries<MediaItem>()
                .Where(e => e.State == EntityState.Unchanged && e.Entity.Kind is MediaKind.Season or MediaKind.Episode).ToList())
            {
                entry.State = EntityState.Detached;
            }
        }
    }

    /// <summary>
    /// A request that failed or timed out: HttpClient reports a timeout as a <see cref="TaskCanceledException"/>,
    /// which only means "stop" when <paramref name="ct"/> was cancelled, and a connection dropped while the answer was
    /// being read as an <see cref="IOException"/> (<see cref="HttpIOException"/>).
    /// </summary>
    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException or IOException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

    // macOS can hand back decomposed Unicode (e + combining accent) where the *arr apps store composed "é";
    // compare composed forms so "Léon" matches "Léon".
    private string? MapPath(string? path) => path is null ? null : Key(PathMapper.Map(path, settings.PathMappings));

    private static string Key(string path) => path.TrimEnd('/').Normalize(System.Text.NormalizationForm.FormC);

    private static Dictionary<string, T> Index<T>(IEnumerable<T> source, Func<T, string?> key)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in source)
        {
            if (key(item) is { Length: > 0 } k)
            {
                index.TryAdd(k, item);
            }
        }

        return index;
    }

    private static (ArrInstance Instance, T Item)? Find<T>(Dictionary<string, (ArrInstance, T)> index, string? key) =>
        key is not null && index.TryGetValue(Key(key), out var hit) ? hit : null;

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? DateOnlyOf(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Instance} is unavailable: {Error}")]
    private partial void LogArrUnavailable(string instance, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh metadata for {Title}")]
    private partial void LogItemFailed(Exception exception, string title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata lookup for {Term} failed: {Error}")]
    private partial void LogLookupFailed(string term, string error);
}
