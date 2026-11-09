using System.Globalization;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Parsing;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Integrations.Arr.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Flexarr.Metadata;

/// <param name="FromLocal">Matched by neither Radarr nor Sonarr, but filled in from the .nfo and artwork beside it.</param>
public sealed record MetadataRefreshResult(int FromArr, int FromLookup, int Unmatched, int FromLocal = 0);

/// <summary>
/// Fills in titles, artwork, overviews, genres, ratings, trailers and cast for a library.
/// Radarr and Sonarr are the primary source (they already hold metadata for everything they manage, with no
/// API keys needed); items they don't manage are looked up through their search endpoints; TV cast comes from TVmaze.
/// What neither of them finds is looked up on TVmaze (shows) and, with a key, TMDB (films). A .nfo and artwork beside a
/// film or show (Kodi's, Jellyfin's, Plex's) win over all of them.
/// </summary>
public sealed partial class MetadataRefresher(
    FlexarrDbContext db,
    ArrClient arr,
    TvMazeClient tvmaze,
    ImageCache images,
    IMetadataSettings settings,
    ILogger<MetadataRefresher> logger,
    TmdbClient? tmdb = null,
    Books.BookProviders? bookProviders = null,
    SeerrClient? seerr = null)
{
    private static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryUnmatchedAfter = TimeSpan.FromDays(1);
    private const int MaxCast = 24;

    /// <summary>The source of a film's or show's details when they came from searching for its folder's name.</summary>
    public const string RadarrLookup = "Radarr lookup", SonarrLookup = "Sonarr lookup";

    /// <summary>The source of an artist's or album's details when nothing but its files' tags supplied them.</summary>
    public const string Tags = "Tags";

    /// <summary>The source of an artist's or album's details when they were matched on Deezer by hand (Fix match).</summary>
    public const string Deezer = "Deezer";

    /// <summary>The source of a film's or show's details when only the .nfo and artwork beside it supplied them.</summary>
    public const string Local = "Local files";

    /// <summary>The source of a film's details when TMDB had them, and a show's when TVmaze did, found by an id.</summary>
    public const string Tmdb = "TMDB", TvMaze = "TVmaze";

    /// <summary>The same, found by searching for the title (which can pick the wrong one, so these are worth a look).</summary>
    public const string TmdbSearch = "TMDB search", TvMazeSearch = "TVmaze search";

    private Dictionary<string, Genre>? _genres;
    private bool _seerrDown;
    private int _seerrFailuresInARow;
    private (Guid Id, string? Tmdb, string? Imdb, string? Tvdb, string? TvMaze) _idsBefore;

    /// <summary>How long Seerr's scores are kept before asking again, plus up to two weeks so a library isn't asked all at once.</summary>
    private static readonly TimeSpan ScoresStaleAfter = TimeSpan.FromDays(30);

    /// <summary>How long one scores question may take: a slow Seerr mustn't hold up a scan or Fix match.</summary>
    private static readonly TimeSpan SeerrScoresTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Errors in a row (for different titles) after which Seerr is left alone for the rest of the refresh.</summary>
    private const int SeerrFailuresBeforeGivingUp = 5;
    private Dictionary<string, Person>? _peopleByTmdb;
    private Dictionary<string, Person>? _peopleByTvMaze;

    /// <summary>A timed refresh's limit: at most this many titles of each kind, the longest since refreshed first.</summary>
    private int? _dueAtMost;

    /// <summary>A timed refresh's titles to leave for now.</summary>
    private IReadOnlyCollection<Guid> _notNow = [];

    /// <summary>A scan's refresh: only titles never refreshed yet and those in the folders just scanned.</summary>
    private bool _scannedOnly;

    /// <summary>
    /// A scan's forced refresh of the folders just scanned (a cover or <c>.nfo</c> changed there): those titles are read
    /// afresh, and the rest of the library is left alone.
    /// </summary>
    private bool _forcedWithin;

    /// <summary>
    /// Titles a refresh left due because their Radarr or Sonarr didn't answer (see <see cref="RefreshDueAsync"/>).
    /// </summary>
    public List<Guid> LeftDue { get; } = [];

    /// <summary>
    /// Refreshes the <paramref name="atMost"/> titles of the library longest due (and as many of their seasons,
    /// episodes or albums as come with them), on a timer of its own, so a whole library falling due together isn't
    /// refreshed inside one scan. Titles a Radarr or Sonarr that didn't answer left due are in <see cref="LeftDue"/>,
    /// for the caller to pass back as <paramref name="notNow"/> a while, so they don't hold up the rest.
    /// </summary>
    public async Task<MetadataRefreshResult> RefreshDueAsync(Library library, int atMost, IReadOnlyCollection<Guid> notNow, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(notNow);
        _dueAtMost = atMost;
        _notNow = notNow;
        try
        {
            return await RefreshLibraryAsync(library, false, [], null, null, ct).ConfigureAwait(false);
        }
        finally
        {
            _dueAtMost = null;
            _notNow = [];
        }
    }

    /// <summary>
    /// What a scan refreshes: titles new since the last refresh and those in <paramref name="within"/>, read afresh with
    /// <paramref name="force"/>; with <paramref name="force"/> and no <paramref name="within"/>, everything. Titles merely
    /// out of date are left to <see cref="RefreshDueAsync"/>.
    /// </summary>
    public async Task<MetadataRefreshResult> RefreshScannedAsync(Library library, bool force, IReadOnlyCollection<string> within, int? tmdbId, int? tvdbId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(within);
        _scannedOnly = true;
        _forcedWithin = force && within.Count > 0;
        try
        {
            return await RefreshLibraryAsync(library, force, within, tmdbId, tvdbId, ct).ConfigureAwait(false);
        }
        finally
        {
            _scannedOnly = false;
            _forcedWithin = false;
        }
    }

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
            LibraryType.HomeVideos => await RefreshHomeVideosAsync(library.Id, force, within, ct).ConfigureAwait(false),
            LibraryType.Audiobooks => await RefreshBooksAsync(library.Id, force, within, ct).ConfigureAwait(false),
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
                .Known.Select(k => (k.Instance, Movie: k.Entry)).ToList();
            result = await RefreshMovieAsync(item, Index(known, k => MapPath(k.Movie.Path)), Index(known, k => k.Movie.ImdbId),
                Index(known, k => k.Movie.TmdbId > 0 ? k.Movie.TmdbId.ToString(CultureInfo.InvariantCulture) : null), radarrs, force: true, ct).ConfigureAwait(false);
        }
        else if (item.Kind == MediaKind.Series)
        {
            var sonarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Sonarr).ToList();
            var known = (await KnownAsync(sonarrs, only, i => i.TvdbId, arr.GetSeriesAsync, arr.GetOneSeriesAsync, arr.GetSeriesByTvdbIdAsync, ct).ConfigureAwait(false))
                .Known.Select(k => (k.Instance, Series: k.Entry)).ToList();
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
        var items = await Stale(libraryId, MediaKind.Movie, force, also).Include(i => i.Genres).Include(i => i.Credits).AsSplitQuery().ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var radarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Radarr).ToList();
        // Just the webhook's, or a timed refresh's few: ask for just those.
        var onlyWithin = (!force || _forcedWithin) && (_dueAtMost is not null || items.All(i => also.Contains(i.Id)));
        var (fetched, allAnswered) = await KnownAsync(radarrs, onlyWithin ? items : null, Hinted(also, tmdbId, i => i.TmdbId),
            arr.GetMoviesAsync, arr.GetMovieAsync, arr.GetMoviesByTmdbIdAsync, ct).ConfigureAwait(false);
        var known = fetched.Select(k => (k.Instance, Movie: k.Entry)).ToList();

        var byPath = Index(known, k => MapPath(k.Movie.Path));
        var byImdb = Index(known, k => k.Movie.ImdbId);
        var byTmdb = Index(known, k => k.Movie.TmdbId > 0 ? k.Movie.TmdbId.ToString(CultureInfo.InvariantCulture) : null);
        int fromArr = 0, fromLookup = 0, unmatched = 0, fromLocal = 0;

        foreach (var item in items)
        {
            // A Radarr that didn't answer (down, restarting, a timeout) isn't a film gone from it: leave its films as they
            // are, still linked and still due, rather than re-sourcing them from TMDB unlinked for a week.
            if (!allAnswered && Unconfirmed(item, known.Select(k => (k.Instance, k.Movie.Id))))
            {
                LeftDue.Add(item.Id);
                continue;
            }

            try
            {
                switch (await RefreshMovieAsync(item, byPath, byImdb, byTmdb, radarrs, force, ct).ConfigureAwait(false))
                {
                    case true: fromArr++; break;
                    case false: fromLookup++; break;
                    default:
                        if (item.MetadataSource == Local) { fromLocal++; } else { unmatched++; }
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, item.Title);
                unmatched++;
            }

            item.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + fromLookup + unmatched + fromLocal, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, fromLookup, unmatched, fromLocal);
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
        _idsBefore = IdsOf(item);
        var kept = Keep(item); // What was edited by hand stays; the sources fill in the rest.
        var result = await RefreshMovieFromSourcesAsync(item, byPath, byImdb, byTmdb, radarrs, force, ct).ConfigureAwait(false);
        await RestoreAsync(item, kept, ct).ConfigureAwait(false);
        await ApplySeerrRatingsAsync(item, ct).ConfigureAwait(false);
        return result;
    }

    private async Task<bool?> RefreshMovieFromSourcesAsync(
        MediaItem item,
        Dictionary<string, (ArrInstance, RadarrMovie)> byPath,
        Dictionary<string, (ArrInstance, RadarrMovie)> byImdb,
        Dictionary<string, (ArrInstance, RadarrMovie)> byTmdb,
        List<ArrInstance> radarrs,
        bool force,
        CancellationToken ct)
    {
        // A .nfo's ids find the film in Radarr (or its search) when the folder alone wouldn't.
        var local = item.Path is { } path ? LocalFiles.ForMovie(path) : LocalArt.None;
        if (!item.MatchLocked && local.Nfo?.Ids is { } ids)
        {
            item.TmdbId = ids.TmdbId ?? item.TmdbId;
            item.ImdbId = ids.ImdbId ?? item.ImdbId;
        }

        // A match fixed by hand is found by its TMDB id only: the folder may be one Radarr has as a different film.
        var hit = item.MatchLocked
            ? Find(byTmdb, item.TmdbId)
            : Find(byPath, item.Path) ?? Find(byImdb, item.ImdbId) ?? Find(byTmdb, item.TmdbId);
        if (hit is (var instance, var movie))
        {
            await ApplyMovieAsync(item, movie, instance, local, ct).ConfigureAwait(false);
            if (force || item.Credits.Count == 0)
            {
                await ApplyMovieCreditsAsync(item, instance, movie.Id, ct).ConfigureAwait(false);
            }

            return true;
        }

        if (radarrs.Count > 0 && await LookupMovieAsync(radarrs[0], item, ct).ConfigureAwait(false) is { } found)
        {
            await ApplyMovieAsync(item, found, null, local, ct).ConfigureAwait(false);
            return false;
        }

        if (settings.TmdbApiKey is { Length: > 0 } key && await FindOnTmdbAsync(key, item, ct).ConfigureAwait(false) is ({ } film, var searched))
        {
            await ApplyTmdbMovieAsync(item, film, searched, local, force, ct).ConfigureAwait(false);
            return false;
        }

        await ApplyLocalOnlyAsync(item, local, ct).ConfigureAwait(false);
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

    private async Task ApplyMovieAsync(MediaItem item, RadarrMovie movie, ArrInstance? instance, LocalArt local, CancellationToken ct)
    {
        item.Title = movie.Title;
        item.SortTitle = SortTitle.For(movie.Title);
        item.OriginalTitle = movie.OriginalTitle != movie.Title ? movie.OriginalTitle : null;
        item.Year = movie.Year > 0 ? movie.Year : item.Year;
        item.Overview = NullIfEmpty(movie.Overview);
        item.ContentRating = NullIfEmpty(movie.Certification);
        item.StrictestRating = null;
        item.Studio = NullIfEmpty(movie.Studio);
        item.RuntimeMinutes = movie.Runtime > 0 ? movie.Runtime : null;
        item.Status = movie.Status;
        item.ReleaseDate = DateOnlyOf(movie.InCinemas ?? movie.DigitalRelease ?? movie.PhysicalRelease ?? movie.ReleaseDate);
        item.CommunityRating = movie.Ratings?.Imdb?.Value is > 0 ? movie.Ratings.Imdb.Value : movie.Ratings?.Tmdb?.Value is > 0 ? movie.Ratings.Tmdb.Value : null;
        item.Popularity = movie.Popularity;
        item.TrailerYouTubeId = NullIfEmpty(movie.YouTubeTrailerId);
        item.ImdbId = NullIfEmpty(movie.ImdbId) ?? item.ImdbId;
        item.TmdbId = movie.TmdbId > 0 ? movie.TmdbId.ToString(CultureInfo.InvariantCulture) : item.TmdbId;

        // Seerr's scores, while they're fresh, win: Radarr only fills what Seerr didn't give, so a film whose Radarr
        // score is missing or differs doesn't lose Seerr's (and its Certified Fresh) on every refresh until Seerr is
        // asked again. A film matched to a different title takes Radarr's.
        var critic = movie.Ratings?.RottenTomatoes?.Value is > 0 and var rt ? (int)Math.Round(rt) : (int?)null;
        var imdbRating = movie.Ratings?.Imdb?.Value is > 0 and var imdb ? imdb : (double?)null;
        var seerrFresh = ScoresFresh(item) && IdsOf(item) == _idsBefore;
        item.CriticRating = seerrFresh ? item.CriticRating ?? critic : critic;
        item.ImdbRating = seerrFresh ? item.ImdbRating ?? imdbRating : imdbRating;
        item.CriticCertified &= item.CriticRating is >= 60;
        item.ArrId = instance is null ? null : movie.Id;
        item.MetadataSource = instance?.Name ?? RadarrLookup;
        ForgetScoresIfRematched(item, keepArrScores: true);
        ApplyNfo(item, local.Nfo);
        await ApplyImagesAsync(item, movie.Images, movie.RemotePoster, local, ct).ConfigureAwait(false);
        await SetGenresAsync(item, NfoGenres(item, local) ?? movie.Genres, ct).ConfigureAwait(false);
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

        var people = await PeopleAsync(tmdb: true, wanted.Select(c => c.Credit.PersonTmdbId.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);

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

            await SetPersonPhotoAsync(person, credit.Images.FirstOrDefault(i => i.CoverType == "headshot")?.RemoteUrl, PersonSources.Tmdb, ct).ConfigureAwait(false);
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
        if (lidarrs.Count == 0 && _dueAtMost is not null)
        {
            return new MetadataRefreshResult(0, 0, 0); // Nothing goes out of date: the tags are read when scanned.
        }

        if (lidarrs.Count == 0)
        {
            // Followed artists still waiting for their first album were followed through a Lidarr that's gone.
            await db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == MediaKind.Artist && i.Placeholder && !i.Children.Any())
                .ExecuteDeleteAsync(ct).ConfigureAwait(false);
            return await RefreshMusicFromTagsAsync(libraryId, force, within, ct).ConfigureAwait(false);
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
        var allAnswered = true;
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
                allAnswered = false;
            }
        }

        int fromArr = 0, fromTags = 0, unmatched = 0;
        foreach (var artist in artists)
        {
            var albums = await db.MediaItems.Where(i => i.ParentId == artist.Id && i.Kind == MediaKind.Album).Include(i => i.Genres).ToListAsync(ct).ConfigureAwait(false);
            var kept = albums.Prepend(artist).Select(i => (Item: i, Kept: Keep(i))).ToList(); // What was edited or chosen by hand stays.
            try
            {
                // An artist matched by hand is the Lidarr artist chosen (or, matched on Deezer, none), never another.
                var hit = artist.MatchLocked
                    ? known.Where(k => k.Instance.Name == artist.MetadataSource && k.Artist.Id == artist.ArrId).Select(k => (k.Instance, Artist: (LidarrArtist?)k.Artist)).FirstOrDefault()
                    : lidarrs
                        .Select(l => (Instance: l, Artist: LidarrMatcher.MatchArtist(artist, albums.Select(a => a.Path), known.Where(k => k.Instance == l).Select(k => k.Artist).ToList(), settings.PathMappings)))
                        .FirstOrDefault(h => h.Artist is not null);
                // A followed artist still waiting for their first album, whom Lidarr no longer has or follows (changed in
                // Lidarr itself): nothing is coming, so they leave the library.
                // Every Lidarr is asked: any that follows a match keeps them.
                if (artist.Placeholder && albums.Count == 0 && allAnswered && !artist.MatchLocked
                    && !lidarrs.Any(l => LidarrMatcher.MatchArtist(artist, [], known.Where(k => k.Instance == l).Select(k => k.Artist).ToList(), settings.PathMappings)
                        is { Monitored: true, MonitorNewItems: "new" or "all" }))
                {
                    db.MediaItems.Remove(artist);
                    if (artist.MusicBrainzId is { } followedId)
                    {
                        // Their follow is over too.
                        await db.MediaRequests.Where(r => r.Kind == MediaKind.Artist && r.FulfilledAt == null && r.MusicBrainzId == followedId)
                            .ExecuteUpdateAsync(u => u.SetProperty(r => r.FulfilledAt, DateTime.UtcNow), ct).ConfigureAwait(false);
                    }

                    unmatched++;
                    continue;
                }

                if (hit.Artist is { } lidarrArtist)
                {
                    await ApplyArtistAsync(artist, albums, lidarrArtist, hit.Instance, ct).ConfigureAwait(false);
                    fromArr++;
                }
                else
                {
                    await ApplyTagsAsync(artist, albums, ct).ConfigureAwait(false);
                    foreach (var album in albums.Where(a => a.MatchLocked))
                    {
                        await RefreshLockedAlbumAsync(album, null, ct).ConfigureAwait(false);
                    }

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

            foreach (var (item, keep) in kept)
            {
                await RestoreAsync(item, keep, ct).ConfigureAwait(false);
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

    /// <summary>
    /// No Lidarr: the tags stand, and the artwork the scanner found gets its ambient colour. A cover may have none
    /// (greyscale, near-black), so being handed to the tags with a time set is what says it was tried: it isn't read
    /// again every scan, and its time, which is in its artwork's address, only moves when the colour does.
    /// </summary>
    private async Task<MetadataRefreshResult> RefreshMusicFromTagsAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        // The folders just scanned: those albums and their artists, not every colour in the library.
        List<Guid>? scanned = null;
        if (within.Count > 0)
        {
            var albums = await WithinAsync(libraryId, MediaKind.Album, within, ct).ConfigureAwait(false);
            scanned = [.. albums, .. await WithinAsync(libraryId, MediaKind.Artist, within, ct).ConfigureAwait(false)];
        }

        var everything = force && !_forcedWithin;
        List<Guid> forced = _forcedWithin ? scanned ?? [] : [];
        var items = await db.MediaItems
            .Where(i => i.LibraryId == libraryId && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist) && i.PosterUrl != null
                && (everything || forced.Contains(i.Id)
                    || (i.AccentColor == null && (i.MetadataUpdatedAt == null || (i.MetadataSource != Tags && !i.MatchLocked))
                        && (scanned == null || scanned.Contains(i.Id)))))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            string? accent;
            try
            {
                accent = await images.ReadAccentColorAsync(item.PosterUrl!, ct).ConfigureAwait(false);
            }
            catch (ImageCacheBusyException)
            {
                continue; // not looked at: the next refresh tries again
            }

            if (accent != item.AccentColor || item.MetadataUpdatedAt is null)
            {
                item.MetadataUpdatedAt = DateTime.UtcNow;
            }

            item.AccentColor = accent;
            item.MetadataSource = item.MatchLocked ? item.MetadataSource : Tags; // Matched by hand: still what was chosen.
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
            if (album.MatchLocked)
            {
                await RefreshLockedAlbumAsync(album, lidarr.Name == album.MetadataSource ? lidarrAlbums : null, ct).ConfigureAwait(false);
            }
            else if (lidarrAlbums is not null)
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

    /// <summary>
    /// An album matched by hand: the Lidarr album chosen is read again (from <paramref name="sameArtist"/>, its
    /// artist's albums, when they were fetched; else by its id). One matched on Deezer keeps what it was given then.
    /// When Lidarr can't say, it stays as it is.
    /// </summary>
    private async Task RefreshLockedAlbumAsync(MediaItem album, IReadOnlyList<LidarrAlbum>? sameArtist, CancellationToken ct)
    {
        if (album.ArrId is not { } albumId || settings.ArrInstances.FirstOrDefault(a => a.Kind == ArrKind.Lidarr && a.Name == album.MetadataSource) is not { } lidarr)
        {
            return;
        }

        try
        {
            var chosen = sameArtist?.FirstOrDefault(a => a.Id == albumId) ?? await arr.GetAlbumAsync(lidarr, albumId, ct).ConfigureAwait(false);
            await ApplyAlbumAsync(album, chosen, lidarr, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct))
        {
            LogArrUnavailable(lidarr.Name, ex.Message);
        }
    }

    /// <summary>Fix match: an album is the Lidarr album chosen. The caller sets the lock and saves.</summary>
    public async Task ApplyChosenAlbumAsync(MediaItem album, LidarrAlbum chosen, ArrInstance lidarr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(lidarr);
        album.DeezerId = null;
        var kept = Keep(album);
        await ApplyAlbumAsync(album, chosen, lidarr, ct).ConfigureAwait(false);
        await RestoreAsync(album, kept, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Fix match: an album is the Deezer album chosen (read whole, with its genres). Kept as given: refreshes don't
    /// ask Deezer again. The caller sets the lock and saves.
    /// </summary>
    public async Task ApplyChosenAlbumAsync(MediaItem album, DeezerAlbum chosen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(chosen);
        var kept = Keep(album);
        Rename(album, chosen.Title);
        album.Year = chosen.Year ?? album.Year;
        album.ReleaseDate = DateOnly.TryParseExact(chosen.ReleaseDate, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
        album.AlbumType = chosen.RecordType switch
        {
            "ep" => AlbumKind.Ep,
            "single" => AlbumKind.SingleRelease,
            "compile" => AlbumKind.Compilation,
            "album" => AlbumKind.Album,
            _ => album.AlbumType,
        };
        album.RuntimeMinutes = chosen.Duration > 0 ? (int)Math.Max(1, Math.Round(chosen.Duration / 60.0)) : album.RuntimeMinutes;
        album.Overview = null;
        album.CommunityRating = null;
        album.ArrId = null;
        album.DeezerId = chosen.Id.ToString(CultureInfo.InvariantCulture);
        album.MetadataSource = Deezer;
        if (chosen.Genres?.Data is { Count: > 0 } genres)
        {
            await SetGenresAsync(album, [.. genres.Select(g => g.Name)], ct).ConfigureAwait(false);
        }

        await SetChosenCoverAsync(album, ArtworkUrl.TrustedRemote(chosen.Cover), ct).ConfigureAwait(false);
        await RestoreAsync(album, kept, ct).ConfigureAwait(false);
    }

    /// <summary>Fix match: an artist is the Lidarr artist chosen, and its albums are matched among that artist's. The caller locks and saves.</summary>
    public async Task ApplyChosenArtistAsync(MediaItem artist, List<MediaItem> albums, LidarrArtist chosen, ArrInstance lidarr, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(chosen);
        ArgumentNullException.ThrowIfNull(lidarr);
        artist.DeezerId = null;
        var kept = albums.Prepend(artist).Select(i => (Item: i, Kept: Keep(i))).ToList();
        await ApplyArtistAsync(artist, albums, chosen, lidarr, ct).ConfigureAwait(false);
        foreach (var (item, keep) in kept)
        {
            await RestoreAsync(item, keep, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Fix match: an artist is the Deezer artist chosen, with their name and photo. The caller locks and saves.</summary>
    public async Task ApplyChosenArtistAsync(MediaItem artist, DeezerArtist chosen, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(chosen);
        var kept = Keep(artist);
        Rename(artist, chosen.Name);
        artist.ArrId = null;
        artist.DeezerId = chosen.Id.ToString(CultureInfo.InvariantCulture);
        artist.MetadataSource = Deezer;
        if (ArtworkUrl.TrustedRemote(chosen.Picture) is { } photo)
        {
            await SetChosenCoverAsync(artist, photo, ct).ConfigureAwait(false);
        }

        await RestoreAsync(artist, kept, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Match automatically again, for an album or artist: unlocked, and back to its tags' name, with the cover beside
    /// its files (or none, which the next scan or Lidarr fills in) in place of one from the match. The caller saves
    /// and has the folder read again.
    /// </summary>
    public static void ForgetMusicMatch(MediaItem item, string? localCover)
    {
        ArgumentNullException.ThrowIfNull(item);
        item.MatchLocked = false;
        item.ArtworkLocked = false;
        item.DeezerId = null;
        if (!item.DetailsLocked && item.OriginalTitle is { } tagged)
        {
            item.Title = tagged;
            item.SortTitle = SortTitle.For(tagged);
            item.OriginalTitle = null;
        }

        if (item.PosterUrl is { } poster && !poster.StartsWith("file://", StringComparison.Ordinal) && !poster.StartsWith("embedded://", StringComparison.Ordinal))
        {
            item.PosterUrl = localCover;
            item.AccentColor = null;
        }

        item.ArrId = null;
        item.MetadataSource = Tags;
        item.MetadataUpdatedAt = null;
    }

    /// <summary>A cover chosen (or brought by the match), with its colour; null keeps the one there.</summary>
    public async Task SetChosenCoverAsync(MediaItem item, string? cover, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (cover is null || cover == item.PosterUrl)
        {
            return;
        }

        item.PosterUrl = cover;
        item.AccentColor = await images.AccentColorAsync(cover, ct).ConfigureAwait(false);
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
        if (item.MatchLocked)
        {
            // Matched by hand: what was chosen stands even where Lidarr doesn't vouch for it.
            if (item.AccentColor is null && item.PosterUrl is { } chosen)
            {
                item.AccentColor = await images.AccentColorAsync(chosen, ct).ConfigureAwait(false);
            }

            return;
        }

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
    public static string? ArrImage(ArrInstance lidarr, IReadOnlyList<ArrImage> lidarrImages, string coverType, string apiFolder)
    {
        ArgumentNullException.ThrowIfNull(lidarr);
        ArgumentNullException.ThrowIfNull(lidarrImages);
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
        var items = await Stale(libraryId, MediaKind.Series, force, also).Include(i => i.Genres).Include(i => i.Credits).AsSplitQuery().ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var sonarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        // Just the webhook's, or a timed refresh's few: ask for just those.
        var onlyWithin = (!force || _forcedWithin) && (_dueAtMost is not null || items.All(i => also.Contains(i.Id)));
        var (fetched, allAnswered) = await KnownAsync(sonarrs, onlyWithin ? items : null, Hinted(also, tvdbId, i => i.TvdbId),
            arr.GetSeriesAsync, arr.GetOneSeriesAsync, arr.GetSeriesByTvdbIdAsync, ct).ConfigureAwait(false);
        var known = fetched.Select(k => (k.Instance, Series: k.Entry)).ToList();

        var byPath = Index(known, k => MapPath(k.Series.Path));
        var byTvdb = Index(known, k => k.Series.TvdbId > 0 ? k.Series.TvdbId.ToString(CultureInfo.InvariantCulture) : null);
        var byImdb = Index(known, k => k.Series.ImdbId);
        int fromArr = 0, fromLookup = 0, unmatched = 0, fromLocal = 0;

        foreach (var item in items)
        {
            // As for films: a Sonarr that didn't answer leaves its shows linked and due, not re-sourced from TVmaze.
            if (!allAnswered && Unconfirmed(item, known.Select(k => (k.Instance, k.Series.Id))))
            {
                LeftDue.Add(item.Id);
                continue;
            }

            try
            {
                switch (await RefreshOneSeriesAsync(item, byPath, byTvdb, byImdb, sonarrs, force, ct).ConfigureAwait(false))
                {
                    case true: fromArr++; break;
                    case false: fromLookup++; break;
                    default:
                        if (item.MetadataSource == Local) { fromLocal++; } else { unmatched++; }
                        break;
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException || IsNetworkFailure(ex, ct))
            {
                LogItemFailed(ex, item.Title);
                unmatched++;
            }

            item.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(fromArr + fromLookup + unmatched + fromLocal, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(fromArr, fromLookup, unmatched, fromLocal);
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
        _idsBefore = IdsOf(item);
        var kept = Keep(item); // What was edited by hand stays; the sources fill in the rest.
        var result = await RefreshSeriesFromSourcesAsync(item, byPath, byTvdb, byImdb, sonarrs, force, ct).ConfigureAwait(false);
        await RestoreAsync(item, kept, ct).ConfigureAwait(false);
        await ApplySeerrRatingsAsync(item, ct).ConfigureAwait(false);
        return result;
    }

    private static (Guid, string?, string?, string?, string?) IdsOf(MediaItem item) => (item.Id, item.TmdbId, item.ImdbId, item.TvdbId, item.TvMazeId);

    /// <summary>
    /// When this refresh matched the item to a different title, the old title's scores go (Radarr's own, set just
    /// before, stay), so the new one never shows them; Seerr is asked again straight away.
    /// </summary>
    private void ForgetScoresIfRematched(MediaItem item, bool keepArrScores = false)
    {
        if (_idsBefore.Id != item.Id || IdsOf(item) == _idsBefore)
        {
            return;
        }

        if (!keepArrScores)
        {
            item.ImdbRating = null;
            item.CriticRating = null;
        }

        item.CriticCertified = false;
        item.ScoresCheckedAt = null;
    }

    /// <summary>
    /// Rotten Tomatoes (with Certified Fresh) and, for films, IMDb from Seerr, over what Radarr or a file said. Seerr is
    /// asked only when it's connected, the title has a TMDB id and its scores are a month old (or never asked); when it
    /// doesn't answer, the scores stay as they were.
    /// </summary>
    private async Task ApplySeerrRatingsAsync(MediaItem item, CancellationToken ct)
    {
        if (seerr is null || _seerrDown || settings.Seerr is not { } instance
            || !int.TryParse(item.TmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var tmdbId) || tmdbId <= 0
            || ScoresFresh(item))
        {
            return;
        }

        var movie = item.Kind == MediaKind.Movie;
        SeerrRatings? ratings;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SeerrScoresTimeout);
        try
        {
            ratings = SeerrRatings.Parse(await seerr.GetAsync(instance, SeerrRatings.PathFor(movie, tmdbId), timeout.Token).ConfigureAwait(false), movie);
            _seerrFailuresInARow = 0;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested
            && ex is HttpRequestException or IOException or OperationCanceledException or System.Text.Json.JsonException)
        {
            // Seerr unreachable or refusing the key: leave it alone for the rest of this refresh. Its error or a slow
            // answer for one title (Overseerr answers 500 when a lookup behind it fails) is that title's alone, unless it
            // keeps happening.
            var unreachable = ex is IOException
                || ex is HttpRequestException { StatusCode: null or System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden };
            _seerrDown = unreachable || ++_seerrFailuresInARow >= SeerrFailuresBeforeGivingUp;
            LogSeerrRatingsFailed(item.Title, ex.Message, _seerrDown);
            return;
        }

        item.ScoresCheckedAt = DateTime.UtcNow;
        if (ratings?.CriticScore is { } critics)
        {
            item.CriticRating = critics;
            item.CriticCertified = ratings.Certified;
        }
        else if (!movie)
        {
            // Seerr is a show's only source of Rotten Tomatoes: when it has none, neither does the show.
            item.CriticRating = null;
            item.CriticCertified = false;
        }

        item.ImdbRating = ratings?.Imdb ?? item.ImdbRating;
    }

    /// <summary>Whether Seerr gave the item's scores recently enough not to be asked again yet.</summary>
    private static bool ScoresFresh(MediaItem item) => item.ScoresCheckedAt is { } checkedAt && DateTime.UtcNow - checkedAt < ScoresStaleAfter + Spread(item.Id);

    /// <summary>0 to 14 days, the same for an item every time, so a library's scores don't all go stale together.</summary>
    private static TimeSpan Spread(Guid id) => TimeSpan.FromDays(Math.Abs(id.GetHashCode() % 15));

    private async Task<bool?> RefreshSeriesFromSourcesAsync(
        MediaItem item,
        Dictionary<string, (ArrInstance, SonarrSeries)> byPath,
        Dictionary<string, (ArrInstance, SonarrSeries)> byTvdb,
        Dictionary<string, (ArrInstance, SonarrSeries)> byImdb,
        List<ArrInstance> sonarrs,
        bool force,
        CancellationToken ct)
    {
        bool? result = null;
        var local = item.Path is { } path ? LocalFiles.ForSeries(path) : LocalArt.None;
        if (!item.MatchLocked && local.Nfo?.Ids is { } ids)
        {
            item.TvdbId = ids.TvdbId ?? item.TvdbId;
            item.TmdbId = ids.TmdbId ?? item.TmdbId;
            item.ImdbId = ids.ImdbId ?? item.ImdbId;
        }

        // A match fixed by hand is found by its TVDB id only: the folder may be one Sonarr has as a different show.
        var hit = item.MatchLocked
            ? Find(byTvdb, item.TvdbId)
            : Find(byPath, item.Path) ?? Find(byTvdb, item.TvdbId) ?? Find(byImdb, item.ImdbId);
        if (hit is (var instance, var series))
        {
            await ApplySeriesAsync(item, series, instance, local, ct).ConfigureAwait(false);
            await ApplyEpisodesAsync(item, series, instance, ct).ConfigureAwait(false);
            result = true;
        }
        else if (sonarrs.Count > 0 && await LookupSeriesAsync(sonarrs[0], item, ct).ConfigureAwait(false) is { } found)
        {
            await ApplySeriesAsync(item, found, null, local, ct).ConfigureAwait(false);
            await ApplySeasonsAsync(item, found, ct).ConfigureAwait(false);
            result = false;
        }
        else if (await FindOnTvMazeAsync(item, ct).ConfigureAwait(false) is ({ } show, var searched))
        {
            await ApplyTvMazeShowAsync(item, show, searched, local, ct).ConfigureAwait(false);
            result = false;
        }
        else
        {
            await ApplyLocalOnlyAsync(item, local, ct).ConfigureAwait(false);
        }

        await ApplyLocalSeasonsAsync(item, ct).ConfigureAwait(false);

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

    private async Task ApplySeriesAsync(MediaItem item, SonarrSeries series, ArrInstance? instance, LocalArt local, CancellationToken ct)
    {
        // TheTVDB tells shows apart with a year in the title ("Archer (2009)"); that's not what anyone says or
        // searches for, so it goes in the year field.
        var (title, taggedYear) = TvNameParser.SplitTrailingYear(series.Title);
        item.Title = title;
        item.SortTitle = SortTitle.For(title);
        item.Year = series.Year > 0 ? series.Year : taggedYear ?? item.Year;
        item.Overview = NullIfEmpty(series.Overview);
        item.ContentRating = NullIfEmpty(series.Certification);
        item.StrictestRating = null;
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
        ForgetScoresIfRematched(item);
        ApplyNfo(item, local.Nfo);
        await ApplyImagesAsync(item, series.Images, series.RemotePoster, local, ct).ConfigureAwait(false);
        await SetGenresAsync(item, NfoGenres(item, local) ?? series.Genres, ct).ConfigureAwait(false);
    }

    private async Task<List<MediaItem>> ApplySeasonsAsync(MediaItem series, SonarrSeries arrSeries, CancellationToken ct)
    {
        var seasons = await db.MediaItems.Where(i => i.ParentId == series.Id && i.Kind == MediaKind.Season).ToListAsync(ct).ConfigureAwait(false);
        foreach (var season in seasons)
        {
            var arrSeason = arrSeries.Seasons.FirstOrDefault(s => s.SeasonNumber == season.SeasonNumber);
            season.PosterUrl = arrSeason?.Images?.FirstOrDefault(i => i.CoverType == "poster" && ArtworkUrl.IsHttps(i.RemoteUrl))?.RemoteUrl ?? series.PosterUrl;
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
            episode.ThumbUrl = match.Images.FirstOrDefault(i => i.CoverType == "screenshot" && ArtworkUrl.IsHttps(i.RemoteUrl))?.RemoteUrl;
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

        var people = await PeopleAsync(tmdb: false, wanted.Select(c => c.Person.Id.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);

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

            await SetPersonPhotoAsync(entity, person.Image?.Original ?? person.Image?.Medium, PersonSources.TvMaze, ct).ConfigureAwait(false);
            item.Credits.Add(new Credit { PersonId = entity.Id, Role = role, Character = character, Order = order++ });
        }
    }

    // ------------------------------------------------------------------ Shared

    /// <summary>Radarr's or Sonarr's artwork, unless there's some beside the film or show.</summary>
    private Task ApplyImagesAsync(MediaItem item, IReadOnlyList<ArrImage> arrImages, string? remotePoster, LocalArt local, CancellationToken ct)
    {
        // Only https: a file:// URL from Radarr or Sonarr isn't read off disk.
        string? Url(string type) => arrImages.FirstOrDefault(i => i.CoverType == type && ArtworkUrl.IsHttps(i.RemoteUrl))?.RemoteUrl;

        var poster = local.Poster ?? Url("poster") ?? (ArtworkUrl.IsHttps(remotePoster) ? remotePoster : null);
        return SetArtworkAsync(item, poster, local.Backdrop ?? Url("fanart"), local.Logo ?? Url("clearlogo"), ct);
    }

    /// <param name="item">The film or show.</param>
    /// <param name="poster">An https URL, or a file:// one from <see cref="LocalFiles"/>; the same for the others.</param>
    /// <param name="backdrop">The backdrop.</param>
    /// <param name="logo">The logo.</param>
    /// <param name="ct">Stops the refresh.</param>
    private async Task SetArtworkAsync(MediaItem item, string? poster, string? backdrop, string? logo, CancellationToken ct)
    {
        var changed = backdrop != item.BackdropUrl || poster != item.PosterUrl;

        item.PosterUrl = poster;
        item.BackdropUrl = backdrop;
        item.LogoUrl = logo;

        if ((changed || item.AccentColor is null) && (backdrop ?? poster) is { } source)
        {
            item.AccentColor = await images.AccentColorAsync(source, ct).ConfigureAwait(false)
                ?? (poster is not null && poster != source ? await images.AccentColorAsync(poster, ct).ConfigureAwait(false) : null);
        }
    }

    // ------------------------------------------------------------------ TMDB and TVmaze

    /// <summary>The film on TMDB: by the id it has (one fixed by hand above all), its IMDb id, or its title and year.</summary>
    /// <returns>The film, and whether it was found by its title.</returns>
    private async Task<(TmdbMovie? Film, bool Searched)> FindOnTmdbAsync(string key, MediaItem item, CancellationToken ct)
    {
        var client = tmdb ?? throw new InvalidOperationException("TMDB isn't available.");
        try
        {
            var id = int.TryParse(item.TmdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var known) ? known : 0;
            var searched = false;
            if (id == 0 && item.ImdbId is { } imdb && await client.FindByImdbAsync(key, imdb, ct).ConfigureAwait(false) is { } byImdb)
            {
                id = byImdb.Id;
            }

            if (id == 0 && !item.MatchLocked)
            {
                var results = await client.SearchMoviesAsync(key, item.Title, item.Year, ct).ConfigureAwait(false);
                if (results.Count == 0 && item.Year is not null)
                {
                    results = await client.SearchMoviesAsync(key, item.Title, null, ct).ConfigureAwait(false);
                }

                id = BestByTitle(results, item, r => r.Title ?? "", r => r.Year)?.Id ?? 0;
                searched = true;
            }

            return (id == 0 ? null : await client.GetMovieAsync(key, id, ct).ConfigureAwait(false), searched);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct) || ex is System.Text.Json.JsonException)
        {
            LogLookupFailed($"TMDB: {item.Title}", ex.Message);
            return (null, false);
        }
    }

    private async Task ApplyTmdbMovieAsync(MediaItem item, TmdbMovie movie, bool searched, LocalArt local, bool force, CancellationToken ct)
    {
        var title = NullIfEmpty(movie.Title) ?? item.Title;
        item.Title = title;
        item.SortTitle = SortTitle.For(title);
        item.OriginalTitle = movie.OriginalTitle is { Length: > 0 } original && original != title ? original : null;
        item.Year = movie.Year > 0 ? movie.Year : item.Year;
        item.Overview = NullIfEmpty(movie.Overview);
        item.ContentRating = Certification(movie.ReleaseDates, settings.Country);
        item.StrictestRating = StrictestCertification(movie.ReleaseDates, settings.Country, item.ContentRating);
        item.Studio = movie.ProductionCompanies is [var studio, ..] ? studio.Name : null;
        item.RuntimeMinutes = movie.Runtime > 0 ? movie.Runtime : null;
        item.Status = movie.Status;
        item.ReleaseDate = DateOnly.TryParse(movie.ReleaseDate, CultureInfo.InvariantCulture, out var released) ? released : null;
        item.CommunityRating = movie.VoteAverage > 0 ? Math.Round(movie.VoteAverage, 1) : null;
        item.Popularity = movie.Popularity;
        item.TrailerYouTubeId = movie.Videos?.Results.Where(v => v.Site == "YouTube" && v.Type == "Trailer" && v.Key is { Length: > 0 })
            .OrderByDescending(v => v.Official).Select(v => v.Key).FirstOrDefault();
        item.TmdbId = movie.Id.ToString(CultureInfo.InvariantCulture);
        item.ImdbId = NullIfEmpty(movie.ImdbId) ?? item.ImdbId;
        item.ArrId = null;
        item.MetadataSource = searched ? TmdbSearch : Tmdb;
        ForgetScoresIfRematched(item);
        ApplyNfo(item, local.Nfo);

        // TMDB keeps some logos as SVG, which the image cache can't read; the PNGs are the ones to use.
        var logo = movie.Images?.Logos.Where(l => !l.FilePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Iso6391 == "en").ThenByDescending(l => l.VoteAverage).FirstOrDefault()?.FilePath;
        await SetArtworkAsync(item, local.Poster ?? TmdbClient.ImageUrl(movie.PosterPath), local.Backdrop ?? TmdbClient.ImageUrl(movie.BackdropPath),
            local.Logo ?? TmdbClient.ImageUrl(logo), ct).ConfigureAwait(false);
        await SetGenresAsync(item, NfoGenres(item, local) ?? movie.Genres?.Select(g => g.Name).ToList() ?? [], ct).ConfigureAwait(false);

        if ((force || item.Credits.Count == 0) && movie.Credits is { } credits)
        {
            await ApplyTmdbCreditsAsync(item, credits, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The age rating to show: the release in the server's country (people know their own), else the UK's (age limits
    /// use its steps), else the US one, else any release with one.
    /// </summary>
    private static string? Certification(TmdbReleases? releases, string? country)
    {
        var all = releases?.Results ?? [];
        return (all.FirstOrDefault(r => r.Iso31661 == (country ?? "GB"))?.ReleaseDates ?? [])
            .Concat(all.FirstOrDefault(r => r.Iso31661 == "GB")?.ReleaseDates ?? [])
            .Concat(all.FirstOrDefault(r => r.Iso31661 == "US")?.ReleaseDates ?? [])
            .Concat(all.SelectMany(r => r.ReleaseDates))
            .Select(r => NullIfEmpty(r.Certification)).FirstOrDefault(c => c is not null);
    }

    /// <summary>
    /// What age limits go by when it's stricter than the <paramref name="shown"/> certificate: the strictest of the
    /// server's country's, the UK's and the US's, so moving the server between the UK and the US changes nothing a limit
    /// lets through. See <see cref="MediaItem.StrictestRating"/>.
    /// </summary>
    internal static string? StrictestCertification(TmdbReleases? releases, string? country, string? shown) =>
        RatingAges.StricterThan(shown, (releases?.Results ?? [])
            .Where(r => r.Iso31661 == country || r.Iso31661 is "GB" or "US")
            .SelectMany(r => r.ReleaseDates).Select(r => r.Certification));

    private async Task ApplyTmdbCreditsAsync(MediaItem item, TmdbCredits credits, CancellationToken ct)
    {
        var wanted = credits.Cast.OrderBy(c => c.Order).Take(MaxCast)
            .Select(c => (c.Id, c.Name, c.ProfilePath, Role: CreditRole.Actor, Character: NullIfEmpty(c.Character)))
            .Concat(credits.Crew.Select(c => (c.Id, c.Name, c.ProfilePath, Role: c.Job switch
            {
                "Director" => CreditRole.Director,
                "Screenplay" or "Writer" => CreditRole.Writer,
                _ => (CreditRole?)null,
            }, Character: (string?)null)).Where(c => c.Role is not null).Select(c => (c.Id, c.Name, c.ProfilePath, Role: c.Role!.Value, c.Character)))
            .DistinctBy(c => (c.Id, c.Role))
            .ToList();

        var people = await PeopleAsync(tmdb: true, wanted.Select(c => c.Id.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);

        db.Credits.RemoveRange(item.Credits);
        item.Credits.Clear();
        var order = 0;
        foreach (var (id, name, profile, role, character) in wanted)
        {
            var tmdbId = id.ToString(CultureInfo.InvariantCulture);
            if (!people.TryGetValue(tmdbId, out var person))
            {
                person = new Person { Name = name, TmdbId = tmdbId };
                db.People.Add(person);
                people[tmdbId] = person;
            }

            await SetPersonPhotoAsync(person, TmdbClient.ImageUrl(profile), PersonSources.Tmdb, ct).ConfigureAwait(false);
            item.Credits.Add(new Credit { PersonId = person.Id, Role = role, Character = character, Order = order++ });
        }
    }

    /// <summary>The show on TVmaze: by the id it has, its TVDB or IMDb id, or its title (and year).</summary>
    /// <returns>The show, and whether it was found by its title.</returns>
    private async Task<(TvMazeShow? Show, bool Searched)> FindOnTvMazeAsync(MediaItem item, CancellationToken ct)
    {
        try
        {
            // An id a title search found isn't trusted over the folder's: the search runs again.
            if (int.TryParse(item.TvMazeId, NumberStyles.None, CultureInfo.InvariantCulture, out var known) && (item.MetadataSource != TvMazeSearch || item.MatchLocked)
                && await tvmaze.GetShowAsync(known, ct).ConfigureAwait(false) is { } show)
            {
                return (show, false);
            }

            if (int.TryParse(item.TvdbId, NumberStyles.None, CultureInfo.InvariantCulture, out var tvdb) && await tvmaze.LookupByTvdbAsync(tvdb, ct).ConfigureAwait(false) is { } byTvdb)
            {
                return (byTvdb, false);
            }

            if (item.ImdbId is { } imdb && await tvmaze.LookupByImdbAsync(imdb, ct).ConfigureAwait(false) is { } byImdb)
            {
                return (byImdb, false);
            }

            if (item.MatchLocked)
            {
                return (null, false);
            }

            var results = await tvmaze.SearchAsync(item.Title, ct).ConfigureAwait(false);
            return (BestByTitle(results.Select(r => r.Show).ToList(), item, s => s.Name, s => s.Year), true);
        }
        catch (Exception ex) when (IsNetworkFailure(ex, ct) || ex is System.Text.Json.JsonException)
        {
            LogLookupFailed($"TVmaze: {item.Title}", ex.Message);
            return (null, false);
        }
    }

    private async Task ApplyTvMazeShowAsync(MediaItem item, TvMazeShow show, bool searched, LocalArt local, CancellationToken ct)
    {
        item.Title = show.Name;
        item.SortTitle = SortTitle.For(show.Name);
        item.OriginalTitle = null;
        item.Year = show.Year > 0 ? show.Year : item.Year;
        item.Overview = PlainText(show.Summary);
        item.Studio = show.Network?.Name ?? show.WebChannel?.Name;
        item.RuntimeMinutes = show.Runtime ?? show.AverageRuntime;
        item.Status = show.Status;
        item.ReleaseDate = DateOnly.TryParse(show.Premiered, CultureInfo.InvariantCulture, out var premiered) ? premiered : null;
        item.CommunityRating = show.Rating?.Average is > 0 and var rating ? rating : null;
        item.TvMazeId = show.Id.ToString(CultureInfo.InvariantCulture);
        item.TvdbId = show.Externals?.Thetvdb is > 0 and var tvdb ? tvdb.ToString(CultureInfo.InvariantCulture) : item.TvdbId;
        item.ImdbId = NullIfEmpty(show.Externals?.Imdb) ?? item.ImdbId;
        item.ArrId = null;
        item.MetadataSource = searched ? TvMazeSearch : TvMaze;
        ForgetScoresIfRematched(item);
        ApplyNfo(item, local.Nfo);

        var images = await tvmaze.GetImagesAsync(show.Id, ct).ConfigureAwait(false);
        var backdrop = images.Where(i => i.Type == "background").OrderByDescending(i => i.Main).Select(i => i.Resolutions.Original?.Url).FirstOrDefault(u => u is not null);
        await SetArtworkAsync(item, local.Poster ?? show.Image?.Original ?? show.Image?.Medium, local.Backdrop ?? backdrop, local.Logo, ct).ConfigureAwait(false);
        await SetGenresAsync(item, NfoGenres(item, local) ?? show.Genres ?? [], ct).ConfigureAwait(false);

        // Seasons and episodes: Sonarr's would come from it; these are TVmaze's.
        var seasons = await db.MediaItems.Where(i => i.ParentId == item.Id && i.Kind == MediaKind.Season).ToListAsync(ct).ConfigureAwait(false);
        var tvSeasons = await tvmaze.GetSeasonsAsync(show.Id, ct).ConfigureAwait(false);
        foreach (var season in seasons)
        {
            var image = tvSeasons.FirstOrDefault(s => s.Number == season.SeasonNumber)?.Image;
            season.PosterUrl = image?.Original ?? image?.Medium ?? item.PosterUrl;
            season.BackdropUrl = item.BackdropUrl;
            season.AccentColor = item.AccentColor;
            season.MetadataUpdatedAt = DateTime.UtcNow;
        }

        var seasonIds = seasons.Select(s => s.Id).ToList();
        var episodes = await db.MediaItems.Where(i => i.ParentId != null && seasonIds.Contains(i.ParentId.Value)).ToListAsync(ct).ConfigureAwait(false);
        if (episodes.Count == 0)
        {
            return;
        }

        var tvEpisodes = await tvmaze.GetEpisodesAsync(show.Id, ct).ConfigureAwait(false);
        // TVmaze numbers specials by date rather than within season 0, so they're only matched by date.
        var byNumber = tvEpisodes.Where(e => e.Season is not null && e.Number is not null).GroupBy(e => (e.Season!.Value, e.Number!.Value)).ToDictionary(g => g.Key, g => g.First());
        var byDate = tvEpisodes.Where(e => e.Airdate is { Length: > 0 }).GroupBy(e => e.Airdate!).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            var match = episode.AirDate is { } date
                ? byDate.GetValueOrDefault(date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
                : byNumber.GetValueOrDefault((episode.SeasonNumber ?? 0, episode.EpisodeNumber ?? 0));
            if (match is null)
            {
                continue;
            }

            if (NullIfEmpty(match.Name) is { } name && name != "TBA")
            {
                var end = episode.EpisodeNumberEnd is { } last && match.Season is { } s && match.Number is { } n
                    ? Enumerable.Range(n + 1, Math.Max(0, last - n)).Select(more => byNumber.GetValueOrDefault((s, more))?.Name).Where(t => !string.IsNullOrWhiteSpace(t))
                    : [];
                episode.Title = string.Join(" / ", end.Prepend(name).Distinct());
            }

            episode.Overview = PlainText(match.Summary);
            episode.ReleaseDate = DateOnly.TryParse(match.Airdate, CultureInfo.InvariantCulture, out var aired) ? aired : null;
            episode.RuntimeMinutes = match.Runtime ?? item.RuntimeMinutes;
            episode.ThumbUrl = match.Image?.Original ?? match.Image?.Medium;
            episode.BackdropUrl = item.BackdropUrl;
            episode.AccentColor = item.AccentColor;
            episode.ArrId = null;
            episode.MetadataSource = TvMaze;
            episode.MetadataUpdatedAt = DateTime.UtcNow;
        }
    }

    /// <summary>TVmaze's summaries are HTML ("&lt;p&gt;A &lt;b&gt;show&lt;/b&gt;…&lt;/p&gt;").</summary>
    public static string? PlainText(string? html) =>
        html is null ? null : NullIfEmpty(System.Net.WebUtility.HtmlDecode(HtmlTag().Replace(html.Replace("</p>", "\n\n", StringComparison.OrdinalIgnoreCase), string.Empty)).Trim());

    [System.Text.RegularExpressions.GeneratedRegex("<[^>]*>")]
    private static partial System.Text.RegularExpressions.Regex HtmlTag();

    // ------------------------------------------------------------------ Home videos

    /// <summary>
    /// Home videos are never looked up: what's beside them (a poster.jpg, a .nfo, an episode's -thumb.jpg) is used, and
    /// a frame from the video stands in for whatever isn't there.
    /// </summary>
    private async Task<MetadataRefreshResult> RefreshHomeVideosAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var also = await WithinAsync(libraryId, MediaKind.Series, within, ct).ConfigureAwait(false);
        var items = await Stale(libraryId, MediaKind.Series, force, also).Include(i => i.Genres).ToListAsync(ct).ConfigureAwait(false);
        var fromLocal = 0;
        foreach (var item in items)
        {
            var local = item.Path is { } path ? LocalFiles.ForSeries(path) : LocalArt.None;
            await ApplyLocalOnlyAsync(item, local, ct).ConfigureAwait(false);
            await ApplyLocalSeasonsAsync(item, ct).ConfigureAwait(false);
            await ApplyFramesAsync(item, ct).ConfigureAwait(false);
            fromLocal += local.IsEmpty ? 0 : 1;
            item.MetadataUpdatedAt = DateTime.UtcNow;
            await SaveEveryAsync(items.IndexOf(item) + 1, ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(0, 0, 0, fromLocal);
    }

    /// <summary>
    /// A frame a little way into each video without a still of its own, and the first video's for a folder (season)
    /// or show without a poster.
    /// </summary>
    private async Task ApplyFramesAsync(MediaItem series, CancellationToken ct)
    {
        var seasons = await db.MediaItems.Where(i => i.ParentId == series.Id && i.Kind == MediaKind.Season).OrderBy(i => i.SeasonNumber).ToListAsync(ct).ConfigureAwait(false);
        var seasonIds = seasons.Select(s => s.Id).ToList();
        var videos = await db.MediaItems.Where(i => i.ParentId != null && seasonIds.Contains(i.ParentId.Value)).OrderBy(i => i.EpisodeNumber).ToListAsync(ct).ConfigureAwait(false);
        var videoIds = videos.Select(v => v.Id).ToList();
        var files = (await db.MediaFiles.Where(f => videoIds.Contains(f.MediaItemId)).Select(f => new { f.MediaItemId, f.Path, f.DurationSeconds }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(f => f.MediaItemId).ToDictionary(g => g.Key, g => g.OrderBy(f => f.Path, StringComparer.Ordinal).First());

        foreach (var video in videos)
        {
            if (files.TryGetValue(video.Id, out var file) && (video.ThumbUrl is null || ArtworkUrl.TryParseFrame(video.ThumbUrl, out _, out _)))
            {
                // A tenth of the way in, past a fade from black, but no further than a minute.
                video.ThumbUrl = ArtworkUrl.Frame(file.Path, Math.Min(60, (file.DurationSeconds ?? 0) / 10));
            }

            video.MetadataUpdatedAt = DateTime.UtcNow;
        }

        foreach (var season in seasons)
        {
            season.PosterUrl ??= videos.FirstOrDefault(v => v.ParentId == season.Id)?.ThumbUrl;
            season.MetadataUpdatedAt = DateTime.UtcNow;
        }

        if (series.PosterUrl is null && seasons.Select(s => s.PosterUrl).FirstOrDefault(p => p is not null) is { } first)
        {
            await SetArtworkAsync(series, first, series.BackdropUrl ?? first, series.LogoUrl, ct).ConfigureAwait(false);
        }

        // A still's address is its video's path, the same when the video is replaced: its ?v= moves on at every refresh
        // (as every picture's used to), so a new still isn't hidden by a cached old one.
        foreach (var shown in videos.Concat(seasons).Append(series))
        {
            if (new[] { shown.PosterUrl, shown.BackdropUrl, shown.ThumbUrl }.Any(url => ArtworkUrl.TryParseFrame(url, out _, out _)))
            {
                shown.ArtworkChangedAt = DateTime.UtcNow;
            }
        }
    }

    // ------------------------------------------------------------------ Local files

    /// <summary>
    /// A film or show neither Radarr nor Sonarr matched: what's beside it fills it in. Artwork they gave before stays
    /// (they may only be out of reach for now); local artwork that's since been deleted goes.
    /// </summary>
    private async Task ApplyLocalOnlyAsync(MediaItem item, LocalArt local, CancellationToken ct)
    {
        ForgetScoresIfRematched(item);
        ApplyNfo(item, local.Nfo);
        if (NfoGenres(item, local) is { } genres)
        {
            await SetGenresAsync(item, genres, ct).ConfigureAwait(false);
        }

        await SetArtworkAsync(item, local.Poster ?? NotLocal(item.PosterUrl), local.Backdrop ?? NotLocal(item.BackdropUrl), local.Logo ?? NotLocal(item.LogoUrl), ct)
            .ConfigureAwait(false);
        if (item.MetadataSource is null || item.MetadataSource == Local)
        {
            item.MetadataSource = local.IsEmpty ? null : Local;
        }
    }

    /// <summary>
    /// What a .nfo says wins over Radarr and Sonarr: someone wrote it for this copy. A match fixed by hand wins over the
    /// .nfo, which was likely what got it wrong.
    /// </summary>
    private static void ApplyNfo(MediaItem item, NfoInfo? nfo)
    {
        if (nfo is null || item.MatchLocked)
        {
            return;
        }

        if (nfo.Title is { } title)
        {
            item.Title = title;
            item.SortTitle = SortTitle.For(title);
        }

        item.OriginalTitle = nfo.OriginalTitle is { } original ? (original != item.Title ? original : null) : item.OriginalTitle;
        item.Year = nfo.Year ?? item.Year;
        item.Overview = nfo.Plot ?? item.Overview;
        item.ContentRating = nfo.ContentRating ?? item.ContentRating;
        // The file's own rating is someone's choice, and has the last word.
        item.StrictestRating = nfo.ContentRating is null ? item.StrictestRating : null;
        item.Studio = nfo.Studio ?? item.Studio;
        item.RuntimeMinutes = nfo.RuntimeMinutes ?? item.RuntimeMinutes;
        item.ReleaseDate = nfo.Premiered ?? item.ReleaseDate;
        item.CommunityRating = nfo.Rating ?? item.CommunityRating;
        item.ImdbRating = nfo.ImdbRating ?? item.ImdbRating;
    }

    private static IReadOnlyList<string>? NfoGenres(MediaItem item, LocalArt local) =>
        !item.MatchLocked && local.Nfo is { Genres.Count: > 0 } nfo ? nfo.Genres : null;

    private static string? NotLocal(string? url) => ArtworkUrl.IsLocal(url) ? null : url;

    /// <summary>Season posters, and episodes' .nfo files and stills, from beside them; over Sonarr's.</summary>
    private async Task ApplyLocalSeasonsAsync(MediaItem series, CancellationToken ct)
    {
        if (series.Path is not { } folder || !Directory.Exists(folder))
        {
            return;
        }

        var seasons = await db.MediaItems.Where(i => i.ParentId == series.Id && i.Kind == MediaKind.Season).ToListAsync(ct).ConfigureAwait(false);
        foreach (var season in seasons)
        {
            // Without a poster of its own, a season shows the show's. A home video's "season" is a folder of its own.
            var own = season.Path is { } seasonFolder && seasonFolder != folder ? LocalFiles.FolderPoster(seasonFolder) : null;
            season.PosterUrl = own ?? (season.Path is null ? LocalFiles.SeasonPoster(folder, season.SeasonNumber ?? 0) : null)
                ?? (ArtworkUrl.IsLocal(season.PosterUrl) && season.PosterUrl != series.PosterUrl ? null : season.PosterUrl);
        }

        var seasonIds = seasons.Select(s => s.Id).ToList();
        var episodes = await db.MediaItems.Where(i => i.ParentId != null && seasonIds.Contains(i.ParentId.Value)).ToListAsync(ct).ConfigureAwait(false);
        var episodeIds = episodes.Select(e => e.Id).ToList();
        var files = (await db.MediaFiles.Where(f => episodeIds.Contains(f.MediaItemId)).Select(f => new { f.MediaItemId, f.Path }).ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(f => f.MediaItemId).ToDictionary(g => g.Key, g => g.Select(f => f.Path).Order(StringComparer.Ordinal).First());
        var listings = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);
        foreach (var episode in episodes)
        {
            if (!files.TryGetValue(episode.Id, out var file) || Path.GetDirectoryName(file) is not { } directory)
            {
                continue;
            }

            if (!listings.TryGetValue(directory, out var listing))
            {
                listing = listings[directory] = LocalFiles.Listing(directory);
            }

            var (nfo, thumb) = LocalFiles.ForEpisode(file, listing);
            episode.ThumbUrl = thumb ?? NotLocal(episode.ThumbUrl);
            if (nfo is not null)
            {
                episode.Title = nfo.Title ?? episode.Title;
                episode.Overview = nfo.Plot ?? episode.Overview;
                episode.ReleaseDate = nfo.Premiered ?? episode.ReleaseDate;
                episode.RuntimeMinutes = nfo.RuntimeMinutes ?? episode.RuntimeMinutes;
            }
        }
    }

    /// <summary>
    /// People shared across the whole refresh, so an actor in two films processed before the next save
    /// isn't created twice. Only the people with <paramref name="keys"/> not looked up yet are read, not everyone.
    /// </summary>
    private async Task<Dictionary<string, Person>> PeopleAsync(bool tmdb, IEnumerable<string> keys, CancellationToken ct)
    {
        var map = tmdb ? _peopleByTmdb ??= new(StringComparer.Ordinal) : _peopleByTvMaze ??= new(StringComparer.Ordinal);
        var wanted = keys.Where(k => !map.ContainsKey(k)).Distinct(StringComparer.Ordinal).ToList();
        if (wanted.Count == 0)
        {
            return map;
        }

        var people = tmdb
            ? await db.People.Where(p => wanted.Contains(p.TmdbId!)).ToListAsync(ct).ConfigureAwait(false)
            : await db.People.Where(p => wanted.Contains(p.TvMazeId!)).ToListAsync(ct).ConfigureAwait(false);
        foreach (var person in people)
        {
            map.TryAdd(tmdb ? person.TmdbId! : person.TvMazeId!, person);
        }

        return map;
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
        if (_scannedOnly && (!force || _forcedWithin))
        {
            // Never refreshed, or set to be on the next scan (PathMappingFinder.RematchAsync: a new mapping or connection
            // may link them).
            var rematch = DateTime.UnixEpoch;
            return db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == kind
                && (also.Contains(i.Id) || i.MetadataUpdatedAt == null || i.MetadataUpdatedAt <= rematch));
        }

        if (_dueAtMost is { } atMost)
        {
            var notNow = _notNow.ToList();
            return Due(libraryId, kind, force, also).Where(i => !notNow.Contains(i.Id)).OrderBy(i => i.MetadataUpdatedAt).ThenBy(i => i.Id).Take(atMost);
        }

        return Due(libraryId, kind, force, also);
    }

    private IQueryable<MediaItem> Due(Guid libraryId, MediaKind kind, bool force, List<Guid> also)
    {
        var now = DateTime.UtcNow;
        var cutoff = now - StaleAfter;
        var retryCutoff = now - RetryUnmatchedAfter;
        // Items that didn't match last time, or were only found by a title search (or, for music, only have their tags),
        // are retried daily (a rename or new *arr entry may fix them). A new *arr entry also arrives as a webhook, whose
        // folder is refreshed straight away.
        return db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == kind
            && (force || also.Contains(i.Id) || i.MetadataUpdatedAt == null || i.MetadataUpdatedAt < cutoff
                || ((i.PosterUrl == null || ((i.MetadataSource == RadarrLookup || i.MetadataSource == SonarrLookup || i.MetadataSource == TmdbSearch
                        || i.MetadataSource == TvMazeSearch || i.MetadataSource == Tags) && !i.MatchLocked))
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
            ? i => i.Id == within[0] && (own(i) is null || (i.MetadataSource is RadarrLookup or SonarrLookup or TmdbSearch or TvMazeSearch && !i.MatchLocked))
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
    /// <returns>What they have, and whether every instance asked answered (a title gone from one, a 404, counts as an answer).</returns>
    private async Task<(List<(ArrInstance Instance, T Entry)> Known, bool AllAnswered)> KnownAsync<T>(
        List<ArrInstance> instances,
        IReadOnlyList<MediaItem>? only,
        Func<MediaItem, string?> externalId,
        Func<ArrInstance, CancellationToken, Task<IReadOnlyList<T>>> all,
        Func<ArrInstance, int, CancellationToken, Task<T>> byArrId,
        Func<ArrInstance, int, CancellationToken, Task<IReadOnlyList<T>>> byExternalId,
        CancellationToken ct)
    {
        var known = new List<(ArrInstance Instance, T Entry)>();
        var allAnswered = true;
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
                        allAnswered &= ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound };
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
                        allAnswered = false;
                    }
                }
            }

            return (known, allAnswered);
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
                allAnswered = false;
            }
        }

        return (known, allAnswered);

        int? ExternalId(MediaItem item) =>
            int.TryParse(externalId(item), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>
    /// Whether a title linked to a Radarr or Sonarr entry is missing from what was fetched. When an instance didn't
    /// answer, that may only mean it's down, so the title is left as it is (unstamped, so it's tried again next time).
    /// </summary>
    private static bool Unconfirmed(MediaItem item, IEnumerable<(ArrInstance Instance, int Id)> known) =>
        item.ArrId is { } arrId && !known.Any(k => k.Id == arrId && k.Instance.Name == item.MetadataSource);

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

    /// <summary>
    /// A cast or crew photo from a film's or show's source, unless an admin fixed the person's by hand or it's from
    /// their own folder or an upload. People are loaded once at the start of a refresh, so before a photo changes the
    /// person is read again: an upload or lock made since wins.
    /// </summary>
    private async Task SetPersonPhotoAsync(Person person, string? url, string source, CancellationToken ct)
    {
        if (url is null || !PersonSources.PhotoReplaceable(person))
        {
            return;
        }

        if (person.ImageUrl != url && db.Entry(person).State is not EntityState.Added
            && await db.People.AsNoTracking().Where(p => p.Id == person.Id)
                .Select(p => new { p.InfoLocked, p.PhotoSource, p.ImageUrl, p.ImageChangedAt, p.ImageStamp }).FirstOrDefaultAsync(ct).ConfigureAwait(false) is { } now
            && (now.InfoLocked || now.PhotoSource is PersonSources.Folder or PersonSources.Upload or PersonSources.Manual))
        {
            // Changed by hand meanwhile: take what's saved, so this refresh's save keeps it.
            person.InfoLocked = now.InfoLocked;
            person.PhotoSource = now.PhotoSource;
            person.ImageUrl = now.ImageUrl;
            person.ImageChangedAt = now.ImageChangedAt;
            person.ImageStamp = now.ImageStamp;
            return;
        }

        PersonSources.SetPhoto(person, url, source, DateTime.UtcNow);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateOnly? DateOnlyOf(DateTime? value) => value is { } v ? DateOnly.FromDateTime(v) : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Instance} is unavailable: {Error}")]
    private partial void LogArrUnavailable(string instance, string error);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not refresh metadata for {Title}")]
    private partial void LogItemFailed(Exception exception, string title);

    [LoggerMessage(Level = LogLevel.Information, Message = "Metadata lookup for {Term} failed: {Error}")]
    private partial void LogLookupFailed(string term, string error);

    [LoggerMessage(Level = LogLevel.Information, Message = "Seerr didn't give the scores for {Title} ({Error}); leaving it alone for the rest of this refresh: {GivingUp}")]
    private partial void LogSeerrRatingsFailed(string title, string error, bool givingUp);
}
