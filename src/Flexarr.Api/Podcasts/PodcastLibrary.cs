using System.Globalization;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Podcasts;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Podcasts;

/// <summary>Adding a podcast came to this: its id, or why it couldn't be added.</summary>
public sealed record PodcastAddResult(Guid? PodcastId, string? Error);

/// <summary>
/// Podcasts in the library: adding one by its feed, and bringing feeds up to date. A podcast is an item in the server's
/// Podcasts library (the first one, when there are several) with an item for each episode, so watch state, downloads
/// and artwork work as they do for everything else. Feeds are shared: subscribing is per profile.
/// </summary>
public sealed partial class PodcastLibrary(FlexarrDbContext db, PodcastFetcher fetcher, TimeProvider clock, ILogger<PodcastLibrary> logger)
{
    /// <summary>
    /// Podcasts are part of Flexarr rather than a library someone adds: there's always one Podcasts library, made on
    /// first start with a folder in the data directory (Settings › Libraries can move it, say beside the rest of the
    /// media). It can't be removed, and no second one can be added.
    /// </summary>
    public static async Task EnsureLibraryAsync(FlexarrDbContext db, Configuration.FlexarrOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);
        if (await db.Libraries.IgnoreQueryFilters().AnyAsync(l => l.Type == LibraryType.Podcasts, ct))
        {
            return;
        }

        var folder = Path.GetFullPath(Path.Combine(options.DataDirectory, "podcasts"));
        Directory.CreateDirectory(folder);
        db.Libraries.Add(new Library { Name = "Podcasts", Type = LibraryType.Podcasts, Folders = [new LibraryFolder { Path = folder }] });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The Podcasts library this profile can see; null when it's hidden from it.</summary>
    public Task<Library?> LibraryAsync(CancellationToken ct) =>
        db.Libraries.AsNoTracking().Include(l => l.Folders).Where(l => l.Type == LibraryType.Podcasts).OrderBy(l => l.CreatedAt).FirstOrDefaultAsync(ct);

