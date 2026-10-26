using System.Globalization;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints;

/// <param name="Playlists">For the "playlists" style: the profile's playlists, which aren't items (Items is empty then).</param>
/// <param name="Mixes">For the "mixes" style: the profile's daily mixes, which aren't items either.</param>
/// <param name="Channels">For the "live" style: the profile's favourite channels with what's on, which aren't items either.</param>
public sealed record HomeRowDto(string Id, string Title, string Style, IReadOnlyList<ItemSummaryDto> Items, string? Link = null,
    IReadOnlyList<Playlists.PlaylistSummaryDto>? Playlists = null, IReadOnlyList<MixSummaryDto>? Mixes = null,
    IReadOnlyList<LiveTv.LiveChannelDto>? Channels = null)
{
    /// <summary>Whether there's anything to show: a row of items, of playlists, of mixes or of channels.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool HasContent => Items.Count > 0 || Playlists is { Count: > 0 } || Mixes is { Count: > 0 } || Channels is { Count: > 0 };
}

/// <param name="Resume">What the first featured title's Play button resumes (the episode, for a show), when it comes from Continue Watching.</param>
public sealed record HomeDto(IReadOnlyList<ItemSummaryDto> Featured, IReadOnlyDictionary<Guid, string?> Overviews, IReadOnlyList<HomeRowDto> Rows, IReadOnlyList<UpcomingDto> Upcoming, int Downloading, ItemSummaryDto? Resume = null);

public sealed record SearchPersonDto(Guid Id, string Name, string? Image, int Credits);

/// <summary>A track that matched a search, with the name of the album it's on (its summary only names the artist).</summary>
public sealed record SearchTrackDto(ItemSummaryDto Item, string? Album);

/// <summary>Titles are films, shows and artists (everything top-level); albums and tracks come grouped on their own.</summary>
public sealed record SearchResultsDto(
    IReadOnlyList<ItemSummaryDto> Titles,
    IReadOnlyList<ItemSummaryDto> Episodes,
    IReadOnlyList<SearchPersonDto> People,
    IReadOnlyList<string> Genres,
    IReadOnlyList<ItemSummaryDto> Albums,
    IReadOnlyList<SearchTrackDto> Tracks);

public sealed record GenreDto(string Name, int Movies, int Shows, string? Backdrop);

public static partial class DiscoveryEndpoints
{
    private const int RowSize = 20;

