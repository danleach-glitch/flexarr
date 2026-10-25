using System.Xml.Linq;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Profiles;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Podcasts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Podcasts;

/// <param name="Available">There's a Podcasts library this profile can see.</param>
/// <param name="NeedsLibrary">There's no Podcasts library at all: the main profile can add one in Settings.</param>
public sealed record PodcastStatusDto(bool Available, bool NeedsLibrary, string Country);

public sealed record PodcastSubscriptionDto(DateTime SubscribedAt);

/// <summary>A profile's podcast settings, for every show it follows.</summary>
/// <param name="AutoDownload">Phones keep the newest episodes of followed shows without being asked (on Wi-Fi).</param>
/// <param name="KeepEpisodes">How many of each show's newest unplayed episodes they keep.</param>
/// <param name="Speed">The speed podcasts play at.</param>
public sealed record PodcastPreferencesDto(bool AutoDownload, int KeepEpisodes, double Speed);

/// <summary>Changes podcast settings; fields left out keep what they were.</summary>
public sealed record PodcastPreferencesRequest(bool? AutoDownload = null, int? KeepEpisodes = null, double? Speed = null);

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

/// <param name="PodcastId">Set when the show is in the library already.</param>
public sealed record DirectoryPodcastDto(long ITunesId, string Title, string? Author, string? Artwork, string? Genre, bool Explicit, int? EpisodeCount,
    bool HasFeed, Guid? PodcastId, bool Subscribed);

/// <summary>Adds a podcast: by Apple's id (from search or the charts), or by its feed's address.</summary>
public sealed record AddPodcastRequest(string? FeedUrl = null, long? ITunesId = null);

/// <summary>An episode the phone should have without being asked, from a show it auto-downloads.</summary>
public sealed record AutoDownloadDto(Guid EpisodeId, Guid PodcastId);

public sealed record OpmlImportDto(int Added, int AlreadySubscribed, IReadOnlyList<string> Failed);

public static class PodcastEndpoints
{
    /// <summary>How much of an episode's notes lists carry.</summary>
    private const int ListOverview = 300;

