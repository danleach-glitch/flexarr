using System.Xml.Linq;
using Flexarr.Api.Books;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Podcasts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Podcasts;

/// <param name="Available">There's a Podcasts library this profile can see.</param>
/// <param name="NeedsLibrary">There's no Podcasts library at all: the main profile can add one in Settings.</param>
public sealed record PodcastStatusDto(bool Available, bool NeedsLibrary, string Country);

/// <param name="OldestFirst">Heard in order: listed oldest first, and phones keep the oldest unplayed episodes.</param>
public sealed record PodcastSubscriptionDto(DateTime SubscribedAt, bool OldestFirst = false);

/// <summary>Follows a podcast, or changes how its episodes are ordered; left out, the order stays as it was.</summary>
public sealed record SubscriptionRequest(bool? OldestFirst = null);

/// <summary>A profile's podcast settings, for every show it follows.</summary>
/// <param name="AutoDownload">Phones keep the newest episodes of followed shows without being asked (on Wi-Fi).</param>
/// <param name="KeepEpisodes">How many of each show's newest unplayed episodes they keep.</param>
/// <param name="Speed">The speed podcasts play at: the profile's one speed for podcasts and books.</param>
/// <param name="ShowInSidebar">Podcasts has a place in the web app's sidebar.</param>
/// <param name="SkipBackSeconds">Seconds skip back jumps, in podcasts and books alike (from 0.58).</param>
/// <param name="SkipForwardSeconds">Seconds skip forward jumps, in podcasts and books alike (from 0.58).</param>
/// <param name="SmartRewind">After a pause, an episode or book picks up a little earlier (from 0.58).</param>
public sealed record PodcastPreferencesDto(bool AutoDownload, int KeepEpisodes, double Speed, bool ShowInSidebar = true,
    int SkipBackSeconds = 30, int SkipForwardSeconds = 30, bool SmartRewind = true)
{
    public static PodcastPreferencesDto Of(Profile p) => new(p.PodcastAutoDownload, p.PodcastKeepEpisodes, p.PodcastSpeed, p.PodcastsInSidebar,
        BookPlayback.SkipOrDefault(p.AudiobookSkipBackSeconds), BookPlayback.SkipOrDefault(p.AudiobookSkipForwardSeconds), p.AudiobookSmartRewind);
}

/// <summary>Changes podcast settings; fields left out keep what they were. Speed, skips and smart rewind are books' too.</summary>
public sealed record PodcastPreferencesRequest(bool? AutoDownload = null, int? KeepEpisodes = null, double? Speed = null, bool? ShowInSidebar = null,
    int? SkipBackSeconds = null, int? SkipForwardSeconds = null, bool? SmartRewind = null);

public sealed record PodcastDto(
    Guid Id,
    string Title,
    string? Author,
    string? Overview,
    ArtworkDto Artwork,
    string? AccentColor,
    string? Link,
    IReadOnlyList<string> Categories,
    bool Explicit,
    int EpisodeCount,
    int UnplayedCount,
    DateTime? LatestEpisodeAt,
    long? ITunesId,
    string FeedUrl,
    DateTime? CheckedAt,
    string? Error,
    PodcastSubscriptionDto? Subscription);

/// <param name="Overview">The episode's notes as plain text; cut short in lists, whole from <c>/podcasts/episodes/{id}</c>.</param>
/// <param name="SizeBytes">The file's size as the feed gives it, for showing what a download will take.</param>
public sealed record PodcastEpisodeDto(
    Guid Id,
    Guid PodcastId,
    string PodcastTitle,
    string Title,
    string? Overview,
    DateTime? PublishedAt,
    double? DurationSeconds,
    int? Season,
    int? Episode,
    PodcastEpisodeType EpisodeType,
    bool Explicit,
    ArtworkDto Artwork,
    string? AccentColor,
    UserDataDto UserData,
    long? SizeBytes,
    bool HasTranscript);

/// <summary>A named part of an episode, from the feed's chapters file or the times listed in its notes.</summary>
public sealed record PodcastChapterDto(double StartSeconds, string Title);

/// <param name="PodcastId">Set when the show is in the library already.</param>
public sealed record DirectoryPodcastDto(long ITunesId, string Title, string? Author, string? Artwork, string? Genre, bool Explicit, int? EpisodeCount,
    bool HasFeed, Guid? PodcastId, bool Subscribed);