    public static RouteGroupBuilder MapDiscoveryEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/home", async (FlexarrDbContext db, CurrentProfile profile, IServiceProvider services, CancellationToken ct) =>
        {
            var me = profile.Id;
            var options = services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions>>().CurrentValue;
            var arr = services.GetRequiredService<Flexarr.Integrations.Arr.ArrClient>();
            var cache = services.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>();
            var rows = new List<HomeRowDto>();
            var states = db.PlaybackStates.Where(p => p.UserId == me);
            var topLevel = db.MediaItems.Where(i => i.ParentId == null);
            var unwatchedMovies = topLevel.Where(i => i.Kind == MediaKind.Movie && !i.PlaybackStates.Any(p => p.UserId == me && p.Watched));

            var continueWatching = await ContinueWatchingAsync(db, me, RowSize, null, ct);
            if (continueWatching.Count > 0)
            {
                rows.Add(new HomeRowDto("continue", "Continue watching", "wide", continueWatching));
            }

            // The profile's favourite channels and what's on them; nothing until some are starred.
            if (options.LiveTv?.Account is not null && LayoutEndpoints.IsOn(options.Layout.Home, "live-tv"))
            {
                var channels = await LiveTv.LiveTvEndpoints.FavouritesOnNowAsync(db, me, DateTime.UtcNow, ct);
                rows.Add(new HomeRowDto("live-tv", "Live TV", "live", [], "/livetv", Channels: channels));
            }

            var libraries = await db.Libraries.OrderBy(l => l.Name).ToListAsync(ct);
            foreach (var library in libraries.Where(l => l.Type is LibraryType.Movies or LibraryType.TvShows))
            {
                var movies = library.Type == LibraryType.Movies;
                // "in {library}" only when there's more than one library of the kind to tell apart.
                var title = $"Latest unwatched {(movies ? "films" : "shows")}"
                    + (libraries.Count(l => l.Type == library.Type) > 1 ? $" in {library.Name}" : "");
                rows.Add(new HomeRowDto($"recent-{library.Id}", title, "poster",
                    await LatestUnwatched(topLevel.Where(i => i.LibraryId == library.Id), movies, me).Take(RowSize).Summaries(me).ToListAsync(ct),
                    $"/library/{library.Id}?sort=added"));
            }

            rows.Add(new HomeRowDto("top-movies", "Top 10 films in your library", "top10",
                await TopRated(topLevel.Where(i => i.Kind == MediaKind.Movie)).Take(10).Summaries(me).ToListAsync(ct)));
            rows.Add(new HomeRowDto("top-shows", "Top 10 shows in your library", "top10",
                await TopRated(topLevel.Where(i => i.Kind == MediaKind.Series)).Take(10).Summaries(me).ToListAsync(ct)));

            // Music this profile has been playing, and the newest albums, when it can see a music library at all.
            var musicLibraries = libraries.Where(l => l.Type == LibraryType.Music).ToList();
            if (musicLibraries.Count > 0)
            {
                rows.AddRange(await MusicDiscovery.RowsAsync(db, cache, me, null, RowSize, ct));
                // The row opens the library when there's only one to open.
                var link = musicLibraries.Count == 1 ? $"/library/{musicLibraries[0].Id}?view=all&sort=added" : null;
                rows.Add(new HomeRowDto("music-new", "New albums", "poster", await MusicDiscovery.NewAlbumsAsync(db, me, null, RowSize, ct), link));
            }

            // "Because you watched": share genres and cast with the latest thing finished.
            var lastWatched = await states.Where(p => p.Watched && (p.MediaItem!.Kind == MediaKind.Movie || p.MediaItem.Kind == MediaKind.Episode)).OrderByDescending(p => p.LastPlayedAt)
                .Select(p => p.MediaItem!.Kind == MediaKind.Episode ? p.MediaItem.Parent!.Parent! : p.MediaItem)
                .FirstOrDefaultAsync(ct);
            if (lastWatched is not null)
            {
                var similar = await SimilarAsync(db, lastWatched.Id, me, ct);
                if (similar.Count >= 4)
                {
                    rows.Add(new HomeRowDto("because", $"Because you watched {lastWatched.Title}", "poster", similar));
                }
            }

            // A few genre rows, rotating daily so the home screen doesn't go stale.
            var genres = await db.Genres.Select(g => new { g.Name, Count = g.Items.Count(i => i.ParentId == null) })
                .Where(g => g.Count >= 12).OrderByDescending(g => g.Count).Take(12).ToListAsync(ct);
            var day = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
            foreach (var genre in genres.OrderBy(g => (g.Name.GetHashCode(StringComparison.Ordinal) ^ day) & int.MaxValue).Take(3))
            {
                var picks = await topLevel.Where(i => i.Genres.Any(g => g.Name == genre.Name) && !i.PlaybackStates.Any(p => p.UserId == me && p.Watched))
                    .OrderByDescending(i => i.CommunityRating).Take(RowSize).Summaries(me).ToListAsync(ct);
                rows.Add(new HomeRowDto($"genre-{genre.Name}", genre.Name, "poster", picks, $"/discover?genre={Uri.EscapeDataString(genre.Name)}"));
            }

            rows.Add(new HomeRowDto("gems", "Highly rated, not yet watched", "poster",
                await TopRated(unwatchedMovies).Skip(10).Take(RowSize).Summaries(me).ToListAsync(ct)));

            // Featured: well-rated things with full artwork, unwatched first, changing daily.
            var rotation = await FeaturedAsync(topLevel, me, day, ct);
            (rotation, var resume) = await LeadWithContinueWatchingAsync(db, me, continueWatching, rotation, ct);
            var overviews = await OverviewsAsync(db, rotation, ct);
            // Sequential: these share the DbContext with the queries above, which isn't thread-safe.
            // Coming soon is a bonus; the home page must render even if it fails.
            List<UpcomingDto> upcoming = [];
            var downloading = 0;
            // Switched off in Settings: don't ask Sonarr and Radarr at all.
            if (LayoutEndpoints.IsOn(options.Layout.Home, "upcoming"))
            {
                try
                {
                    upcoming = (await UpcomingEndpoints.UpcomingAsync(10, options, arr, cache, db, null, ct)).Where(u => !u.Available && u.Date >= DateTime.UtcNow.AddHours(-12)).Take(16).ToList();
                    downloading = (await UpcomingEndpoints.DownloadsAsync(options, arr, cache, db, null, ct)).Count;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogUpcomingFailed(services.GetRequiredService<ILoggerFactory>().CreateLogger("Flexarr.Home"), ex);
                }
            }

            // Collections chosen for Home (household ones and this profile's own).
            rows.AddRange(await Collections.CollectionEndpoints.HomeRowsAsync(db, profile, RowSize, ct));

            // Coming soon is a row like the others (its titles travel in Upcoming), so it can be moved or switched off.
            rows.Add(new HomeRowDto("upcoming", "Coming soon", "upcoming", []));
            var shown = rows.Where(r => r.HasContent || (r.Id == "upcoming" && upcoming.Count > 0));
            return new HomeDto(rotation, overviews, LayoutEndpoints.Arrange(shown, options.Layout.Home), upcoming, downloading, resume);
        }).WithTags("Discovery");

        api.MapGet("/home/continue", async (int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await ContinueWatchingAsync(db, profile.Id, Math.Clamp(take ?? 20, 1, 50), null, ct)).WithTags("Discovery");

        // A library's own home page: what you're watching in it, what's new, the best of it, and its main genres.
        api.MapGet("/libraries/{id:guid}/browse", async (Guid id, FlexarrDbContext db, CurrentProfile profile,
            Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions> options, Microsoft.Extensions.Caching.Memory.IMemoryCache cache, CancellationToken ct) =>
        {
            if (await db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.Id == id, ct) is not { } library)
            {
                return Results.NotFound();
            }

            var me = profile.Id;
            var all = $"/library/{id}?view=all";
            if (library.Type == LibraryType.Podcasts)
            {
                return Results.Ok(new HomeDto([], new Dictionary<Guid, string?>(), [], [], 0)); // Podcasts has its own pages
            }

            if (library.Type == LibraryType.Music)
            {
                // A music library's home: what you've been playing, then the newest albums. No hero: album covers are
                // square, and the rows are the point.
                var musicRows = await MusicDiscovery.RowsAsync(db, cache, me, id, RowSize, ct);
                musicRows.Add(new HomeRowDto("recent", "New albums", "poster", await MusicDiscovery.NewAlbumsAsync(db, me, id, RowSize, ct), $"{all}&sort=added"));
                return Results.Ok(new HomeDto([], new Dictionary<Guid, string?>(), musicRows.Where(r => r.HasContent).ToList(), [], 0));
            }

            var movies = library.Type == LibraryType.Movies;
            var noun = movies ? "films" : "shows";
            var topLevel = db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null);
            var unwatched = topLevel.Where(i => !i.PlaybackStates.Any(p => p.UserId == me && p.Watched));
            var rows = new List<HomeRowDto>();

            // Continue Watching, narrowed to this library.
            var continueWatching = await ContinueWatchingAsync(db, me, RowSize, id, ct);
            rows.Add(new HomeRowDto("continue", "Continue watching", "wide", continueWatching));

            rows.Add(new HomeRowDto("recent", movies ? "Latest unwatched films" : "Latest unwatched shows", "poster",
                await LatestUnwatched(topLevel, movies, me).Take(RowSize).Summaries(me).ToListAsync(ct), $"{all}&sort=added"));
            rows.Add(new HomeRowDto("top", $"Top 10 {noun} in {library.Name}", "top10", await TopRated(topLevel).Take(10).Summaries(me).ToListAsync(ct)));
            rows.Add(new HomeRowDto("released", movies ? "Newest releases" : "Newest shows", "poster",
                await topLevel.Where(i => i.Year != null).OrderByDescending(i => i.Year).ThenByDescending(i => i.Popularity).Take(RowSize).Summaries(me).ToListAsync(ct), $"{all}&sort=year"));
            if (movies)
            {
                rows.Add(new HomeRowDto("gems", "Highly rated, not yet watched", "poster", await TopRated(unwatched).Skip(10).Take(RowSize).Summaries(me).ToListAsync(ct), $"{all}&sort=rating"));
            }
            else
            {
                // Watched is recorded per episode, so a show counts as unwatched only if no episode has been started.
                var started = db.PlaybackStates.Where(p => p.UserId == me && p.MediaItem!.Kind == MediaKind.Episode).Select(p => p.MediaItem!.Parent!.ParentId).Distinct();
                rows.Add(new HomeRowDto("gems", "Highly rated, not yet started", "poster",
                    await TopRated(topLevel.Where(s => !started.Contains(s.Id))).Take(RowSize).Summaries(me).ToListAsync(ct)));
            }

            // The library's biggest genres, biggest first, unwatched and best-rated first within each.
            var genres = await db.Genres.Select(g => new { g.Name, Count = g.Items.Count(i => i.LibraryId == id && i.ParentId == null) })
                .Where(g => g.Count >= 8).OrderByDescending(g => g.Count).Take(8).ToListAsync(ct);
            foreach (var genre in genres)
            {
                var picks = await TopRated(topLevel.Where(i => i.Genres.Any(g => g.Name == genre.Name)))
                    .ThenBy(i => i.PlaybackStates.Any(p => p.UserId == me && p.Watched)).Take(RowSize).Summaries(me).ToListAsync(ct);
                rows.Add(new HomeRowDto($"genre-{genre.Name}", genre.Name, "poster", picks, $"{all}&genre={Uri.EscapeDataString(genre.Name)}"));
            }

            var day = DateOnly.FromDateTime(DateTime.UtcNow).DayNumber;
            var rotation = await FeaturedAsync(topLevel, me, day, ct);
            (rotation, var resume) = await LeadWithContinueWatchingAsync(db, me, continueWatching, rotation, ct);
            var overviews = await OverviewsAsync(db, rotation, ct);

            var layout = movies ? options.CurrentValue.Layout.Movies : options.CurrentValue.Layout.Shows;
            return Results.Ok(new HomeDto(rotation, overviews, LayoutEndpoints.Arrange(rows.Where(r => r.Items.Count > 0), layout), [], 0, resume));
        }).WithTags("Discovery");

        api.MapGet("/items/{id:guid}/similar", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await SimilarAsync(db, id, profile.Id, ct)).WithTags("Discovery");

        // Artist radio: a station of about fifty tracks seeded from an artist (or an album's artist). The same `seed`
        // gives the same station, so a client can offer "play it again"; without one, every station is new.
        api.MapGet("/items/{id:guid}/radio", async (Guid id, int? seed, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var item = await db.MediaItems.Where(i => i.Id == id).Select(i => new { i.Kind, i.ParentId }).FirstOrDefaultAsync(ct);
            var artistId = item switch
            {
                { Kind: MediaKind.Artist } => id,
                { Kind: MediaKind.Album, ParentId: { } artist } => artist,
                _ => (Guid?)null,
            };
            if (artistId is null)
            {
                return Results.NotFound();
            }

            var candidates = await MusicDiscovery.RadioCandidatesAsync(db, artistId.Value, profile.Id, ct);
            var picks = MusicDiscovery.PickRadio(artistId.Value, candidates, seed ?? Random.Shared.Next());
            return Results.Ok(await MusicDiscovery.InOrderAsync(db, picks, profile.Id, ct));
        }).WithTags("Discovery");

        api.MapGet("/search", async (string q, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var term = q.Trim();
            if (term.Length == 0)
            {
                return new SearchResultsDto([], [], [], [], [], []);
            }

            var limit = Math.Clamp(take ?? 12, 1, 50);
            var contains = $"%{term.Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
            var prefix = $"{term.Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";

            // Titles that start with the term rank above titles that merely contain it.
            var titles = await db.MediaItems
                // Podcasts have a search of their own (Apple's directory and the shows followed).
                .Where(i => i.ParentId == null && i.Kind != MediaKind.Podcast
                    && (EF.Functions.Like(i.Title, contains) || (i.OriginalTitle != null && EF.Functions.Like(i.OriginalTitle, contains))))
                .OrderByDescending(i => EF.Functions.Like(i.Title, prefix) || EF.Functions.Like(i.SortTitle, prefix))
                .ThenByDescending(i => i.Popularity ?? i.CommunityRating)
                .Take(limit).Summaries(profile.Id).ToListAsync(ct);
            var episodes = await db.MediaItems.Where(i => i.Kind == MediaKind.Episode && EF.Functions.Like(i.Title, contains))
                .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).Take(8).Summaries(profile.Id).ToListAsync(ct);
            var people = await db.People.Where(p => EF.Functions.Like(p.Name, contains) && p.Credits.Any())
                .OrderByDescending(p => EF.Functions.Like(p.Name, prefix)).ThenByDescending(p => p.Credits.Count)
                .Take(8)
                .Select(p => new SearchPersonDto(p.Id, p.Name, ArtworkUrls.Person(p.Id, p.ImageUrl != null), p.Credits.Count))
                .ToListAsync(ct);
            var genres = await db.Genres.Where(g => EF.Functions.Like(g.Name, contains)).Select(g => g.Name).Take(5).ToListAsync(ct);

            // Albums and tracks by name; their summaries carry the artist and borrow the album's cover.
            var albums = await db.MediaItems.Where(i => i.Kind == MediaKind.Album && EF.Functions.Like(i.Title, contains))
                .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).ThenByDescending(i => i.Year).Take(8).Summaries(profile.Id).ToListAsync(ct);
            var trackIds = await db.MediaItems.Where(i => i.Kind == MediaKind.Track && EF.Functions.Like(i.Title, contains))
                .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).ThenBy(i => i.SortTitle).Take(8)
                .Select(i => new { i.Id, Album = i.Parent!.Title }).ToListAsync(ct);
            var trackItems = await MusicDiscovery.InOrderAsync(db, trackIds.Select(t => t.Id), profile.Id, ct);
            var tracks = trackItems.Select(t => new SearchTrackDto(t, trackIds.First(x => x.Id == t.Id).Album)).ToList();
            return new SearchResultsDto(titles, episodes, people, genres, albums, tracks);
        }).WithTags("Discovery");

        api.MapGet("/genres", async (FlexarrDbContext db, CancellationToken ct) =>
        {
            var genres = await db.Genres
                .Select(g => new
                {
                    g.Name,
                    Movies = g.Items.Count(i => i.Kind == MediaKind.Movie),
                    Shows = g.Items.Count(i => i.Kind == MediaKind.Series),
                    Candidates = g.Items.Where(i => i.BackdropUrl != null && i.CommunityRating != null)
                        .OrderByDescending(i => i.CommunityRating).ThenByDescending(i => i.Popularity)
                        .Select(i => new { i.Id, i.MetadataUpdatedAt }).Take(12).ToList(),
                })
                .Where(g => g.Movies + g.Shows > 0)
                .OrderByDescending(g => g.Movies + g.Shows)
                .ToListAsync(ct);

            // Each genre gets its own cover: big genres share most titles, so skip ones already used.
            var used = new HashSet<Guid>();
            return genres.Select(g =>
            {
                var cover = g.Candidates.FirstOrDefault(c => used.Add(c.Id)) ?? g.Candidates.FirstOrDefault();
                return new GenreDto(g.Name, g.Movies, g.Shows,
                    cover is null ? null : ArtworkUrls.For(cover.Id, cover.MetadataUpdatedAt?.Ticks / 10_000_000 ?? 0, false, true, false, false).Backdrop);
            }).ToList();
        }).WithTags("Discovery");

        api.MapGet("/genres/{name}/items", async (string name, string? kind, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var query = db.MediaItems.Where(i => i.ParentId == null && i.Genres.Any(g => g.Name == name));
            if (Enum.TryParse<MediaKind>(kind, ignoreCase: true, out var k))
            {
                query = query.Where(i => i.Kind == k);
            }

            var total = await query.CountAsync(ct);
            var items = await TopRated(query).Skip(Math.Max(0, skip ?? 0)).Take(Math.Clamp(take ?? 60, 1, 200)).Summaries(profile.Id).ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Discovery");

        return api;
    }

    /// <summary>Partly watched items first, then the next episode of shows in progress, most recent first.</summary>
    /// <param name="libraryId">Only what's in this library (null for everything).</param>
    public static async Task<List<ItemSummaryDto>> ContinueWatchingAsync(FlexarrDbContext db, string profileId, int limit, Guid? libraryId, CancellationToken ct)
    {
        var states = db.PlaybackStates.Where(p => p.UserId == profileId && (libraryId == null || p.MediaItem!.LibraryId == libraryId));
        // Films and episodes only: a track half played is music's business (its Recently played row), and a Play button
        // on an album in the hero would be a different thing from resuming a film.
        var inProgress = await states.Where(p => !p.Watched && p.PositionSeconds > 0 && (p.MediaItem!.Kind == MediaKind.Movie || p.MediaItem.Kind == MediaKind.Episode))
            .OrderByDescending(p => p.LastPlayedAt).Take(limit)
            .Select(p => new { p.MediaItemId, p.LastPlayedAt, SeriesId = p.MediaItem!.Parent != null ? p.MediaItem.Parent.ParentId : null })
            .ToListAsync(ct);

        var since = DateTime.UtcNow.AddDays(-90);
        var finished = await states.Where(p => p.Watched && p.LastPlayedAt > since && p.MediaItem!.Kind == MediaKind.Episode)
            .Select(p => new { p.LastPlayedAt, SeriesId = p.MediaItem!.Parent!.ParentId })
            .ToListAsync(ct);
        var busy = inProgress.Select(p => p.SeriesId).Where(s => s is not null).ToHashSet();
        var shows = finished.Where(p => p.SeriesId is not null && !busy.Contains(p.SeriesId))
            .GroupBy(p => p.SeriesId!.Value).Select(g => (SeriesId: g.Key, At: g.Max(p => p.LastPlayedAt)))
            .OrderByDescending(s => s.At).Take(limit).ToList();

        // Every show's episodes in one query, rather than a few queries per show.
        var episodes = (await PlaybackService.EpisodesAsync(db, profileId, shows.Select(s => s.SeriesId).ToList(), ct)).ToLookup(e => e.SeriesId);
        var upNext = new List<(Guid Id, DateTime At)>();
        foreach (var (seriesId, at) in shows)
        {
            if (PlaybackService.UpNext(episodes[seriesId].ToList()) is { } next)
            {
                upNext.Add((next.Id, at));
            }
        }

        var order = inProgress.Select(p => (Id: p.MediaItemId, At: p.LastPlayedAt)).Concat(upNext)
            .OrderByDescending(x => x.At).Select(x => x.Id).Distinct().Take(limit).ToList();
        var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return order.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }

    /// <summary>Same kind, ranked by shared genres and cast, then rating.</summary>
    public static async Task<List<ItemSummaryDto>> SimilarAsync(FlexarrDbContext db, Guid id, string profileId, CancellationToken ct)
    {
        var source = await db.MediaItems.AsNoTracking().Where(i => i.Id == id)
            .Select(i => new { i.Kind, Genres = i.Genres.Select(g => g.Id).ToList(), People = i.Credits.Where(c => c.Order < 10 || c.Role != CreditRole.Actor).Select(c => c.PersonId).ToList() })
            .FirstOrDefaultAsync(ct);
        if (source is null || source.Genres.Count == 0)
        {
            return [];
        }

        var ranked = await db.MediaItems
            .Where(i => i.ParentId == null && i.Kind == source.Kind && i.Id != id && i.Genres.Any(g => source.Genres.Contains(g.Id)))
            .Select(i => new
            {
                i.Id,
                Score = i.Genres.Count(g => source.Genres.Contains(g.Id)) * 2
                    + i.Credits.Count(c => source.People.Contains(c.PersonId)) * 3
                    + (i.CommunityRating ?? 5) / 2,
            })
            .OrderByDescending(x => x.Score).Take(RowSize).ToListAsync(ct);
        var order = ranked.Select(r => r.Id).ToList();
        var items = await db.MediaItems.Where(i => order.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return order.Select(o => items[o]).ToList();
    }

    /// <summary>
    /// Well-rated titles with full artwork, unwatched first, five of them rotating daily. The ids are picked first,
    /// so only the five shown are summarised.
    /// </summary>
    private static async Task<List<ItemSummaryDto>> FeaturedAsync(IQueryable<MediaItem> topLevel, string profileId, int day, CancellationToken ct)
    {
        var candidates = await topLevel.Where(i => i.BackdropUrl != null && i.Overview != null && i.CommunityRating >= 7.3)
            .OrderBy(i => i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)).ThenByDescending(i => i.LogoUrl != null)
            .Take(60).Select(i => i.Id).ToListAsync(ct);
        var picks = candidates.OrderBy(id => (id.GetHashCode() ^ day) & int.MaxValue).Take(5).ToList();
        var items = await topLevel.Where(i => picks.Contains(i.Id)).Summaries(profileId).ToDictionaryAsync(i => i.Id, ct);
        return picks.Where(items.ContainsKey).Select(id => items[id]).ToList();
    }

    private static async Task<Dictionary<Guid, string?>> OverviewsAsync(FlexarrDbContext db, List<ItemSummaryDto> items, CancellationToken ct)
    {
        var ids = items.Select(i => i.Id).ToList();
        return await db.MediaItems.Where(i => ids.Contains(i.Id)).Select(i => new { i.Id, i.Overview }).ToDictionaryAsync(i => i.Id, i => i.Overview, ct);
    }

    /// <summary>

    /// Puts whatever you were last watching first in a hero rotation: the show for an episode, so the hero has its
    /// artwork. Returns the rotation and what its first title resumes (null if nothing is in progress).
    /// </summary>
    private static async Task<(List<ItemSummaryDto> Rotation, ItemSummaryDto? Resume)> LeadWithContinueWatchingAsync(
        FlexarrDbContext db, string profileId, List<ItemSummaryDto> continueWatching, List<ItemSummaryDto> rotation, CancellationToken ct)
    {
        if (continueWatching.Count == 0)
        {
            return (rotation, null);
        }

        var resume = continueWatching[0];

        var leadId = resume.Kind == MediaKind.Episode
            ? await db.MediaItems.Where(i => i.Id == resume.Id).Select(i => i.Parent!.ParentId).FirstOrDefaultAsync(ct)
            : resume.Id;
        return leadId is { } id && await db.MediaItems.Where(i => i.Id == id).Summaries(profileId).FirstOrDefaultAsync(ct) is { } lead
            ? ([lead, .. rotation.Where(r => r.Id != lead.Id).Take(4)], resume)
            : (rotation, null);
    }

    /// <summary>
    /// Newest first, leaving out what you've seen: films you've watched, and shows with no unwatched episodes. A show's
    /// newest episode bubbles it up, rather than listing every episode.
    /// </summary>
    private static IOrderedQueryable<MediaItem> LatestUnwatched(IQueryable<MediaItem> topLevel, bool movies, string profileId) => movies
        ? topLevel.Where(i => i.Kind == MediaKind.Movie && !i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)).OrderByDescending(i => i.AddedAt)
        : topLevel.Where(i => i.Children.SelectMany(s => s.Children).Any(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)))
            .OrderByDescending(i => i.Children.SelectMany(s => s.Children).Max(e => (DateTime?)e.AddedAt));

    /// <summary>Audience rating, nudged by critics where Rotten Tomatoes has a score.</summary>
    private static IOrderedQueryable<MediaItem> TopRated(IQueryable<MediaItem> query) =>
        query.Where(i => i.CommunityRating != null)
            .OrderByDescending(i => i.CommunityRating + (i.CriticRating == null ? 0 : (i.CriticRating.Value - 70) / 50.0))
            .ThenByDescending(i => i.Popularity);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Coming soon is unavailable")]
    private static partial void LogUpcomingFailed(ILogger logger, Exception exception);
}