    public static RouteGroupBuilder MapPodcastEndpoints(this RouteGroupBuilder api)
    {
        var group = api.MapGroup("/podcasts").WithTags("Podcasts");

        group.MapGet("/status", async (FlexarrDbContext db, IOptionsMonitor<FlexarrOptions> options, CancellationToken ct) =>
        {
            var visible = await db.Libraries.AnyAsync(l => l.Type == LibraryType.Podcasts, ct);
            var any = visible || await db.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Type == LibraryType.Podcasts, ct);
            return new PodcastStatusDto(visible, !any, options.CurrentValue.Podcasts.ResolvedCountry);
        });

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
        });

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

        // What auto-play goes on to after an episode: the show's next episode (the one after it, unplayed), else the
        // newest unplayed episode of the shows this profile follows. 204 when there's nothing left to hear.
        group.MapGet("/episodes/{id:guid}/next", async (Guid id, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            var current = await db.MediaItems.AsNoTracking().Where(i => i.Id == id && i.Kind == MediaKind.PodcastEpisode)
                .Select(i => new { i.ParentId, i.SortTitle }).FirstOrDefaultAsync(ct);
            if (current is null)
            {
                return Results.NotFound();
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

        // Follows a podcast (a body, if any, is ignored: how followed shows behave is the profile's podcast settings).
        group.MapPut("/{id:guid}/subscription", async (Guid id, FlexarrDbContext db, CurrentProfile profile, PodcastRefreshQueue queue, CancellationToken ct) =>
        {
            if (!await db.MediaItems.AnyAsync(i => i.Id == id && i.Kind == MediaKind.Podcast, ct))
            {
                return Results.NotFound();
            }

            if (!await db.PodcastSubscriptions.AnyAsync(s => s.ProfileId == profile.Id && s.FeedId == id, ct))
            {
                db.PodcastSubscriptions.Add(new PodcastSubscription { ProfileId = profile.Id, FeedId = id });
                await db.SaveChangesAsync(ct);
                queue.Enqueue(id); // caught up straight away, rather than at its next turn
            }

            return Results.Ok(await PodcastAsync(db, id, profile.Id, ct));
        });

        group.MapGet("/preferences", async (FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
            await db.Profiles.Where(p => p.Id == profile.Id).Select(p => new PodcastPreferencesDto(p.PodcastAutoDownload, p.PodcastKeepEpisodes, p.PodcastSpeed))
                .FirstOrDefaultAsync(ct) is { } preferences ? Results.Ok(preferences) : Results.NotFound());

        group.MapPut("/preferences", async (PodcastPreferencesRequest request, FlexarrDbContext db, CurrentProfile profile, CancellationToken ct) =>
        {
            if (request.KeepEpisodes is < 1 or > 50 || request.Speed is < 0.5 or > 4)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["preferences"] = ["Keep 1 to 50 episodes, at a speed from 0.5× to 4×."] });
            }

            if (await db.Profiles.FirstOrDefaultAsync(p => p.Id == profile.Id, ct) is not { } me)
            {
                return Results.NotFound();
            }

            me.PodcastAutoDownload = request.AutoDownload ?? me.PodcastAutoDownload;
            me.PodcastKeepEpisodes = request.KeepEpisodes ?? me.PodcastKeepEpisodes;
            me.PodcastSpeed = request.Speed ?? me.PodcastSpeed;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new PodcastPreferencesDto(me.PodcastAutoDownload, me.PodcastKeepEpisodes, me.PodcastSpeed));
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

            var feeds = await db.PodcastSubscriptions.AsNoTracking().Where(s => s.ProfileId == profile.Id).Select(s => s.FeedId).ToListAsync(ct);
            var wanted = new List<AutoDownloadDto>();
            foreach (var feed in feeds)
            {
                var episodes = await db.MediaItems.Where(i => i.ParentId == feed && i.Kind == MediaKind.PodcastEpisode
                        && i.Enclosure!.EpisodeType != PodcastEpisodeType.Trailer
                        && !i.PlaybackStates.Any(p => p.UserId == profile.Id && p.Watched))
                    .OrderByDescending(i => i.SortTitle).Take(settings.PodcastKeepEpisodes).Select(i => i.Id).ToListAsync(ct);
                wanted.AddRange(episodes.Select(e => new AutoDownloadDto(e, feed)));
            }

            return wanted;
        });

        group.MapGet("/search", async (string q, PodcastDirectory directory, FlexarrDbContext db, CurrentProfile profile, IOptionsMonitor<FlexarrOptions> options,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
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

            int added = 0, already = 0;
            var failed = new List<string>();
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

            return Results.Ok(new OpmlImportDto(added, already, failed));
        }).Accepts<string>("text/x-opml", "application/xml", "text/xml");

        return api;
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
                    .Select(s => new PodcastSubscriptionDto(s.SubscribedAt)).FirstOrDefault(),
                Count = i.Children.Count,
                Unplayed = i.Children.Count(e => !e.PlaybackStates.Any(p => p.UserId == profileId && p.Watched)),
                Latest = i.Children.Max(e => (DateTime?)e.AddedAt),
            })
            .Select(x => new PodcastDto(x.Item.Id, x.Item.Title, x.Item.Studio, x.Item.Overview,
                ArtworkUrls.For(x.Item.Id, x.Item.MetadataUpdatedAt == null ? 0 : x.Item.MetadataUpdatedAt.Value.Ticks / 10_000_000, x.Item.PosterUrl != null, false, false, false),
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
                ArtworkUrls.For(x.Item.Id, x.Item.MetadataUpdatedAt == null ? 0 : x.Item.MetadataUpdatedAt.Value.Ticks / 10_000_000,
                    x.Item.PosterUrl != null || x.Podcast.PosterUrl != null, false, false, x.Item.ThumbUrl != null),
                x.Item.AccentColor ?? x.Podcast.AccentColor,
                new UserDataDto(x.State == null ? 0 : x.State.PositionSeconds, x.State != null && x.State.Watched, x.State == null ? 0 : x.State.PlayCount,
                    x.State == null ? null : x.State.LastPlayedAt, null),
                x.Enclosure.LengthBytes,
                x.Enclosure.TranscriptUrl != null));

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