/// <summary>Adds a podcast: by Apple's id (from search or the charts), or by its feed's address.</summary>
public sealed record AddPodcastRequest(string? FeedUrl = null, long? ITunesId = null);

/// <summary>An episode the phone should have without being asked, from a show it auto-downloads.</summary>
public sealed record AutoDownloadDto(Guid EpisodeId, Guid PodcastId);

/// <summary>An episode the phone downloaded that's been played since, wherever: the phone deletes its copy.</summary>
public sealed record PlayedDownloadDto(Guid EpisodeId);

/// <summary>Puts an episode in Up Next: first (Play next), or after the rest.</summary>
public sealed record UpNextRequest(Guid EpisodeId, bool Next = false);

/// <summary>Up Next in a new order: these episodes, in this order (any left out leave it).</summary>
public sealed record UpNextOrderRequest(IReadOnlyList<Guid> EpisodeIds);

public sealed record OpmlImportDto(int Added, int AlreadySubscribed, IReadOnlyList<string> Failed);

public static class PodcastEndpoints
{
    private static readonly SemaphoreSlim OpmlImporting = new(1, 1);

    /// <summary>How much of an episode's notes lists carry.</summary>
    private const int ListOverview = 300;

    public static RouteGroupBuilder MapPodcastEndpoints(this RouteGroupBuilder api)
    {
        // A kids profile has no podcasts (its status says they aren't available: see LibraryVisibility).
        var group = api.MapGroup("/podcasts").WithTags("Podcasts").NotForKids();

        api.MapGet("/podcasts/status", async (FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var visible = await db.Libraries.AnyAsync(l => l.Type == LibraryType.Podcasts, ct);
            var any = visible || await db.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Type == LibraryType.Podcasts, ct);
            return new PodcastStatusDto(visible, !any, options.CurrentValue.Podcasts.ResolvedCountry);
        }).WithTags("Podcasts");

