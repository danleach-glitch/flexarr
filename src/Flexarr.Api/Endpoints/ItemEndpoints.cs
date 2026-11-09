using Flexarr.Api.Configuration;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Flexarr.Api.Localization;

namespace Flexarr.Api.Endpoints;

public static class ItemEndpoints
{
    private const int MaxPageSize = 500;

    /// <summary>The most tracks a genre's Play or Shuffle queues.</summary>
    private const int MaxGenreTracks = 200;

    /// <summary>
    /// Albums with this genre: their own, or their artist's when they have none (Lidarr tags some albums only through
    /// their artist). An artist's every genre doesn't pass to albums tagged themselves, or Ben would be hip hop. All of
    /// them without a genre.
    /// </summary>
    private static IQueryable<MediaItem> InGenre(IQueryable<MediaItem> albums, string? genre) => string.IsNullOrWhiteSpace(genre)
        ? albums
        : albums.Where(a => a.Genres.Any(g => g.Name == genre) || (!a.Genres.Any() && a.Parent!.Genres.Any(g => g.Name == genre)));

    public static RouteGroupBuilder MapItemEndpoints(this RouteGroupBuilder api)
    {
        api.MapGet("/libraries/{id:guid}/items", async (Guid id, string? search, string? sort, string? genre, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var query = db.MediaItems.Where(i => i.LibraryId == id && i.ParentId == null);
            if (!string.IsNullOrWhiteSpace(search))
            {
                // SQLite LIKE is case-insensitive for ASCII.
                var pattern = $"%{search.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
                query = query.Where(i => EF.Functions.Like(i.Title, pattern));
            }

            if (!string.IsNullOrWhiteSpace(genre))
            {
                query = query.Where(i => i.Genres.Any(g => g.Name == genre));
            }

            var total = await query.CountAsync(ct);
            var ordered = sort switch
            {
                "added" => query.OrderByDescending(i => i.AddedAt),
                "year" => query.OrderByDescending(i => i.Year).ThenBy(i => i.SortTitle),
                "rating" => query.OrderByDescending(i => i.CommunityRating).ThenBy(i => i.SortTitle),
                "runtime" => query.OrderBy(i => i.RuntimeMinutes).ThenBy(i => i.SortTitle),
                _ => query.OrderBy(i => i.SortTitle).ThenBy(i => i.Year),
            };
            var items = await ordered.Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 100, 1, MaxPageSize)).Summaries(profile.Id).ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Items");

        // Each genre with a backdrop from one of its best-rated titles here, for the library's Genres tab.
        api.MapGet("/libraries/{id:guid}/genres", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
        {
            var genres = await db.Genres
                .Select(g => new
                {
                    g.Name,
                    Count = g.Items.Count(i => i.LibraryId == id && i.ParentId == null),
                    Candidates = g.Items.Where(i => i.LibraryId == id && i.ParentId == null && i.BackdropUrl != null)
                        .OrderByDescending(i => i.CommunityRating ?? 0).ThenByDescending(i => i.Popularity)
                        .Select(i => new { i.Id, i.MetadataUpdatedAt, i.ArtworkChangedAt }).Take(12).ToList(),
                })
                .Where(g => g.Count > 0)
                .OrderByDescending(g => g.Count)
                .ToListAsync(ct);

            // As with Discover's genres: each gets its own cover where it can, since big genres share most titles.
            var used = new HashSet<Guid>();
            return genres.Select(g =>
            {
                var cover = g.Candidates.FirstOrDefault(c => used.Add(c.Id)) ?? g.Candidates.FirstOrDefault();
                return new LibraryGenreDto(g.Name, g.Count,
                    cover is null ? null : ArtworkUrls.For(cover.Id, ArtworkUrls.Version(cover.MetadataUpdatedAt, cover.ArtworkChangedAt), false, true, false, false).Backdrop);
            }).ToList();
        }).WithTags("Items");

        // A music library's albums (its items are its artists), paged like items: A–Z by default, newest added, or by year.
        api.MapGet("/libraries/{id:guid}/albums", async (Guid id, string? search, string? sort, string? genre, int? skip, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var query = InGenre(db.MediaItems.Where(i => i.LibraryId == id && i.Kind == MediaKind.Album), genre);
            if (!string.IsNullOrWhiteSpace(search))
            {
                // An album's title or its artist's name.
                var pattern = $"%{search.Trim().Replace("%", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal)}%";
                query = query.Where(i => EF.Functions.Like(i.Title, pattern) || EF.Functions.Like(i.Parent!.Title, pattern));
            }

            var total = await query.CountAsync(ct);
            var ordered = sort switch
            {
                "added" => query.NewestArrivalsFirst(),
                "year" => query.OrderByDescending(i => i.Year).ThenBy(i => i.SortTitle),
                _ => query.OrderBy(i => i.SortTitle).ThenBy(i => i.Year),
            };
            var items = await ordered.Skip(Math.Max(skip ?? 0, 0)).Take(Math.Clamp(take ?? 100, 1, MaxPageSize)).Summaries(profile.Id).ToListAsync(ct);
            return new PagedDto<ItemSummaryDto>(total, items);
        }).WithTags("Items");

        // A music library's genres by album, for its Genres view: an album's own, or its artist's when it has none. The
        // backdrop is an artist's fanart; a genre none of whose artists has any gets up to three album covers instead.
        api.MapGet("/libraries/{id:guid}/album-genres", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
        {
            // Worked out here rather than in SQL, which can't count across both sides; a library has a few thousand albums at most.
            var albums = await db.MediaItems.Where(a => a.LibraryId == id && a.Kind == MediaKind.Album)
                .OrderByDescending(a => a.AddedAt)
                .Select(a => new { a.Id, a.ParentId, Cover = a.PosterUrl != null, a.MetadataUpdatedAt, a.ArtworkChangedAt, Genres = a.Genres.Select(g => g.Name).ToList() })
                .ToListAsync(ct);
            var artists = await db.MediaItems.Where(a => a.LibraryId == id && a.Kind == MediaKind.Artist && !a.Placeholder)
                .OrderByDescending(a => a.CommunityRating ?? 0).ThenBy(a => a.SortTitle)
                .Select(a => new { a.Id, Fanart = a.BackdropUrl != null, a.MetadataUpdatedAt, a.ArtworkChangedAt, Genres = a.Genres.Select(g => g.Name).ToList() })
                .ToListAsync(ct);
            var artistGenres = artists.ToDictionary(a => a.Id, a => a.Genres);

            // One pass: each genre's album count, and the artists it belongs to (an album's artist, or the artist's own
            // genre), for its picture.
            var counts = new Dictionary<string, int>();
            var artistsOf = new Dictionary<string, HashSet<Guid>>();
            // Each genre's album covers, newest first (the albums come newest first), for one without fanart.
            var coversOf = new Dictionary<string, List<string>>();
            void Note(string genre, Guid? artist)
            {
                if (artist is { } a)
                {
                    if (!artistsOf.TryGetValue(genre, out var set))
                    {
                        artistsOf[genre] = set = [];
                    }

                    set.Add(a);
                }
            }

            foreach (var album in albums)
            {
                // Its own genres, or its artist's when it has none (as InGenre).
                var genres = album.Genres.Count > 0 ? album.Genres
                    : album.ParentId is { } artist && artistGenres.TryGetValue(artist, out var theirs) ? theirs : [];
                foreach (var genre in genres.Distinct())
                {
                    counts[genre] = counts.GetValueOrDefault(genre) + 1;
                    Note(genre, album.ParentId);
                    if (album.Cover)
                    {
                        if (!coversOf.TryGetValue(genre, out var covers))
                        {
                            coversOf[genre] = covers = [];
                        }

                        if (covers.Count < 3)
                        {
                            covers.Add(ArtworkUrls.For(album.Id, ArtworkUrls.Version(album.MetadataUpdatedAt, album.ArtworkChangedAt), true, false, false, false).Poster!);
                        }
                    }
                }
            }

            foreach (var artist in artists)
            {
                foreach (var genre in artist.Genres)
                {
                    Note(genre, artist.Id);
                }
            }

            // The artists come best-rated first, so the first with fanart is the best picture; each genre its own where it can.
            var withFanart = artists.Where(a => a.Fanart).ToList();
            var used = new HashSet<Guid>();
            return counts.OrderByDescending(g => g.Value).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var theirs = artistsOf.GetValueOrDefault(g.Key) ?? [];
                    var candidates = withFanart.Where(a => theirs.Contains(a.Id)).ToList();
                    var cover = candidates.FirstOrDefault(c => used.Add(c.Id)) ?? candidates.FirstOrDefault();
                    return cover is null
                        ? new LibraryGenreDto(g.Key, g.Value, null, coversOf.GetValueOrDefault(g.Key) ?? [])
                        : new LibraryGenreDto(g.Key, g.Value,
                            ArtworkUrls.For(cover.Id, ArtworkUrls.Version(cover.MetadataUpdatedAt, cover.ArtworkChangedAt), false, true, false, false).Backdrop);
                }).ToList();
        }).WithTags("Items");

        // A genre's tracks in a music library, for its Play and Shuffle: by artist, album and track, or shuffled. 200 at most.
        api.MapGet("/libraries/{id:guid}/tracks", async (Guid id, string? genre, bool? shuffle, int? take, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var albums = InGenre(db.MediaItems.Where(i => i.LibraryId == id && i.Kind == MediaKind.Album), genre).Select(a => a.Id);
            var tracks = db.MediaItems.Where(t => t.Kind == MediaKind.Track && t.ParentId != null && albums.Contains(t.ParentId.Value));
            var limit = Math.Clamp(take ?? MaxGenreTracks, 1, MaxGenreTracks);
            var ordered = shuffle == true
                ? tracks.OrderBy(_ => EF.Functions.Random())
                : tracks.OrderBy(t => t.Parent!.Parent!.SortTitle).ThenBy(t => t.Parent!.Year).ThenBy(t => t.Parent!.SortTitle)
                    .ThenBy(t => t.DiscNumber ?? 1).ThenBy(t => t.TrackNumber ?? int.MaxValue).ThenBy(t => t.SortTitle);
            return await ordered.Take(limit).Summaries(profile.Id).ToListAsync(ct);
        }).WithTags("Items");

        api.MapGet("/items/recent", async (int? take, Guid? libraryId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.MediaItems
                .Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode) && (libraryId == null || i.LibraryId == libraryId))
                .OrderByDescending(i => i.AddedAt)
                .Take(Math.Clamp(take ?? 24, 1, 100))
                .Summaries(profile.Id)
                .ToListAsync(ct))
            .WithTags("Items");

        // includeSeason (the web's title page): a series also carries its Up Next episode and one season's episodes (the
        // requested number, else the first unwatched), so the page needs one request rather than three in a row.
        api.MapGet("/items/{id:guid}", async (Guid id, bool? includeSeason, int? season, FlexarrDbContext db, PlaybackService playback,
            CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, Watchlist.WatchlistService watchlist, CancellationToken ct) =>
        {
            // A kids profile doesn't find subtitles.
            var bazarr = options.CurrentValue.Bazarr?.Instance is not null && !db.ServesKids;
            var detail = await DetailAsync(id, db, profile, bazarr, ct);
            if (detail is null)
            {
                return Results.NotFound();
            }

            // Null to a kids profile, which has no watchlist.
            if (detail.Item.Kind is MediaKind.Movie or MediaKind.Series && !db.ServesKids)
            {
                detail = detail with { OnWatchlist = await watchlist.ContainsItemAsync(profile.Id, id, ct) };
            }

            if (includeSeason == true && detail.Item.Kind == MediaKind.Series)
            {
                var nextUp = await playback.NextUpAsync(id, ct) is { } episode
                    ? await db.MediaItems.Where(i => i.Id == episode.Id).Summaries(profile.Id).FirstAsync(ct)
                    : null;
                // The same choice the page makes: specials last, then the requested season, the first unwatched, or the first.
                var seasons = detail.Children.OrderBy(s => s.SeasonNumber == 0).ThenBy(s => s.SeasonNumber).ToList();
                var selected = seasons.FirstOrDefault(s => season is { } n && s.SeasonNumber == n)
                    ?? seasons.FirstOrDefault(s => !s.UserData.Watched && s.SeasonNumber != 0)
                    ?? seasons.FirstOrDefault();
                var seasonDetail = selected is null ? null : await DetailAsync(selected.Id, db, profile, bazarr, ct);
                detail = detail with { NextUp = nextUp, Season = seasonDetail };
            }

            return Results.Ok(detail);
        }).WithTags("Items");

        // Everything an artist (or one album) has, in the order a record collection plays: albums oldest first, then
        // disc and track. One request, so the web's Play and Shuffle buttons don't fetch each album in turn.
        api.MapGet("/items/{id:guid}/tracks", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var kind = await db.MediaItems.Where(i => i.Id == id).Select(i => (MediaKind?)i.Kind).FirstOrDefaultAsync(ct);
            if (kind is not (MediaKind.Artist or MediaKind.Album))
            {
                return Results.NotFound();
            }

            // An artist's albums first, then the tracks in them (and any straight under the artist): each found by index.
            // Asked for in one go, as tracks whose album is this or whose album's artist is, SQLite read every item
            // in the database.
            List<Guid> albums = [id];
            if (kind == MediaKind.Artist)
            {
                albums.AddRange(await db.MediaItems.Where(i => i.ParentId == id).Select(i => i.Id).ToListAsync(ct));
            }

            var tracks = db.MediaItems.Where(i => i.Kind == MediaKind.Track && i.ParentId != null && albums.Contains(i.ParentId.Value));
            return Results.Ok(await tracks
                .OrderBy(t => t.Parent!.Year == null).ThenBy(t => t.Parent!.Year).ThenBy(t => t.Parent!.SortTitle)
                .ThenBy(t => t.SortTitle)
                .Summaries(profile.Id).ToListAsync(ct));
        }).WithTags("Items");

        // The version of a title this profile wants, on any of its devices; no file goes back to choosing automatically.
        api.MapPut("/items/{id:guid}/version", async (Guid id, VersionChoiceRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var item = await db.MediaItems.AsNoTracking().FirstOrDefaultAsync(i => i.Id == id && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode), ct);
            if (item is null)
            {
                return Results.NotFound();
            }

            var file = request.FileId is { } wanted ? (await Playback.TitleVersions.FilesAsync(db, item, ct)).FirstOrDefault(f => f.Id == wanted) : null;
            if (request.FileId is not null && file is null)
            {
                return Results.NotFound();
            }

            var copies = await Playback.TitleVersions.CopiesAsync(db, item, ct);
            await db.VersionChoices.Where(c => c.ProfileId == profile.Id && copies.Contains(c.MediaItemId)).ExecuteDeleteAsync(ct);
            if (file is not null)
            {
                db.VersionChoices.Add(new VersionChoice
                {
                    ProfileId = profile.Id, MediaItemId = file.MediaItemId, MediaFileId = file.Id, Label = VersionRules.Label(file), Edition = VersionRules.Edition(file),
                });
                await db.SaveChangesAsync(ct);
            }

            return Results.NoContent();
        }).WithTags("Items");

        // A parent's Allow or Block for profiles with an age limit, on a film, show, artist or podcast (its seasons,
        // episodes and tracks follow it).
        api.MapPut("/items/{id:guid}/kids", async (Guid id, KidsOverrideRequest request, FlexarrDbContext db, CancellationToken ct) =>
        {
            if (!Enum.IsDefined(request.Override))
            {
                return Problems.Field("Choose none, allow or block.", field: "override");
            }

            if (await db.MediaItems.IgnoreQueryFilters().FirstOrDefaultAsync(i => i.Id == id && i.MissingSince == null, ct) is not { } item)
            {
                return Results.NotFound();
            }

            if (item.ParentId is not null)
            {
                return Problems.Of("Allow or block the whole title: its seasons and episodes follow it.", status: 409);
            }

            item.KidsOverride = request.Override;
            await db.SaveChangesAsync(ct);
            LibraryVisibility.KidsSettingsChanged();
            return Results.NoContent();
        }).WithTags("Items").RequireAuthorization(Security.FlexarrClaims.MainProfilePolicy);

        return api;
    }

    /// <param name="bazarr">Whether Bazarr is set up, so films and episodes Sonarr and Radarr manage can have subtitles found.</param>
    private static async Task<ItemDetailDto?> DetailAsync(Guid id, FlexarrDbContext db, CurrentProfile profile, bool bazarr, CancellationToken ct)
    {
        var item = await db.MediaItems.Where(i => i.Id == id).Summaries(profile.Id).FirstOrDefaultAsync(ct);
        if (item is null)
        {
            return null;
        }

        if (await db.MediaItems.AsNoTracking().Include(i => i.Genres).FirstOrDefaultAsync(i => i.Id == id, ct) is not { } entity)
        {
            return null;
        }

        var ancestors = new List<ItemSummaryDto>();
        for (var parentId = entity.ParentId; parentId is { } pid;)
        {
            // A scan may take a parent away mid-request: the trail stops there instead of failing.
            if (await db.MediaItems.Where(i => i.Id == pid).Summaries(profile.Id).FirstOrDefaultAsync(ct) is not { } parent)
            {
                break;
            }

            ancestors.Insert(0, parent);
            parentId = await db.MediaItems.Where(i => i.Id == pid).Select(i => i.ParentId).FirstOrDefaultAsync(ct);
        }

        // An artist's albums read newest first; everything else in its natural order.
        var childQuery = db.MediaItems.Where(i => i.ParentId == id);
        var children = await (entity.Kind == MediaKind.Artist
                ? childQuery.OrderByDescending(i => i.Year).ThenBy(i => i.SortTitle)
                : childQuery.OrderBy(i => i.SortTitle))
            .Summaries(profile.Id).ToListAsync(ct);
        var files = await db.MediaFiles.AsNoTracking().Where(f => f.MediaItemId == id).Include(f => f.Streams)
            .OrderByDescending(f => f.Height).ToListAsync(ct);

        // Seasons and episodes show their series' cast.
        var creditsFor = entity.Kind is MediaKind.Season or MediaKind.Episode && ancestors.Count > 0 ? ancestors[0].Id : id;
        var credits = await db.Credits.Where(c => c.MediaItemId == creditsFor).OrderBy(c => c.Order)
            .Select(c => new CreditDto(c.PersonId, c.Person!.Name, c.Role, c.Character, ArtworkUrls.Person(c.PersonId, c.Person.ImageUrl != null, c.Person.ImageChangedAt)))
            .ToListAsync(ct);

        var series = entity.Kind is MediaKind.Season or MediaKind.Episode && ancestors.Count > 0
            ? await db.MediaItems.AsNoTracking().Include(i => i.Genres).FirstAsync(i => i.Id == ancestors[0].Id, ct)
            : entity;

        // A season's episode list shows overviews; send them with it rather than one request per episode.
        var childOverviews = entity.Kind == MediaKind.Season
            ? await db.MediaItems.Where(i => i.ParentId == id).Select(i => new { i.Id, i.Overview }).ToDictionaryAsync(i => i.Id, i => i.Overview, ct)
            : new Dictionary<Guid, string?>();

        // Where things are on the server's disk is the main profile's to know: the fields stay (apps read them), empty.
        var paths = profile.IsMainProfile;
        return new ItemDetailDto(
            item, paths ? entity.Path : null, entity.OriginalTitle, entity.Overview ?? (entity.Kind == MediaKind.Season ? series.Overview : null),
            entity.Studio ?? series.Studio, series.Status, entity.TrailerYouTubeId,
            series.Genres.Select(g => g.Name).Order().ToList(),
            series.TvdbId, entity.TmdbId ?? series.TmdbId, entity.ImdbId ?? series.ImdbId,
            ancestors, children, credits, files.Select(f => ToDto(f, paths)).ToList(), childOverviews, entity.MatchLocked,
            CanSearchSubtitles: bazarr && entity.Kind is (MediaKind.Movie or MediaKind.Episode) && entity.ArrId is not null && files.Count > 0,
            RatingAge: entity.RatingAge, KidsOverride: profile.IsMainProfile ? entity.KidsOverride : null,
            Book: entity.Kind == MediaKind.Audiobook ? await Books.BookQueries.DetailAsync(db, entity, profile.Id, ct) : null,
            DetailsLocked: entity.DetailsLocked, SortTitle: entity.SortTitle, ArtworkLocked: entity.ArtworkLocked,
            Versions: entity.Kind is MediaKind.Movie or MediaKind.Episode ? await VersionsAsync(db, entity, profile.Id, ct) : null);
    }

    /// <summary>What the title's Version choice offers; null with only one version to play.</summary>
    private static async Task<List<VersionDto>?> VersionsAsync(FlexarrDbContext db, MediaItem item, string profileId, CancellationToken ct)
    {
        var files = await Playback.TitleVersions.FilesAsync(db, item, ct);
        if (files.Count < 2)
        {
            return null;
        }

        var choice = await Playback.TitleVersions.ChoiceAsync(db, item, profileId, ct);
        var chosen = choice is null ? null : Streaming.VersionPicker.Chosen(files, choice);
        return VersionRules.BestFirst(files)
            .Select(f => new VersionDto(f.Id, VersionRules.Label(f), VersionRules.Edition(f), f.DurationSeconds, f.Size, f.Id == chosen?.Id))
            .ToList();
    }

    private static FileDto ToDto(MediaFile f, bool path) => new(
        f.Id, path ? f.Path : "", f.Size, f.Container, f.DurationSeconds, f.Bitrate, f.VideoCodec, f.Width, f.Height, f.VideoRange,
        f.AudioCodec, f.AudioChannels, f.ProbeError,
        f.Streams.OrderBy(s => s.Index)
            .Select(s => new StreamDto(s.Index, s.Type, s.Codec, s.Profile, s.Language, s.Title, s.IsDefault, s.IsForced, s.Width, s.Height, s.Channels))
            .ToList(),
        f.Hdr10Plus == true);
}
