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

    private Dictionary<string, Genre>? _genres;
    private Dictionary<string, Person>? _peopleByTmdb;
    private Dictionary<string, Person>? _peopleByTvMaze;

    public Task<MetadataRefreshResult> RefreshLibraryAsync(Library library, bool force, CancellationToken ct) =>
        RefreshLibraryAsync(library, force, [], ct);

    /// <param name="library">The library to refresh.</param>
    /// <param name="force">Refresh everything, not just what's new or stale.</param>
    /// <param name="within">Folders just scanned: the films and series in them are refreshed even if they're not stale.</param>
    /// <param name="ct">Stops the refresh.</param>
    public async Task<MetadataRefreshResult> RefreshLibraryAsync(Library library, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(within);
        return library.Type switch
        {
            LibraryType.Movies => await RefreshMoviesAsync(library.Id, force, within, ct).ConfigureAwait(false),
            LibraryType.TvShows => await RefreshSeriesAsync(library.Id, force, within, ct).ConfigureAwait(false),
            LibraryType.Music => await RefreshMusicAsync(library.Id, force, ct).ConfigureAwait(false),
            _ => new MetadataRefreshResult(0, 0, 0),
        };
    }

    // ------------------------------------------------------------------ Movies

    private async Task<MetadataRefreshResult> RefreshMoviesAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        // Nothing due: don't download Radarr's whole library to find out.
        var also = await WithinAsync(libraryId, MediaKind.Movie, within, ct).ConfigureAwait(false);
        var items = await Stale(libraryId, MediaKind.Movie, force, also).Include(i => i.Genres).Include(i => i.Credits).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var radarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Radarr).ToList();
        var known = new List<(ArrInstance Instance, RadarrMovie Movie)>();
        foreach (var radarr in radarrs)
        {
            try
            {
                known.AddRange((await arr.GetMoviesAsync(radarr, ct).ConfigureAwait(false)).Select(m => (radarr, m)));
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                LogArrUnavailable(radarr.Name, ex.Message);
            }
        }

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
        if ((Find(byPath, item.Path) ?? Find(byImdb, item.ImdbId) ?? Find(byTmdb, item.TmdbId)) is (var instance, var movie))
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
        var term = item.ImdbId is { } imdb ? $"imdb:{imdb}"
            : item.TmdbId is { } tmdb ? $"tmdb:{tmdb}"
            : $"{item.Title} {item.Year}".Trim();
        try
        {
            var results = await arr.LookupMoviesAsync(radarr, term, ct).ConfigureAwait(false);
            return results.FirstOrDefault(m => item.Year is null || Math.Abs(m.Year - item.Year.Value) <= 1);
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
        item.MetadataSource = instance?.Name ?? "Radarr lookup";
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

    /// <summary>Music metadata comes from tags and local artwork at scan time; this adds ambient colours.</summary>
    private async Task<MetadataRefreshResult> RefreshMusicAsync(Guid libraryId, bool force, CancellationToken ct)
    {
        var items = await db.MediaItems
            .Where(i => i.LibraryId == libraryId && (i.Kind == MediaKind.Album || i.Kind == MediaKind.Artist) && i.PosterUrl != null && (force || i.AccentColor == null))
            .ToListAsync(ct).ConfigureAwait(false);
        foreach (var item in items)
        {
            item.AccentColor = await images.AccentColorAsync(item.PosterUrl!, ct).ConfigureAwait(false);
            item.MetadataUpdatedAt = DateTime.UtcNow;
            item.MetadataSource = "Tags";
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new MetadataRefreshResult(0, items.Count, 0);
    }

    // ------------------------------------------------------------------ TV

    private async Task<MetadataRefreshResult> RefreshSeriesAsync(Guid libraryId, bool force, IReadOnlyCollection<string> within, CancellationToken ct)
    {
        var also = await WithinAsync(libraryId, MediaKind.Series, within, ct).ConfigureAwait(false);
        var items = await Stale(libraryId, MediaKind.Series, force, also).Include(i => i.Genres).Include(i => i.Credits).ToListAsync(ct).ConfigureAwait(false);
        if (items.Count == 0)
        {
            return new MetadataRefreshResult(0, 0, 0);
        }

        var sonarrs = settings.ArrInstances.Where(a => a.Kind == ArrKind.Sonarr).ToList();
        var known = new List<(ArrInstance Instance, SonarrSeries Series)>();
        foreach (var sonarr in sonarrs)
        {
            try
            {
                known.AddRange((await arr.GetSeriesAsync(sonarr, ct).ConfigureAwait(false)).Select(s => (sonarr, s)));
            }
            catch (Exception ex) when (IsNetworkFailure(ex, ct))
            {
                LogArrUnavailable(sonarr.Name, ex.Message);
            }
        }

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
        if ((Find(byPath, item.Path) ?? Find(byTvdb, item.TvdbId) ?? Find(byImdb, item.ImdbId)) is (var instance, var series))
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
            return results.FirstOrDefault(s => item.Year is null || s.Year == 0 || Math.Abs(s.Year - item.Year.Value) <= 1);
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
        item.MetadataSource = instance?.Name ?? "Sonarr lookup";
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
        // Items that didn't match last time are retried daily (a rename or new *arr entry may fix them). A new *arr
        // entry also arrives as a webhook, whose folder is refreshed straight away.
        return db.MediaItems.Where(i => i.LibraryId == libraryId && i.Kind == kind
            && (force || also.Contains(i.Id) || i.MetadataUpdatedAt == null || i.MetadataUpdatedAt < cutoff
                || (i.PosterUrl == null && i.MetadataUpdatedAt < retryCutoff)));
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
    /// which only means "stop" when <paramref name="ct"/> was cancelled.
    /// </summary>
    private static bool IsNetworkFailure(Exception ex, CancellationToken ct) =>
        ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested);

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