        // The shows this profile follows, the most recently updated first.
        group.MapGet("/", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var subscribed = db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id).Select(s => s.FeedId);
            var podcasts = await Podcasts(db.MediaItems.Where(i => subscribed.Contains(i.Id)), db, profile.Id).ToListAsync(ct);
            return podcasts.OrderByDescending(p => p.LatestEpisodeAt).ToList();
        });

        group.MapPost("/", async (AddPodcastRequest request, PodcastLibrary library, PodcastDirectory directory, FlexarrDbContext db,
            CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var feedUrl = request.FeedUrl?.Trim();
            string? artwork = null;
            if (request.ITunesId is { } iTunesId)
            {
                if (await db.PodcastFeeds.Where(f => f.ITunesId == iTunesId).Select(f => (Guid?)f.MediaItemId).FirstOrDefaultAsync(ct) is { } known)
                {
                    return Results.Ok(await PodcastAsync(db, known, profile.Id, ct));
                }

                DirectoryPodcast? found;
                try
                {
                    found = await directory.LookupAsync(iTunesId, options.CurrentValue.Podcasts.ResolvedCountry, ct);
                }
                catch (HttpRequestException ex)
                {
                    return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
                }

                if (found?.FeedUrl is null)
                {
                    return Results.Problem("Apple's directory doesn't give a feed for this show, so it can't be added.", statusCode: StatusCodes.Status422UnprocessableEntity);
                }

                feedUrl = found.FeedUrl;
                artwork = found.ArtworkUrl;
            }

            if (string.IsNullOrEmpty(feedUrl))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["feedUrl"] = ["Give the podcast's feed address."] });
            }

            var result = await library.AddAsync(feedUrl, request.ITunesId, artwork, ct);
            return result.PodcastId is { } id
                ? Results.Ok(await PodcastAsync(db, id, profile.Id, ct))
                : Results.Problem(result.Error, statusCode: StatusCodes.Status422UnprocessableEntity, title: "The podcast couldn't be added");
        }).NotForAgeLimited();

        group.MapGet("/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, PodcastRefreshQueue queue, TimeProvider clock, CancellationToken ct) =>
        {
            if (await PodcastAsync(db, id, profile.Id, ct) is not { } podcast)
            {
                return Results.NotFound();
            }

            // Followed shows are kept up to date anyway; one that's only being looked at is checked now and then.
            if (podcast.Subscription is null && (podcast.CheckedAt is null || clock.GetUtcNow().UtcDateTime - podcast.CheckedAt > TimeSpan.FromHours(6)))
            {
                queue.Enqueue(id);
            }

            return Results.Ok(podcast);
        });

        group.MapGet("/{id:guid}/episodes", async (Guid id, int? offset, int? limit, string? sort, bool? unplayed, string? q, FlexarrDbContext db,
            CurrentProfile profile, CancellationToken ct) =>
        {
            var episodes = db.MediaItems.Where(i => i.ParentId == id && i.Kind == MediaKind.PodcastEpisode);
            if (unplayed == true)
            {
                episodes = episodes.Where(i => !i.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched));
            }

            if (!string.IsNullOrWhiteSpace(q))
            {
                var pattern = $"%{q.Trim()}%";
                episodes = episodes.Where(i => EF.Functions.Like(i.Title, pattern) || (i.Overview != null && EF.Functions.Like(i.Overview, pattern)));
            }

            var total = await episodes.CountAsync(ct);
            var ordered = sort == "oldest" ? episodes.OrderBy(i => i.SortTitle) : episodes.OrderByDescending(i => i.SortTitle);
            var page = await Episodes(ordered.Skip(Math.Max(0, offset ?? 0)).Take(Math.Clamp(limit ?? 50, 1, 200)), profile.Id, ListOverview).ToListAsync(ct);
            return new PagedDto<PodcastEpisodeDto>(total, page);
        });

        group.MapGet("/episodes/{id:guid}", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await Episodes(db.MediaItems.Where(i => i.Id == id && i.Kind == MediaKind.PodcastEpisode), profile.Id, null).FirstOrDefaultAsync(ct) is { } episode
                ? Results.Ok(episode)
                : Results.NotFound());

        // The episode's chapters: from the feed's chapters file when it has one, else from times listed in its notes.
        // Empty when there are none.
        group.MapGet("/episodes/{id:guid}/chapters", async (Guid id, FlexarrDbContext db, PodcastFetcher fetcher, IMemoryCache cache, CancellationToken ct) =>
        {
            var episode = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.Kind == MediaKind.PodcastEpisode && i.Enclosure != null)
                .Select(i => new { i.Overview, i.Enclosure!.ChaptersUrl, i.Enclosure.DurationSeconds }).FirstOrDefaultAsync(ct);
            if (episode is null)
            {
                return Results.NotFound();
            }

            var chapters = episode.ChaptersUrl is { } url ? await ChaptersFileAsync(fetcher, cache, url, ct) : [];
            if (chapters.Count == 0)
            {
                chapters = PodcastChapters.FromNotes(episode.Overview, episode.DurationSeconds);
            }

            return Results.Ok(chapters.Select(c => new PodcastChapterDto(c.StartSeconds, c.Title)).ToList());
        });

        // What auto-play goes on to after an episode: the first in Up Next, else the show's next episode (the one after
        // it, unplayed), else the newest unplayed episode of the shows this profile follows. 204 when there's nothing
        // left to hear.
        group.MapGet("/episodes/{id:guid}/next", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var current = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.Kind == MediaKind.PodcastEpisode)
                .Select(i => new { i.ParentId, i.SortTitle }).FirstOrDefaultAsync(ct);
            if (current is null)
            {
                return Results.NotFound();
            }

            if (await Episodes(UpNext(db, profile.Id).Where(i => i.Id != id), profile.Id, ListOverview).FirstOrDefaultAsync(ct) is { } queued)
            {
                return Results.Ok(queued);
            }

            var unplayed = db.MediaItems.Where(i => i.Kind == MediaKind.PodcastEpisode && i.Id != id
                && i.Enclosure!.EpisodeType != PodcastEpisodeType.Trailer
                && !i.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched));
#pragma warning disable CA1309 // translated to SQL, which compares the dates (yyyyMMddHHmmss) as they are
            var after = unplayed.Where(i => i.ParentId == current.ParentId && string.Compare(i.SortTitle, current.SortTitle) > 0).OrderBy(i => i.SortTitle);