    /// <summary>
    /// The podcast at this feed: the one already in the library (by its address, or its Apple id), else fetched and
    /// added. Nobody is subscribed by adding it.
    /// </summary>
    public async Task<PodcastAddResult> AddAsync(string feedUrl, long? iTunesId, string? artworkUrl, CancellationToken ct)
    {
        if (await LibraryAsync(ct) is not { } library)
        {
            return new PodcastAddResult(null, "Podcasts aren't available on this profile.");
        }

        if (FeedParser.WebUrl(feedUrl) is not { } url)
        {
            return new PodcastAddResult(null, "That isn't a web address.");
        }

        var existing = await db.PodcastFeeds.AsNoTracking()
            .Where(f => f.Url == url || (iTunesId != null && f.ITunesId == iTunesId))
            .Select(f => (Guid?)f.MediaItemId).FirstOrDefaultAsync(ct);
        if (existing is { } known)
        {
            return new PodcastAddResult(known, null);
        }

        var fetch = await fetcher.FetchFeedAsync(url, null, null, ct);
        if (fetch.Feed is not { } parsed)
        {
            return new PodcastAddResult(null, fetch.Error ?? "The feed couldn't be read.");
        }

        // Moved for good: kept under its new address, which may be one the library already has.
        var address = fetch.MovedTo ?? FeedParser.WebUrl(parsed.NewFeedUrl) ?? url;
        if (address != url && await db.PodcastFeeds.AsNoTracking().Where(f => f.Url == address).Select(f => (Guid?)f.MediaItemId).FirstOrDefaultAsync(ct) is { } moved)
        {
            return new PodcastAddResult(moved, null);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var podcast = new MediaItem
        {
            LibraryId = library.Id,
            Kind = MediaKind.Podcast,
            Title = parsed.Title,
            SortTitle = SortTitle.For(parsed.Title),
            AddedAt = now,
            MetadataSource = "Podcast feed",
        };
        var feed = new PodcastFeed { MediaItemId = podcast.Id, Url = address, ITunesId = iTunesId, ETag = fetch.ETag, LastModified = fetch.LastModified };
        db.MediaItems.Add(podcast);
        db.PodcastFeeds.Add(feed);
        Apply(podcast, feed, parsed, artworkUrl, now);
        await ApplyEpisodesAsync(podcast, feed, parsed, now, ct);
        feed.CheckedAt = now;
        feed.NextCheckAt = now + NextCheckIn(feed, now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Someone else added it in the meantime?
            db.ChangeTracker.Clear();
            if (await db.PodcastFeeds.Where(f => f.Url == address).Select(f => (Guid?)f.MediaItemId).FirstOrDefaultAsync(ct) is { } raced)
            {
                return new PodcastAddResult(raced, null);
            }

            throw;
        }

        LogAdded(parsed.Title, parsed.Episodes.Count);
        return new PodcastAddResult(podcast.Id, null);
    }

    /// <summary>
    /// The episode of a show this profile would hear next: one it's part way through, else the next unplayed (the
    /// oldest for a show heard in order, the newest otherwise), else the newest. Null when the show has no episodes.
    /// </summary>
    public static async Task<MediaItem?> NextToHearAsync(FlexarrDbContext db, Guid podcastId, string profileId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var episodes = db.MediaItems.AsNoTracking().Where(i => i.ParentId == podcastId && i.Kind == MediaKind.PodcastEpisode);
        var started = await episodes.Where(i => i.PlaybackStates.Any(p => p.UserId == profileId && !p.Watched && p.PositionSeconds > 0))
            .OrderByDescending(i => i.PlaybackStates.Where(p => p.UserId == profileId).Max(p => p.LastPlayedAt)).FirstOrDefaultAsync(ct);
        if (started is not null)
        {
            return started;
        }

        var oldestFirst = await db.PodcastSubscriptions.AnyAsync(s => s.ProfileId == profileId && s.FeedId == podcastId && s.OldestFirst, ct);
        var unplayed = episodes.Where(i => i.Enclosure!.EpisodeType != PodcastEpisodeType.Trailer && !i.PlaybackStates.Any(p => p.UserId == profileId && p.Watched));
        return await (oldestFirst ? unplayed.OrderBy(i => i.SortTitle) : unplayed.OrderByDescending(i => i.SortTitle)).FirstOrDefaultAsync(ct)
            ?? await episodes.OrderByDescending(i => i.SortTitle).FirstOrDefaultAsync(ct);
    }

    /// <summary>Fetches a feed again (unless it says it hasn't changed) and brings its podcast and episodes up to date.</summary>
    /// <returns>How many episodes were new.</returns>
    public async Task<int> RefreshAsync(Guid podcastId, CancellationToken ct)
    {
        var feed = await db.PodcastFeeds.FirstOrDefaultAsync(f => f.MediaItemId == podcastId, ct);
        var podcast = feed is null ? null : await db.MediaItems.FirstOrDefaultAsync(i => i.Id == podcastId, ct);
        if (feed is null || podcast is null)
        {
            return 0;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var fetch = await fetcher.FetchFeedAsync(feed.Url, feed.ETag, feed.LastModified, ct);
        feed.CheckedAt = now;
        var added = 0;
        if (fetch.Error is { } error)
        {
            feed.Error = error;
            feed.Failures++;
            LogRefreshFailed(feed.Url, error);
        }
        else
        {
            feed.Error = null;
            feed.Failures = 0;
            feed.ETag = fetch.ETag ?? feed.ETag;
            feed.LastModified = fetch.LastModified ?? feed.LastModified;
            if (fetch.Feed is { } parsed)
            {
                Apply(podcast, feed, parsed, null, now);
                added = await ApplyEpisodesAsync(podcast, feed, parsed, now, ct);
                if (FeedParser.WebUrl(parsed.NewFeedUrl) is { } newUrl && newUrl != feed.Url && !await db.PodcastFeeds.AnyAsync(f => f.Url == newUrl, ct))
                {
                    feed.Url = newUrl;
                    feed.ETag = feed.LastModified = null; // a new host: its answers don't carry over
                }
            }

            if (fetch.MovedTo is { } movedTo && movedTo != feed.Url && !await db.PodcastFeeds.AnyAsync(f => f.Url == movedTo, ct))
            {
                feed.Url = movedTo;
            }
        }

        feed.NextCheckAt = now + NextCheckIn(feed, now);
        await db.SaveChangesAsync(ct);
        return added;
    }

    /// <summary>
    /// How long until a feed is fetched again: soon for shows that publish often, rarely for ones that have stopped, and
    /// backing off (up to a day) from a host that keeps failing.
    /// </summary>
    public static TimeSpan NextCheckIn(PodcastFeed feed, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(feed);
        if (feed.Failures > 0)
        {
            return TimeSpan.FromMinutes(Math.Min(24 * 60, 30 * Math.Pow(2, Math.Min(feed.Failures - 1, 6))));
        }

        var quiet = feed.NewEpisodeAt is { } latest ? now - latest : TimeSpan.MaxValue;
        return quiet < TimeSpan.FromDays(14) ? TimeSpan.FromMinutes(30)
            : quiet < TimeSpan.FromDays(60) ? TimeSpan.FromHours(3)
            : TimeSpan.FromHours(12);
    }

    private static void Apply(MediaItem podcast, PodcastFeed feed, ParsedFeed parsed, string? directoryArtwork, DateTime now)
    {
        podcast.Title = parsed.Title;
        podcast.SortTitle = SortTitle.For(parsed.Title);
        podcast.Overview = parsed.Description;
        podcast.Studio = parsed.Author;
        podcast.ContentRating = parsed.Explicit ? "Explicit" : null;

        // The image cache only fetches over https; Apple's copy (from its directory) is always https, so it stands in
        // for a feed whose own artwork isn't.
        var artwork = Https(parsed.ImageUrl) ?? Https(directoryArtwork) ?? Https(podcast.PosterUrl);
        if (artwork != podcast.PosterUrl)
        {
            podcast.PosterUrl = artwork;
            podcast.AccentColor = null;
            podcast.MetadataUpdatedAt = now; // a new ?v= on its artwork links
        }

        podcast.MetadataUpdatedAt ??= now;
        feed.Link = parsed.Link;
        feed.Language = parsed.Language;
        feed.Explicit = parsed.Explicit;
        feed.PodcastGuid = parsed.PodcastGuid ?? feed.PodcastGuid;
        feed.Categories = [.. parsed.Categories.Take(6)];
    }

    /// <summary>Adds the feed's new episodes and updates the rest; episodes that have dropped off the feed are kept.</summary>
    private async Task<int> ApplyEpisodesAsync(MediaItem podcast, PodcastFeed feed, ParsedFeed parsed, DateTime now, CancellationToken ct)
    {
        var known = await db.PodcastEpisodes.Include(e => e.MediaItem).Where(e => e.FeedId == feed.MediaItemId)
            .ToDictionaryAsync(e => e.EpisodeGuid, StringComparer.Ordinal, ct);
        var added = 0;
        foreach (var episode in parsed.Episodes)
        {
            if (known.TryGetValue(episode.EpisodeGuid, out var existing) && existing.MediaItem is { } item)
            {
                Apply(item, existing, episode, now);
                continue;
            }

            item = new MediaItem
            {
                LibraryId = podcast.LibraryId,
                ParentId = podcast.Id,
                Kind = MediaKind.PodcastEpisode,
                Title = episode.Title,
                SortTitle = "",
                // When it came out, so "new episodes" lists go by the show's own dates.
                AddedAt = episode.PublishedAt is { } published && published < now ? published : now,
                MetadataSource = "Podcast feed",
                MetadataUpdatedAt = now,
            };
            var enclosure = new PodcastEpisode { MediaItemId = item.Id, FeedId = feed.MediaItemId, EpisodeGuid = episode.EpisodeGuid, AudioUrl = episode.AudioUrl };
            Apply(item, enclosure, episode, now);
            db.MediaItems.Add(item);
            db.PodcastEpisodes.Add(enclosure);
            added++;
        }

        var newest = parsed.Episodes.Select(e => e.PublishedAt).Max();
        if (newest is { } latest && (feed.NewEpisodeAt is null || latest > feed.NewEpisodeAt))
        {
            feed.NewEpisodeAt = latest < now ? latest : now;
        }

        return added;
    }

    private static void Apply(MediaItem item, PodcastEpisode enclosure, ParsedEpisode episode, DateTime now)
    {
        item.Title = episode.Title;
        // Oldest first, so the episodes' order is the show's.
        item.SortTitle = (episode.PublishedAt ?? item.AddedAt).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        item.Overview = episode.Description;
        item.AirDate = episode.PublishedAt is { } published ? DateOnly.FromDateTime(published) : null;
        item.Year = episode.PublishedAt?.Year;
        item.SeasonNumber = episode.Season;
        item.EpisodeNumber = episode.Episode;
        item.RuntimeMinutes = episode.DurationSeconds is { } seconds ? (int)Math.Round(seconds / 60) : null;
        item.ContentRating = episode.Explicit ? "Explicit" : null;
        if (Https(episode.ImageUrl) is var thumb && thumb != item.ThumbUrl)
        {
            item.ThumbUrl = thumb;
            item.MetadataUpdatedAt = now;
        }

        enclosure.AudioUrl = episode.AudioUrl;
        enclosure.MimeType = episode.MimeType;
        enclosure.LengthBytes = episode.LengthBytes;
        enclosure.PublishedAt = episode.PublishedAt;
        enclosure.DurationSeconds = episode.DurationSeconds ?? enclosure.DurationSeconds;
        enclosure.EpisodeType = episode.EpisodeType;
        enclosure.Explicit = episode.Explicit;
        enclosure.ChaptersUrl = episode.ChaptersUrl;
        enclosure.TranscriptUrl = episode.TranscriptUrl;
        enclosure.TranscriptType = episode.TranscriptType;
    }

    private static string? Https(string? url) => url is not null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? url : null;

    [LoggerMessage(Level = LogLevel.Information, Message = "Added podcast {Title} ({Episodes} episodes)")]
    private partial void LogAdded(string title, int episodes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Couldn't refresh podcast feed {Url}: {Error}")]
    private partial void LogRefreshFailed(string url, string error);
}
