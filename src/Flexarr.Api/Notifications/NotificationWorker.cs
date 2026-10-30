using System.Globalization;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.LiveTv;
using Flexarr.Core.Media;
using Flexarr.Core.Notifications;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Notifications;

/// <summary>
/// Each minute, works out what's new since last time and tells the phones that want to know: films and episodes
/// newly in the library, requests that came in, podcast episodes, finished recordings, and (once a day, if asked)
/// what's on today. Each kind keeps a cursor, so nothing is told twice and nothing from before notifications existed
/// is told at all. See docs/notifications.md.
/// </summary>
public sealed partial class NotificationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<NotificationWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);

    /// <summary>How long a new item waits: for the rest of a season pack to land, and for its metadata and artwork.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(3);

    /// <summary>A library this young is still having its first scan: nothing in it is news.</summary>
    public static readonly TimeSpan FirstScan = TimeSpan.FromHours(1);

    /// <summary>More notifications than this for one phone at once become one that sums them up.</summary>
    public const int Flood = 6;

    /// <summary>A podcast episode published longer ago than this isn't news, though it's new to the feed.</summary>
    private static readonly TimeSpan PodcastFreshness = TimeSpan.FromDays(7);

    /// <summary>Episodes added this soon after following a show are its back catalogue, not news.</summary>
    private static readonly TimeSpan FollowGrace = TimeSpan.FromMinutes(5);

    /// <summary>A finished recording waits this long to be scanned in, so Play has something to play.</summary>
    private static readonly TimeSpan RecordingSettle = TimeSpan.FromMinutes(2);

    /// <summary>Upcoming is sent within this long of its hour (a phone that was off at 9 hears at 10, not at 11pm).</summary>
    private const int UpcomingWindowHours = 3;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Tick, clock);
        do
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                LogFailed(ex);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var notifier = scope.ServiceProvider.GetRequiredService<Notifier>();
        var now = clock.GetUtcNow().UtcDateTime;
        // The cursors move on even with nobody listening, so turning notifications on doesn't bring a backlog.
        var devices = await db.NotificationDevices.ToListAsync(ct);

        await LibraryAsync(db, notifier, devices, now, ct);
        await PodcastsAsync(db, notifier, devices, now, ct);
        await RecordingsAsync(db, notifier, devices, now, ct);
        await UpcomingAsync(db, notifier, devices, now, ct);
    }

    // ---- Films, episodes and requests ----

    private sealed record Fresh(Guid Id, MediaKind Kind, Guid LibraryId, string Title, int? Year, string? Overview, string? Poster,
        string? TmdbId, string? ImdbId, int? Season, int? Episode, DateOnly? AirDate, DateTime AddedAt, Guid? SeriesId);

    private sealed record Show(Guid Id, string Title, string? Poster, string? TmdbId, string? TvdbId, string? ImdbId, DateTime AddedAt);

    private static async Task LibraryAsync(FlexarrDbContext db, Notifier notifier, List<NotificationDevice> devices, DateTime now, CancellationToken ct)
    {
        if (await WindowAsync(db, "library", now - Settle, ct) is not var (from, to))
        {
            return;
        }

        var libraries = await db.Libraries.AsNoTracking().Select(l => new { l.Id, l.Type, l.Name, l.CreatedAt }).ToListAsync(ct);
        // A finished recording has notifications of its own; a library's first scan is no news.
        var skipped = libraries.Where(l => l.Type == LibraryType.TvShows && l.Name == RecordingsLibrary.Name).Select(l => l.Id).ToHashSet();
        var created = libraries.ToDictionary(l => l.Id, l => l.CreatedAt);
        var fresh = (await db.MediaItems.AsNoTracking()
                .Where(i => (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Episode) && i.AddedAt > from && i.AddedAt <= to)
                .Select(i => new Fresh(i.Id, i.Kind, i.LibraryId, i.Title, i.Year, i.Overview, i.PosterUrl, i.TmdbId, i.ImdbId, i.SeasonNumber, i.EpisodeNumber,
                    i.AirDate, i.AddedAt, i.Kind == MediaKind.Episode ? i.Parent!.ParentId : null))
                .ToListAsync(ct))
            .Where(i => !skipped.Contains(i.LibraryId) && created.TryGetValue(i.LibraryId, out var at) && i.AddedAt - at > FirstScan)
            .ToList();
        var shows = await ShowsAsync(db, fresh, ct);
        fresh = await WithoutReplacementsAsync(db, fresh, shows, ct);

        var answered = await RequestsAsync(db, to, now, ct);
        answered.AddRange(await WatchlistArrivalsAsync(db, answered, to, now, ct));
        if (devices.Count == 0)
        {
            await SaveAsync(db, ct);
            return;
        }

        foreach (var profile in devices.GroupBy(d => d.ProfileId))
        {
            var hidden = await db.Profiles.Where(p => p.Id == profile.Key).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct) ?? [];
            var ids = fresh.Select(i => i.Id).ToList();
            var watched = (await db.PlaybackStates.Where(p => p.UserId == profile.Key && p.Watched && ids.Contains(p.MediaItemId))
                .Select(p => p.MediaItemId).ToListAsync(ct)).ToHashSet();
            var theirs = answered.Where(a => a.ProfileId == profile.Key).ToList();
            var told = theirs.SelectMany(a => a.Items).ToHashSet();
            var unseen = fresh.Where(i => !hidden.Contains(i.LibraryId) && !watched.Contains(i.Id)).ToList();

            // Phones of one profile can still want different things.
            foreach (var wants in profile.GroupBy(d => (d.Films, d.Shows, d.Requests)))
            {
                var messages = new List<PushMessage>();
                // A phone that hears about requests and the watchlist gets those instead of the usual news for the same
                // titles; one that doesn't still gets the usual news.
                var visible = wants.Key.Requests ? unseen.Where(i => !told.Contains(i.Id)).ToList() : unseen;
                if (wants.Key.Requests)
                {
                    messages.AddRange(theirs.Select(a => a.Message));
                }

                messages.AddRange(Compose(visible.Where(i => i.Kind == MediaKind.Movie ? wants.Key.Films : wants.Key.Shows), shows, from));
                foreach (var message in messages.Count > Flood ? [Summary(messages)] : messages)
                {
                    await notifier.SendAsync(db, wants, message, null, ct);
                }
            }
        }

        await SaveAsync(db, ct);
    }

    /// <summary>
    /// Saves the tick's changes. If a watchlist title was linked to another of the profile's entries meanwhile (added
    /// again by its library id), that one stands and this one is only marked arrived, so the tick still saves and
    /// nobody is told twice.
    /// </summary>
    private static async Task SaveAsync(FlexarrDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<WatchlistEntry>().Where(e => e.State == EntityState.Modified))
            {
                entry.Property(w => w.MediaItemId).CurrentValue = entry.Property(w => w.MediaItemId).OriginalValue;
            }

            await db.SaveChangesAsync(ct);
        }
    }

    private static async Task<Dictionary<Guid, Show>> ShowsAsync(FlexarrDbContext db, List<Fresh> fresh, CancellationToken ct)
    {
        var seriesIds = fresh.Where(i => i.SeriesId is not null).Select(i => i.SeriesId!.Value).Distinct().ToList();
        return await db.MediaItems.AsNoTracking().Where(s => seriesIds.Contains(s.Id))
            .Select(s => new Show(s.Id, s.Title, s.PosterUrl, s.TmdbId, s.TvdbId, s.ImdbId, s.AddedAt))
            .ToDictionaryAsync(s => s.Id, ct);
    }

    /// <summary>
    /// Drops titles that aren't new to the household, only new copies: a film or episode whose old copy is still
    /// remembered (in another folder or library, missing while its replacement settles, or buried as a watch-history
    /// tombstone). A plain upgrade keeps its item, so it never shows up as new at all.
    /// </summary>
    private static async Task<List<Fresh>> WithoutReplacementsAsync(FlexarrDbContext db, List<Fresh> fresh, Dictionary<Guid, Show> shows, CancellationToken ct)
    {
        if (fresh.Count == 0)
        {
            return fresh;
        }

        var freshIds = fresh.Select(i => i.Id).ToList();
        var known = new HashSet<string>(StringComparer.Ordinal);

        var movies = fresh.Where(i => i.Kind == MediaKind.Movie).ToList();
        var movieTmdb = movies.Where(m => m.TmdbId is not null).Select(m => m.TmdbId!).ToList();
        var movieImdb = movies.Where(m => m.ImdbId is not null).Select(m => m.ImdbId!).ToList();
        if (movieTmdb.Count + movieImdb.Count > 0)
        {
            var older = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                .Where(o => o.Kind == MediaKind.Movie && !freshIds.Contains(o.Id)
                    && ((o.TmdbId != null && movieTmdb.Contains(o.TmdbId)) || (o.ImdbId != null && movieImdb.Contains(o.ImdbId))))
                .Select(o => new { o.TmdbId, o.ImdbId }).ToListAsync(ct);
            var buried = await db.WatchTombstones.AsNoTracking()
                .Where(t => t.Kind == MediaKind.Movie && ((t.TmdbId != null && movieTmdb.Contains(t.TmdbId)) || (t.ImdbId != null && movieImdb.Contains(t.ImdbId))))
                .Select(t => new { t.TmdbId, t.ImdbId }).ToListAsync(ct);
            foreach (var o in older.Concat(buried))
            {
                known.UnionWith(MovieKeys(o.TmdbId, o.ImdbId));
            }
        }

        var freshShows = shows.Values.ToList();
        var showIds = freshShows.Select(s => s.Id).ToList();
        var tvdb = freshShows.Where(s => s.TvdbId is not null).Select(s => s.TvdbId!).ToList();
        var tmdb = freshShows.Where(s => s.TmdbId is not null).Select(s => s.TmdbId!).ToList();
        if (tvdb.Count + tmdb.Count > 0)
        {
            var twins = await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                .Where(s => s.Kind == MediaKind.Series && !showIds.Contains(s.Id)
                    && ((s.TvdbId != null && tvdb.Contains(s.TvdbId)) || (s.TmdbId != null && tmdb.Contains(s.TmdbId))))
                .Select(s => new { s.Id, s.TvdbId, s.TmdbId }).ToListAsync(ct);
            var twinIds = twins.Select(t => t.Id).ToList();
            var twinById = twins.ToDictionary(t => t.Id);
            foreach (var e in await db.MediaItems.IgnoreQueryFilters().AsNoTracking()
                         .Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId != null && twinIds.Contains(e.Parent.ParentId.Value))
                         .Select(e => new { SeriesId = e.Parent!.ParentId!.Value, e.SeasonNumber, e.EpisodeNumber, e.AirDate }).ToListAsync(ct))
            {
                var twin = twinById[e.SeriesId];
                known.UnionWith(EpisodeKeys(twin.TvdbId, twin.TmdbId, e.SeasonNumber, e.EpisodeNumber, e.AirDate));
            }

            foreach (var t in await db.WatchTombstones.AsNoTracking()
                         .Where(t => t.Kind == MediaKind.Episode && ((t.TvdbId != null && tvdb.Contains(t.TvdbId)) || (t.TmdbId != null && tmdb.Contains(t.TmdbId))))
                         .Select(t => new { t.TvdbId, t.TmdbId, t.SeasonNumber, t.EpisodeNumber, t.AirDate }).ToListAsync(ct))
            {
                known.UnionWith(EpisodeKeys(t.TvdbId, t.TmdbId, t.SeasonNumber, t.EpisodeNumber, t.AirDate));
            }
        }

        if (known.Count == 0)
        {
            return fresh;
        }

        return fresh.Where(i => i.Kind == MediaKind.Movie
                ? !MovieKeys(i.TmdbId, i.ImdbId).Any(known.Contains)
                : !(i.SeriesId is { } s && shows.TryGetValue(s, out var show) && EpisodeKeys(show.TvdbId, show.TmdbId, i.Season, i.Episode, i.AirDate).Any(known.Contains)))
            .ToList();
    }

    private static IEnumerable<string> MovieKeys(string? tmdb, string? imdb)
    {
        if (tmdb is not null)
        {
            yield return $"tmdb:{tmdb}";
        }

        if (imdb is not null)
        {
            yield return $"imdb:{imdb}";
        }
    }

    private static IEnumerable<string> EpisodeKeys(string? tvdb, string? tmdb, int? season, int? episode, DateOnly? airDate)
    {
        var which = episode is { } e ? $"{season}x{e}" : airDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (which is null)
        {
            yield break;
        }

        if (tvdb is not null)
        {
            yield return $"tvdb:{tvdb}:{which}";
        }

        if (tmdb is not null)
        {
            yield return $"tmdb:{tmdb}:{which}";
        }
    }

    private sealed record Answered(string ProfileId, PushMessage Message, IReadOnlyCollection<Guid> Items);

    /// <summary>
    /// Requests whose film, or a new episode of whose show, is now in the library. Looked for every minute rather than
    /// among the minute's new items, since what's in the library may only learn its TMDB id a while after it lands.
    /// </summary>
    private static async Task<List<Answered>> RequestsAsync(FlexarrDbContext db, DateTime settled, DateTime now, CancellationToken ct)
    {
        var answered = new List<Answered>();
        var open = await db.MediaRequests.Where(r => r.FulfilledAt == null).ToListAsync(ct);
        foreach (var request in open)
        {
            var tmdb = request.TmdbId.ToString(CultureInfo.InvariantCulture);
            if (request.Kind == MediaKind.Movie)
            {
                var film = await db.MediaItems.AsNoTracking()
                    .Where(i => i.Kind == MediaKind.Movie && i.TmdbId == tmdb && i.AddedAt <= settled)
                    .Select(i => new { i.Id, i.Title, i.Year, i.PosterUrl }).FirstOrDefaultAsync(ct);
                if (film is not null)
                {
                    request.FulfilledAt = now;
                    answered.Add(new Answered(request.ProfileId, new PushMessage("Your request is ready", $"{WithYear(film.Title, film.Year)} is in the library.",
                        "flexarr.video", $"item:{film.Id}", Artwork(film.PosterUrl), Video(film.Id, film.Id)), [film.Id]));
                }

                continue;
            }

            // A show may be in already, asked for again for its new season: what counts is an episode since the request.
            var episodes = await db.MediaItems.AsNoTracking()
                .Where(i => i.Kind == MediaKind.Episode && i.Parent!.Parent!.TmdbId == tmdb && i.AddedAt > request.RequestedAt && i.AddedAt <= settled)
                .OrderBy(i => i.SeasonNumber).ThenBy(i => i.EpisodeNumber).ThenBy(i => i.AirDate)
                .Select(i => new { i.Id, i.SeasonNumber, i.EpisodeNumber, SeriesId = i.Parent!.ParentId!.Value, Series = i.Parent.Parent!.Title, Poster = i.Parent.Parent.PosterUrl })
                .ToListAsync(ct);
            if (episodes.Count > 0)
            {
                var first = episodes[0];
                request.FulfilledAt = now;
                var what = episodes.Count == 1 ? $"{first.Series} {EpisodeLabel(first.SeasonNumber, first.EpisodeNumber)} is in the library." : $"{first.Series} is in the library.";
                answered.Add(new Answered(request.ProfileId, new PushMessage("Your request is ready", what, "flexarr.video", $"item:{first.SeriesId}",
                    Artwork(first.Poster), Video(first.SeriesId, first.Id)), episodes.Select(e => e.Id).ToList()));
            }
        }

        return answered;
    }

    /// <summary>
    /// Titles on a watchlist that are now in a library their profile can see, to tell whoever put them there. A title
    /// they requested has "Your request is ready" instead, so they hear once; either way the entry is marked as arrived.
    /// An entry already pointing at the title keeps it, and none is pointed at a title another of the profile's holds
    /// (one item, one entry per profile), so marking them can't upset the save that follows the notices.
    /// </summary>
    private static async Task<List<Answered>> WatchlistArrivalsAsync(FlexarrDbContext db, List<Answered> requests, DateTime settled, DateTime now, CancellationToken ct)
    {
        var answered = new List<Answered>();
        var waiting = await db.WatchlistEntries.Where(w => w.ArrivedAt == null && w.RemovedAt == null && w.TmdbId != null).ToListAsync(ct);
        if (waiting.Count == 0)
        {
            return answered;
        }

        // Every title any of them could be, in one go: films in, and shows with an episode in.
        var wanted = waiting.Select(w => w.TmdbId!.Value.ToString(CultureInfo.InvariantCulture)).Distinct().ToList();
        var candidates = await db.MediaItems.AsNoTracking()
            .Where(i => i.ParentId == null && (i.Kind == MediaKind.Movie || i.Kind == MediaKind.Series) && wanted.Contains(i.TmdbId!))
            .Where(i => i.Kind == MediaKind.Movie ? i.AddedAt <= settled : i.Children.Any(s => s.Children.Any(e => e.AddedAt <= settled)))
            .Select(i => new { i.Id, i.Kind, i.TmdbId, i.LibraryId, i.Title, i.Year, i.PosterUrl }).ToListAsync(ct);
        if (candidates.Count == 0)
        {
            return answered;
        }

        var hiddenFor = new Dictionary<string, List<Guid>>(StringComparer.Ordinal);
        foreach (var entry in waiting)
        {
            if (!hiddenFor.TryGetValue(entry.ProfileId, out var hidden))
            {
                hidden = await db.Profiles.Where(p => p.Id == entry.ProfileId).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct) ?? [];
                hiddenFor[entry.ProfileId] = hidden;
            }

            var tmdb = entry.TmdbId!.Value.ToString(CultureInfo.InvariantCulture);
            var title = candidates.FirstOrDefault(i => i.Kind == entry.Kind && i.TmdbId == tmdb && !hidden.Contains(i.LibraryId)
                && (entry.MediaItemId == null || i.Id == entry.MediaItemId));
            if (title is null)
            {
                continue;
            }

            // Another entry of theirs holds the title (the same one added two ways): that one stands; this one just arrived.
            if (entry.MediaItemId is null && !await db.WatchlistEntries.AnyAsync(w => w.ProfileId == entry.ProfileId && w.MediaItemId == title.Id, ct))
            {
                entry.MediaItemId = title.Id;
            }

            entry.ArrivedAt = now;
            var told = requests.Any(r => r.ProfileId == entry.ProfileId && r.Message.Thread == $"item:{title.Id}")
                || await db.MediaRequests.AnyAsync(r => r.ProfileId == entry.ProfileId && r.Kind == entry.Kind && r.TmdbId == entry.TmdbId && r.FulfilledAt != null && r.FulfilledAt >= entry.AddedAt, ct);
            if (told)
            {
                continue;
            }

            var first = entry.Kind == MediaKind.Movie ? title.Id : await db.MediaItems.AsNoTracking()
                .Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == title.Id).OrderBy(e => e.SeasonNumber).ThenBy(e => e.EpisodeNumber).ThenBy(e => e.AirDate)
                .Select(e => e.Id).FirstOrDefaultAsync(ct);
            var items = entry.Kind == MediaKind.Movie ? [title.Id]
                : await db.MediaItems.AsNoTracking().Where(e => e.Kind == MediaKind.Episode && e.Parent!.ParentId == title.Id && e.AddedAt <= settled).Select(e => e.Id).ToListAsync(ct);
            answered.Add(new Answered(entry.ProfileId, new PushMessage("From your watchlist", $"{WithYear(title.Title, title.Year)} is ready to watch.", "flexarr.video",
                $"item:{title.Id}", Artwork(title.PosterUrl), Video(title.Id, first)), items));
        }

        // Saved with the rest once the notices are sent, so nothing is marked told before it's been said.
        return answered;
    }

    /// <summary>A notification per film, and one per show however many of its episodes came.</summary>
    private static IEnumerable<PushMessage> Compose(IEnumerable<Fresh> items, Dictionary<Guid, Show> shows, DateTime since)
    {
        foreach (var film in items.Where(i => i.Kind == MediaKind.Movie).OrderBy(i => i.AddedAt))
        {
            yield return new PushMessage($"New film: {WithYear(film.Title, film.Year)}", Short(film.Overview) ?? "Ready to watch.", "flexarr.video",
                $"item:{film.Id}", Artwork(film.Poster), Video(film.Id, film.Id));
        }

        foreach (var group in items.Where(i => i.Kind == MediaKind.Episode && i.SeriesId is not null).GroupBy(i => i.SeriesId!.Value))
        {
            if (!shows.TryGetValue(group.Key, out var show))
            {
                continue;
            }

            var episodes = group.OrderBy(e => e.Season).ThenBy(e => e.Episode).ThenBy(e => e.AirDate).ToList();
            var first = episodes[0];
            // The show itself arrived with these; the rest of a pack that took longer to land is "new episodes".
            var isNew = show.AddedAt > since;
            string title, body;
            if (episodes.Count == 1)
            {
                title = isNew ? $"New show: {show.Title}" : show.Title;
                body = $"{(isNew ? "" : "New episode · ")}{EpisodeLabel(first.Season, first.Episode)} · {first.Title}";
            }
            else
            {
                title = isNew ? $"New show: {show.Title}" : $"{episodes.Count} new episodes of {show.Title}";
                var seasons = episodes.Select(e => e.Season).Distinct().ToList();
                body = seasons.Count == 1 && first.Season is { } s
                    ? $"{(s == 0 ? "Specials" : $"Season {s}")} · {episodes.Count} episodes"
                    : $"{episodes.Count} episodes across {seasons.Count} seasons";
            }

            yield return new PushMessage(title, body, "flexarr.video", $"item:{show.Id}", Artwork(show.Poster), Video(show.Id, first.Id));
        }
    }

    /// <summary>A flood (a big import, a whole collection at once) told in one notification.</summary>
    private static PushMessage Summary(List<PushMessage> messages)
    {
        var names = messages.Select(m => m.Title.Replace("New film: ", "", StringComparison.Ordinal).Replace("New show: ", "", StringComparison.Ordinal)).ToList();
        var body = string.Join(", ", names.Take(4)) + (names.Count > 4 ? $" and {names.Count - 4} more" : "");
        return new PushMessage($"{messages.Count} new things to watch", body, "flexarr.video", "library");
    }

    // ---- Podcasts ----

    private static async Task PodcastsAsync(FlexarrDbContext db, Notifier notifier, List<NotificationDevice> devices, DateTime now, CancellationToken ct)
    {
        if (await WindowAsync(db, "podcasts", now, ct) is not var (from, to))
        {
            return;
        }

        var listening = devices.Where(d => d.Podcasts).ToList();
        if (listening.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var fresh = await db.PodcastEpisodes.AsNoTracking()
            .Where(e => e.MediaItem!.AddedAt > from && e.MediaItem.AddedAt <= to && e.MediaItem.MissingSince == null)
            .Select(e => new { e.MediaItemId, e.FeedId, e.PublishedAt, e.MediaItem!.Title, e.MediaItem.AddedAt })
            .ToListAsync(ct);
        fresh = fresh.Where(e => (e.PublishedAt ?? e.AddedAt) > now - PodcastFreshness).ToList();
        var feedIds = fresh.Select(e => e.FeedId).Distinct().ToList();
        var profiles = listening.Select(d => d.ProfileId).Distinct().ToList();
        var follows = await db.PodcastSubscriptions.AsNoTracking()
            .Where(s => feedIds.Contains(s.FeedId) && profiles.Contains(s.ProfileId))
            .Select(s => new { s.ProfileId, s.FeedId, s.SubscribedAt }).ToListAsync(ct);
        var shows = await db.MediaItems.AsNoTracking().Where(i => feedIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Title, i.PosterUrl }).ToDictionaryAsync(i => i.Id, ct);

        foreach (var follow in follows)
        {
            var episodes = fresh.Where(e => e.FeedId == follow.FeedId && e.AddedAt > follow.SubscribedAt + FollowGrace)
                .OrderByDescending(e => e.PublishedAt ?? e.AddedAt).ToList();
            if (episodes.Count == 0 || !shows.TryGetValue(follow.FeedId, out var show))
            {
                continue;
            }

            var newest = episodes[0];
            var message = episodes.Count == 1
                ? new PushMessage($"New: {show.Title}", newest.Title, "flexarr.podcast", $"podcast:{show.Id}", Artwork(show.PosterUrl), Podcast(show.Id, newest.MediaItemId))
                : new PushMessage($"{episodes.Count} new episodes from {show.Title}", string.Join("\n", episodes.Take(3).Select(e => e.Title)), "flexarr.podcast",
                    $"podcast:{show.Id}", Artwork(show.PosterUrl), Podcast(show.Id, newest.MediaItemId));
            await notifier.SendAsync(db, listening.Where(d => d.ProfileId == follow.ProfileId), message, $"podcast:{newest.MediaItemId}", ct);
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Recordings ----

    private static async Task RecordingsAsync(FlexarrDbContext db, Notifier notifier, List<NotificationDevice> devices, DateTime now, CancellationToken ct)
    {
        if (await WindowAsync(db, "recordings", now - RecordingSettle, ct) is not var (from, to))
        {
            return;
        }

        var listening = devices.Where(d => d.Recordings).ToList();
        if (listening.Count == 0)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var finished = await db.Recordings.AsNoTracking()
            .Where(r => r.State == RecordingState.Completed && r.FinishedAt > from && r.FinishedAt <= to)
            .ToListAsync(ct);
        var ruleIds = finished.Where(r => r.RuleId is not null).Select(r => r.RuleId!.Value).Distinct().ToList();
        var ruleOwners = await db.RecordingRules.AsNoTracking().Where(r => ruleIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.CreatedBy, ct);
        var paths = finished.Where(r => r.FilePath is not null).Select(r => r.FilePath!).ToList();
        var items = (await db.MediaFiles.AsNoTracking().Where(f => paths.Contains(f.Path))
                .Select(f => new { f.Path, f.MediaItemId, Poster = f.MediaItem!.PosterUrl ?? f.MediaItem.Parent!.Parent!.PosterUrl }).ToListAsync(ct))
            .GroupBy(f => f.Path).ToDictionary(g => g.Key, g => g.First());

        foreach (var recording in finished)
        {
            // Recordings are the household's, but whoever set one up is who's waiting for it.
            var owner = recording.CreatedBy ?? (recording.RuleId is { } rule ? ruleOwners.GetValueOrDefault(rule) : null);
            var recipients = owner is null ? listening : listening.Where(d => d.ProfileId == owner).ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var item = recording.FilePath is { } path ? items.GetValueOrDefault(path) : null;
            var episode = recording.Season is { } s && recording.Episode is { } e ? $"S{s} E{e}" : null;
            var body = string.Join(" · ", new[] { episode, recording.Subtitle, recording.ChannelName }.Where(p => !string.IsNullOrWhiteSpace(p)));
            var open = item is null ? null : new Dictionary<string, string> { ["item"] = item.MediaItemId.ToString(), ["play"] = item.MediaItemId.ToString() };
            await notifier.SendAsync(db, recipients, new PushMessage($"Recorded: {recording.Title}", body, "flexarr.recording", "recordings", Artwork(item?.Poster), open),
                $"recording:{recording.Id}", ct);
        }

        await db.SaveChangesAsync(ct);
    }

    // ---- Upcoming ----

    /// <summary>
    /// Once a day at each phone's hour: what's airing today of the shows its profile watches, and films it asked for
    /// that come out today. Nothing's sent on a day with nothing on.
    /// </summary>
    private async Task UpcomingAsync(FlexarrDbContext db, Notifier notifier, List<NotificationDevice> devices, DateTime now, CancellationToken ct)
    {
        var due = new List<(NotificationDevice Device, DateOnly Today, TimeZoneInfo Zone)>();
        foreach (var device in devices.Where(d => d.Upcoming))
        {
            var zone = TimeZoneInfo.TryFindSystemTimeZoneById(device.TimeZone, out var z) ? z : TimeZoneInfo.Utc;
            var local = TimeZoneInfo.ConvertTimeFromUtc(now, zone);
            var today = DateOnly.FromDateTime(local);
            if (device.UpcomingSentOn != today && local.Hour >= device.UpcomingHour && local.Hour < device.UpcomingHour + UpcomingWindowHours)
            {
                due.Add((device, today, zone));
            }
        }

        if (due.Count == 0)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var options = services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue;
        var arr = services.GetRequiredService<ArrClient>();
        var calendars = new Dictionary<string, List<UpcomingDto>>(StringComparer.Ordinal);
        foreach (var (device, today, zone) in due)
        {
            device.UpcomingSentOn = today;
            if (!calendars.TryGetValue(device.ProfileId, out var calendar))
            {
                calendar = await CalendarForAsync(services, options, arr, device.ProfileId, ct);
                calendars[device.ProfileId] = calendar;
            }

            var on = calendar.Where(u => !u.Available && (u.Kind == MediaKind.Movie
                    ? DateOnly.FromDateTime(u.Date) == today
                    : DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(u.Date, DateTimeKind.Utc), zone)) == today))
                .ToList();
            if (on.Count == 0)
            {
                continue;
            }

            var lines = on.Take(5).Select(u => u.Kind == MediaKind.Movie
                ? $"{u.Title} · {u.When}"
                : $"{u.SeriesTitle} · {EpisodeLabel(u.SeasonNumber, u.EpisodeNumber)}").ToList();
            if (on.Count > 5)
            {
                lines.Add($"and {on.Count - 5} more");
            }

            var single = on.Count == 1 && on[0].LibraryItemId is { } id ? new Dictionary<string, string> { ["item"] = id.ToString() } : null;
            await notifier.SendAsync(db, [device], new PushMessage("On today", string.Join("\n", lines), "flexarr.upcoming", "upcoming",
                on.Count == 1 ? Artwork(on[0].Poster) : null, single), $"upcoming:{today:yyyy-MM-dd}", ct);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>The next couple of days from Sonarr and Radarr, cut down to what this profile is following.</summary>
    private static async Task<List<UpcomingDto>> CalendarForAsync(IServiceProvider services, FlexarrOptions options, ArrClient arr, string profileId, CancellationToken ct)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.HiddenLibraryIds = await db.Profiles.Where(p => p.Id == profileId).Select(p => p.HiddenLibraryIds).FirstOrDefaultAsync(ct) ?? [];
        var calendar = await UpcomingEndpoints.UpcomingAsync(2, options, arr, null, db, null, ct);

        var showIds = calendar.Where(u => u.Kind == MediaKind.Episode && u.LibraryItemId is not null).Select(u => u.LibraryItemId!.Value).Distinct().ToList();
        var watching = (await db.PlaybackStates.Where(p => p.UserId == profileId && p.MediaItem!.Parent!.ParentId != null && showIds.Contains(p.MediaItem.Parent.ParentId.Value))
            .Select(p => p.MediaItem!.Parent!.ParentId!.Value).Distinct().ToListAsync(ct)).ToHashSet();
        var requestedShows = (await db.MediaRequests.Where(r => r.ProfileId == profileId && r.Kind == MediaKind.Series).Select(r => r.TmdbId).ToListAsync(ct))
            .Select(t => t.ToString(CultureInfo.InvariantCulture)).ToList();
        foreach (var id in await db.MediaItems.Where(i => showIds.Contains(i.Id) && i.TmdbId != null && requestedShows.Contains(i.TmdbId)).Select(i => i.Id).ToListAsync(ct))
        {
            watching.Add(id);
        }

        var requestedFilms = (await db.MediaRequests.Where(r => r.ProfileId == profileId && r.Kind == MediaKind.Movie).Select(r => r.TmdbId).ToListAsync(ct)).ToHashSet();
        return calendar.Where(u => u.Kind == MediaKind.Movie
            ? u.TmdbId is { } t && requestedFilms.Contains(t)
            : u.LibraryItemId is { } show && watching.Contains(show)).ToList();
    }

    // ---- Helpers ----

    /// <summary>
    /// The stretch since this kind was last told, up to <paramref name="to"/>, moving its cursor on (saved with the
    /// rest). Null the first time, when the cursor starts now: nothing from before notifications existed is news.
    /// </summary>
    private static async Task<(DateTime From, DateTime To)?> WindowAsync(FlexarrDbContext db, string kind, DateTime to, CancellationToken ct)
    {
        var cursor = await db.NotificationCursors.FindAsync([kind], ct);
        if (cursor is null)
        {
            db.NotificationCursors.Add(new NotificationCursor { Kind = kind, Through = to });
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (to <= cursor.Through)
        {
            return null;
        }

        var from = cursor.Through;
        cursor.Through = to;
        return (from, to);
    }

    /// <summary>Artwork the phone can fetch for itself (TMDB and the like, never this server), at a notification's size.</summary>
    private static string? Artwork(string? url) =>
        ArtworkUrl.TrustedRemote(url)?.Replace("image.tmdb.org/t/p/original/", "image.tmdb.org/t/p/w500/", StringComparison.Ordinal);

    private static Dictionary<string, string> Video(Guid item, Guid play) => new() { ["item"] = item.ToString(), ["play"] = play.ToString() };

    private static Dictionary<string, string> Podcast(Guid podcast, Guid episode) => new() { ["podcast"] = podcast.ToString(), ["episode"] = episode.ToString() };

    private static string WithYear(string title, int? year) => year is { } y ? $"{title} ({y.ToString(CultureInfo.InvariantCulture)})" : title;

    private static string EpisodeLabel(int? season, int? episode) => episode is { } e ? $"S{season ?? 1} E{e}" : season is { } s ? $"Season {s}" : "New episode";

    /// <summary>The start of an overview, cut at a word, short enough to read at a glance.</summary>
    private static string? Short(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        text = text.Trim();
        if (text.Length <= 160)
        {
            return text;
        }

        var cut = text.LastIndexOf(' ', 157);
        return string.Concat(text.AsSpan(0, cut > 80 ? cut : 157), "…");
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending notifications failed")]
    private partial void LogFailed(Exception exception);
}