#pragma warning restore CA1309
            var subscribed = db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id).Select(s => s.FeedId);
            var newest = unplayed.Where(i => i.ParentId != null && subscribed.Contains(i.ParentId.Value)).OrderByDescending(i => i.AddedAt);
            var next = await Episodes(after.Take(1), profile.Id, ListOverview).FirstOrDefaultAsync(ct)
                ?? await Episodes(newest.Take(1), profile.Id, ListOverview).FirstOrDefaultAsync(ct);
            return next is null ? Results.NoContent() : Results.Ok(next);
        });

        // The newest episodes of the shows this profile follows.
        group.MapGet("/new", async (int? limit, bool? unplayed, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var subscribed = db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id).Select(s => s.FeedId);
            var episodes = db.MediaItems.Where(i => i.Kind == MediaKind.PodcastEpisode && i.ParentId != null && subscribed.Contains(i.ParentId.Value));
            if (unplayed == true)
            {
                episodes = episodes.Where(i => !i.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched));
            }

            return await Episodes(episodes.OrderByDescending(i => i.AddedAt).Take(Math.Clamp(limit ?? 40, 1, 200)), profile.Id, ListOverview).ToListAsync(ct);
        });

        // Episodes this profile is part way through.
        group.MapGet("/continue", async (int? limit, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var started = db.PlaybackStates.Where(p => p.UserId == profile.Id && !p.Watched && p.PositionSeconds > 0 && p.MediaItem!.Kind == MediaKind.PodcastEpisode)
                .OrderByDescending(p => p.LastPlayedAt).Select(p => p.MediaItemId).Take(Math.Clamp(limit ?? 20, 1, 100));
            var episodes = await Episodes(db.MediaItems.Where(i => started.Contains(i.Id)), profile.Id, ListOverview).ToListAsync(ct);
            return episodes.OrderByDescending(e => e.UserData.LastPlayedAt).ToList();
        });

        // Follows a podcast, or sets the order its episodes are heard in. How followed shows behave otherwise is the
        // profile's podcast settings.
        group.MapPut("/{id:guid}/subscription", async (Guid id, SubscriptionRequest? request, FlexarrDbContext db, CurrentProfile profile, PodcastRefreshQueue queue,
            CancellationToken ct) =>
        {
            if (!await db.MediaItems.AnyAsync(i => i.Id == id && i.Kind == MediaKind.Podcast, ct))
            {
                return Results.NotFound();
            }

            var subscription = await db.PodcastSubscriptions.FirstOrDefaultAsync(s => s.ProfileId == profile.Id && s.FeedId == id, ct);
            if (subscription is null)
            {
                subscription = new PodcastSubscription { ProfileId = profile.Id, FeedId = id };
                db.PodcastSubscriptions.Add(subscription);
                queue.Enqueue(id); // caught up straight away, rather than at its next turn
            }

            subscription.OldestFirst = request?.OldestFirst ?? subscription.OldestFirst;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await PodcastAsync(db, id, profile.Id, ct));
        });

        group.MapGet("/preferences", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.Id == profile.Id, ct) is { } me ? Results.Ok(PodcastPreferencesDto.Of(me)) : Results.NotFound());

        group.MapPut("/preferences", async (PodcastPreferencesRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var steps = BookPlayback.SkipSteps;
            if (request.KeepEpisodes is < 1 or > 50 || request.Speed is { } s && (!double.IsFinite(s) || s is < 0.5 or > 4)
                || request.SkipBackSeconds is { } b && !steps.Contains(b) || request.SkipForwardSeconds is { } f && !steps.Contains(f))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["preferences"] = ["Keep 1 to 50 episodes, at a speed from 0.5× to 4×; skips are 10, 15, 30, 45 or 60 seconds."],
                });
            }

            if (await db.Profiles.FirstOrDefaultAsync(p => p.Id == profile.Id, ct) is not { } me)
            {
                return Results.NotFound();
            }

            me.PodcastAutoDownload = request.AutoDownload ?? me.PodcastAutoDownload;
            me.PodcastKeepEpisodes = request.KeepEpisodes ?? me.PodcastKeepEpisodes;
            // One speed for podcasts and books: older apps setting either keep the other in step.
            if (request.Speed is { } speed)
            {
                BookPlayback.SetSharedSpeed(me, speed);
            }

            me.PodcastsInSidebar = request.ShowInSidebar ?? me.PodcastsInSidebar;
            me.AudiobookSkipBackSeconds = request.SkipBackSeconds ?? me.AudiobookSkipBackSeconds;
            me.AudiobookSkipForwardSeconds = request.SkipForwardSeconds ?? me.AudiobookSkipForwardSeconds;
            me.AudiobookSmartRewind = request.SmartRewind ?? me.AudiobookSmartRewind;
            await db.SaveChangesAsync(ct);
            return Results.Ok(PodcastPreferencesDto.Of(me));
        });

        group.MapDelete("/{id:guid}/subscription", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            await db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id && s.FeedId == id).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        // Off the server for everyone: subscriptions, episodes and their history with it.
        group.MapDelete("/{id:guid}", async (Guid id, FlexarrDbContext db, CancellationToken ct) =>
            await db.MediaItems.Where(i => i.Id == id && i.Kind == MediaKind.Podcast).ExecuteDeleteAsync(ct) > 0 ? Results.NoContent() : Results.NotFound())
            .RequireAuthorization(Security.FlexarrClaims.AdminPolicy);

        group.MapPost("/{id:guid}/refresh", async (Guid id, PodcastLibrary library, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!await db.MediaItems.AnyAsync(i => i.Id == id && i.Kind == MediaKind.Podcast, ct))
            {
                return Results.NotFound();
            }

            await library.RefreshAsync(id, ct);
            return Results.Ok(await PodcastAsync(db, id, profile.Id, ct));
        });

        // What a phone should have without being asked: the newest unplayed episodes of each show it auto-downloads.
        group.MapGet("/auto-downloads", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var settings = await db.Profiles.AsNoTracking().Where(p => p.Id == profile.Id)
                .Select(p => new { p.PodcastAutoDownload, p.PodcastKeepEpisodes }).FirstOrDefaultAsync(ct);
            if (settings is not { PodcastAutoDownload: true })
            {
                return [];
            }

            var feeds = await db.PodcastSubscriptions.AsNoTracking().Where(s => s.ProfileId == profile.Id)
                .Select(s => new { s.FeedId, s.OldestFirst }).ToListAsync(ct);
            var wanted = new List<AutoDownloadDto>();
            foreach (var feed in feeds)
            {
                // Never one taken off the phone: that was a choice, not a gap to fill.
                var unplayed = db.MediaItems.Where(i => i.ParentId == feed.FeedId && i.Kind == MediaKind.PodcastEpisode
                    && i.Enclosure!.EpisodeType != PodcastEpisodeType.Trailer
                    && !i.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched)
                    && !db.Downloads.Any(d => d.ProfileId == profile.Id && d.MediaItemId == i.Id && d.Status == DownloadStatus.Removed));
                // A show heard in order keeps the next ones to hear, not the newest.
                var episodes = await (feed.OldestFirst ? unplayed.OrderBy(i => i.SortTitle) : unplayed.OrderByDescending(i => i.SortTitle))
                    .Take(settings.PodcastKeepEpisodes).Select(i => i.Id).ToListAsync(ct);
                wanted.AddRange(episodes.Select(e => new AutoDownloadDto(e, feed.FeedId)));
            }

            return wanted;
        });

        // Downloaded episodes played since (on another device, say, or marked played): the phone needn't keep them. One
        // played before it was downloaded was downloaded to hear again, so it stays.
        group.MapGet("/played-downloads", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.Downloads.AsNoTracking()
                .Where(d => d.ProfileId == profile.Id && d.Status != DownloadStatus.Removed && d.MediaItem!.Kind == MediaKind.PodcastEpisode
                    && d.MediaItem.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched && p.LastPlayedAt > d.CreatedAt))
                .Select(d => d.MediaItemId).Distinct().Select(id => new PlayedDownloadDto(id)).ToListAsync(ct));

        // Up Next: the episodes to hear after this one, the same on every device. Played ones drop out.
        group.MapGet("/queue", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await Episodes(UpNext(db, profile.Id), profile.Id, ListOverview).ToListAsync(ct));

        group.MapPost("/queue", async (UpNextRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (!await db.MediaItems.AnyAsync(i => i.Id == request.EpisodeId && i.Kind == MediaKind.PodcastEpisode, ct))
            {
                return Results.NotFound();
            }

            var queue = await db.PodcastQueue.Where(q => q.ProfileId == profile.Id).OrderBy(q => q.Position).ToListAsync(ct);
            var entry = queue.FirstOrDefault(q => q.EpisodeId == request.EpisodeId);
            if (entry is null)
            {
                entry = new PodcastQueueEntry { ProfileId = profile.Id, EpisodeId = request.EpisodeId };
                db.PodcastQueue.Add(entry);
            }
            else
            {
                queue.Remove(entry);
            }

            queue.Insert(request.Next ? 0 : queue.Count, entry);
            Renumber(queue);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await Episodes(UpNext(db, profile.Id), profile.Id, ListOverview).ToListAsync(ct));
        });

        group.MapPut("/queue", async (UpNextOrderRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var queue = await db.PodcastQueue.Where(q => q.ProfileId == profile.Id).ToListAsync(ct);
            var order = request.EpisodeIds.Distinct().ToList();
            db.PodcastQueue.RemoveRange(queue.Where(q => !order.Contains(q.EpisodeId)));
            var known = await db.MediaItems.Where(i => order.Contains(i.Id) && i.Kind == MediaKind.PodcastEpisode).Select(i => i.Id).ToListAsync(ct);
            var kept = order.Where(known.Contains).Select(id => queue.FirstOrDefault(q => q.EpisodeId == id)
                ?? db.PodcastQueue.Add(new PodcastQueueEntry { ProfileId = profile.Id, EpisodeId = id }).Entity).ToList();
            Renumber(kept);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await Episodes(UpNext(db, profile.Id), profile.Id, ListOverview).ToListAsync(ct));
        });

        group.MapDelete("/queue/{episodeId:guid}", async (Guid episodeId, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            await db.PodcastQueue.Where(q => q.ProfileId == profile.Id && q.EpisodeId == episodeId).ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/search", async (string q, PodcastDirectory directory, FlexarrDbContext db, CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options,
            CancellationToken ct) =>
        {
            // Apple's directory has no ages to check: a profile with an age limit finds nothing in it.
            if (string.IsNullOrWhiteSpace(q) || db.IsLimited())
            {
                return Results.Ok(Array.Empty<DirectoryPodcastDto>());
            }

            try
            {
                var found = await directory.SearchAsync(q.Trim(), options.CurrentValue.Podcasts.ResolvedCountry, ct);
                return Results.Ok(await DirectoryAsync(db, profile.Id, found, ct));
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/top", async (int? genre, PodcastDirectory directory, FlexarrDbContext db, CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options,
            CancellationToken ct) =>
        {
            if (db.IsLimited())
            {
                return Results.Ok(Array.Empty<DirectoryPodcastDto>());
            }

            try
            {
                var found = await directory.TopAsync(options.CurrentValue.Podcasts.ResolvedCountry, genre, ct);
                return Results.Ok(await DirectoryAsync(db, profile.Id, found, ct));
            }
            catch (HttpRequestException ex)
            {
                return Results.Problem(ex.Message, statusCode: StatusCodes.Status502BadGateway);
            }
        });

        group.MapGet("/genres", () => PodcastDirectory.Genres);

        group.MapGet("/countries", () => PodcastCountries.All);

        group.MapGet("/opml", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var feeds = await db.PodcastSubscriptions.Where(s => s.ProfileId == profile.Id)
                .Select(s => new { s.Feed!.Url, s.Feed.MediaItem!.Title }).OrderBy(f => f.Title).ToListAsync(ct);
            var opml = new XDocument(new XElement("opml", new XAttribute("version", "2.0"),
                new XElement("head", new XElement("title", "Flexarr podcasts")),
                new XElement("body", feeds.Select(f => new XElement("outline", new XAttribute("type", "rss"), new XAttribute("text", f.Title),
                    new XAttribute("title", f.Title), new XAttribute("xmlUrl", f.Url))))));
            return Results.Text(opml.Declaration + opml.ToString(), "text/x-opml", System.Text.Encoding.UTF8);
        }).Produces<string>(contentType: "text/x-opml");

        // Subscriptions from another app's export: each feed is added (if need be) and followed.
        group.MapPost("/opml", async (HttpRequest http, PodcastLibrary library, FlexarrDbContext db, CurrentProfile profile, PodcastRefreshQueue queue, CancellationToken ct) =>
        {
            if (await library.LibraryAsync(ct) is null)
            {
                return Results.Problem("Add a Podcasts library in Settings first.", statusCode: StatusCodes.Status409Conflict);
            }

            List<string> urls;
            try
            {
                using var body = new MemoryStream();
                await PodcastFetcher.CopyLimitedAsync(http.Body, body, 5 * 1024 * 1024, ct);
                body.Position = 0;
                urls = Opml.FeedUrls(body);
            }
            catch (FormatException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["opml"] = [ex.Message] });
            }

            // One import at a time on the server: each can fetch hundreds of feeds, so several at once could tie the server
            // (and its connection) up for a long while.
            if (!await OpmlImporting.WaitAsync(0, ct))
            {
                return Results.Problem("Another podcast import is still running. Try again when it's finished.", statusCode: StatusCodes.Status429TooManyRequests);
            }

            int added = 0, already = 0;
            var failed = new List<string>();
            try
            {
                foreach (var url in urls.Take(500))
                {
                    var result = await library.AddAsync(url, null, null, ct);
                    if (result.PodcastId is not { } id)
                    {
                        failed.Add(url);
                        continue;
                    }

                    if (await db.PodcastSubscriptions.AnyAsync(s => s.ProfileId == profile.Id && s.FeedId == id, ct))
                    {
                        already++;
                        continue;
                    }

                    db.PodcastSubscriptions.Add(new PodcastSubscription { ProfileId = profile.Id, FeedId = id });
                    await db.SaveChangesAsync(ct);
                    added++;
                }
            }
            finally
            {
                OpmlImporting.Release();
            }

            return Results.Ok(new OpmlImportDto(added, already, failed));
        }).Accepts<string>("text/x-opml", "application/xml", "text/xml").NotForAgeLimited();

        return api;
    }

    /// <summary>This profile's Up Next, in order, without the episodes it has played since they were added.</summary>
    public static IQueryable<MediaItem> UpNext(FlexarrDbContext db, string profileId) =>
        db.PodcastQueue.Where(q => q.ProfileId == profileId && !q.Episode!.PlaybackStates.Any(p => p.UserId == profileId && p.Watched))
            .OrderBy(q => q.Position).Select(q => q.Episode!);

    private static void Renumber(List<PodcastQueueEntry> queue)
    {
        for (var i = 0; i < queue.Count; i++)
        {
            queue[i].Position = i;
        }
    }

    private static async Task<PodcastDto?> PodcastAsync(FlexarrDbContext db, Guid id, string profileId, CancellationToken ct) =>
        await Podcasts(db.MediaItems.Where(i => i.Id == id), db, profileId).FirstOrDefaultAsync(ct);

    private static IQueryable<PodcastDto> Podcasts(IQueryable<MediaItem> items, FlexarrDbContext db, string profileId) =>
        items.Where(i => i.Kind == MediaKind.Podcast && i.Feed != null)
            .Select(i => new
            {
                Item = i,
                Feed = i.Feed!,
                Subscription = db.PodcastSubscriptions.Where(s => s.ProfileId == profileId && s.FeedId == i.Id)
                    .Select(s => new PodcastSubscriptionDto(s.SubscribedAt, s.OldestFirst)).FirstOrDefault(),
                Count = i.Children.Count,
                Unplayed = i.Children.Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)),
                Latest = i.Children.Max(e => (DateTime?)e.AddedAt),
            })
            .Select(x => new PodcastDto(x.Item.Id, x.Item.Title, x.Item.Studio, x.Item.Overview,
                ArtworkUrls.For(x.Item.Id, ArtworkUrls.Version(x.Item.MetadataUpdatedAt, x.Item.ArtworkChangedAt), x.Item.PosterUrl != null, false, false, false),
                x.Item.AccentColor, x.Feed.Link, x.Feed.Categories, x.Feed.Explicit, x.Count, x.Unplayed, x.Latest, x.Feed.ITunesId, x.Feed.Url, x.Feed.CheckedAt,
                x.Feed.Error, x.Subscription));

    /// <param name="overview">How much of the notes to carry; null for all of them.</param>
    public static IQueryable<PodcastEpisodeDto> Episodes(IQueryable<MediaItem> items, string profileId, int? overview) =>
        items.Where(i => i.Enclosure != null && i.Parent != null)
            .Select(i => new
            {
                Item = i,
                Enclosure = i.Enclosure!,
                Podcast = i.Parent!,
                State = i.PlaybackStates.Where(p => p.UserId == profileId).Select(p => new { p.PositionSeconds, p.Watched, p.PlayCount, p.LastPlayedAt }).FirstOrDefault(),
            })
            .Select(x => new PodcastEpisodeDto(
                x.Item.Id,
                x.Podcast.Id,
                x.Podcast.Title,
                x.Item.Title,
#pragma warning disable CA1845 // translated to SQL, where there are no spans
                overview == null || x.Item.Overview == null || x.Item.Overview.Length <= overview ? x.Item.Overview : x.Item.Overview.Substring(0, overview.Value) + "…",
#pragma warning restore CA1845
                x.Enclosure.PublishedAt,
                x.Enclosure.DurationSeconds,
                x.Item.SeasonNumber,
                x.Item.EpisodeNumber,
                x.Enclosure.EpisodeType,
                x.Enclosure.Explicit,
                ArtworkUrls.For(x.Item.Id, ArtworkUrls.Version(x.Item.MetadataUpdatedAt, x.Item.ArtworkChangedAt),
                    x.Item.PosterUrl != null || x.Podcast.PosterUrl != null, false, false, x.Item.ThumbUrl != null),
                x.Item.AccentColor ?? x.Podcast.AccentColor,
                new UserDataDto(x.State == null ? 0 : x.State.PositionSeconds, x.State != null && x.State.Watched, x.State == null ? 0 : x.State.PlayCount,
                    x.State == null ? null : x.State.LastPlayedAt, null),
                x.Enclosure.LengthBytes,
                x.Enclosure.TranscriptUrl != null));

    /// <summary>
    /// The chapters in a feed's chapters file, kept for a while so playing an episode again doesn't fetch it again. A
    /// file that can't be read counts as none, and is tried again later.
    /// </summary>
    private static async Task<IReadOnlyList<PodcastChapter>> ChaptersFileAsync(PodcastFetcher fetcher, IMemoryCache cache, string url, CancellationToken ct)
    {
        var key = "podcast-chapters:" + url;
        if (cache.TryGetValue(key, out IReadOnlyList<PodcastChapter>? cached) && cached is not null)
        {
            return cached;
        }

        var json = await fetcher.FetchTextAsync(url, PodcastChapters.MaxFileBytes, ct);
        var chapters = json is null ? [] : PodcastChapters.FromJson(json);
        cache.Set(key, chapters, json is null ? TimeSpan.FromMinutes(10) : TimeSpan.FromHours(6));
        return chapters;
    }

    /// <summary>Directory results, with whether each is in the library already and followed by this profile.</summary>
    private static async Task<List<DirectoryPodcastDto>> DirectoryAsync(FlexarrDbContext db, string profileId, IReadOnlyList<DirectoryPodcast> found, CancellationToken ct)
    {
        var ids = found.Select(p => p.ITunesId).ToList();
        var urls = found.Where(p => p.FeedUrl is not null).Select(p => p.FeedUrl!).ToList();
        var inLibrary = await db.PodcastFeeds.Where(f => (f.ITunesId != null && ids.Contains(f.ITunesId.Value)) || urls.Contains(f.Url))
            .Select(f => new { f.MediaItemId, f.ITunesId, f.Url, Subscribed = db.PodcastSubscriptions.Any(s => s.ProfileId == profileId && s.FeedId == f.MediaItemId) })
            .ToListAsync(ct);
        return found.Select(p =>
        {
            var match = inLibrary.FirstOrDefault(f => f.ITunesId == p.ITunesId || f.Url == p.FeedUrl);
            return new DirectoryPodcastDto(p.ITunesId, p.Title, p.Author, ImageEndpoints.RemoteUrl(p.ArtworkUrl), p.Genre, p.Explicit, p.EpisodeCount,
                p.FeedUrl is not null, match?.MediaItemId, match?.Subscribed == true);
        }).ToList();
    }
}

/// <summary>Reads OPML, the file podcast apps export subscriptions as.</summary>
public static class Opml
{
    /// <exception cref="FormatException">It isn't OPML.</exception>
    public static List<string> FeedUrls(Stream opml)
    {
        XDocument document;
        try
        {
            using var reader = System.Xml.XmlReader.Create(opml, new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Ignore, XmlResolver = null });
            document = XDocument.Load(reader);
        }
        catch (System.Xml.XmlException ex)
        {
            throw new FormatException("That file isn't OPML: it isn't valid XML.", ex);
        }

        if (document.Root?.Name.LocalName != "opml")
        {
            throw new FormatException("That file isn't OPML.");
        }

        return document.Descendants("outline")
            .Select(o => FeedParser.WebUrl(o.Attribute("xmlUrl")?.Value))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
