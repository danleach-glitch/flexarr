using Flexarr.Core.Media;

namespace Flexarr.Core.Podcasts;

/// <summary>
/// A podcast's RSS feed, shared by everyone who subscribes to it. The podcast itself is a <see cref="MediaItem"/> of
/// kind <see cref="MediaKind.Podcast"/> (this row's <see cref="MediaItemId"/>), with an item for each episode under it.
/// </summary>
public sealed class PodcastFeed
{
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>Where the feed is fetched from; a permanent redirect or an <c>itunes:new-feed-url</c> moves it.</summary>
    public required string Url { get; set; }

    /// <summary>The show's id in Apple's directory, when it was found there.</summary>
    public long? ITunesId { get; set; }

    /// <summary>Podcasting 2.0's <c>podcast:guid</c>: the show's id whatever host it moves to.</summary>
    public string? PodcastGuid { get; set; }

    /// <summary>The website the feed links to.</summary>
    public string? Link { get; set; }

    public string? Language { get; set; }
    public bool Explicit { get; set; }

    /// <summary>Apple's categories for it, as the feed gives them ("History", "Society &amp; Culture", "Documentary").</summary>
    public List<string> Categories { get; set; } = [];

    /// <summary>What the last fetch answered with, sent back so an unchanged feed isn't downloaded again.</summary>
    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    public DateTime? CheckedAt { get; set; }

    /// <summary>When the feed last had a new episode, which sets how often it's checked.</summary>
    public DateTime? NewEpisodeAt { get; set; }

    public DateTime NextCheckAt { get; set; } = DateTime.UtcNow;

    /// <summary>Why the last fetch failed, in words for the viewer; null once one works.</summary>
    public string? Error { get; set; }

    /// <summary>Fetches in a row that failed, for backing off.</summary>
    public int Failures { get; set; }
}

public enum PodcastEpisodeType
{
    Full,
    Trailer,
    Bonus,
}

/// <summary>
/// A podcast episode's sound file, on the podcast's host, and what the feed says about it. The episode itself is a
/// <see cref="MediaItem"/> of kind <see cref="MediaKind.PodcastEpisode"/> (this row's <see cref="MediaItemId"/>).
/// </summary>
public sealed class PodcastEpisode
{
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    /// <summary>The podcast it's from (its <see cref="PodcastFeed.MediaItemId"/>).</summary>
    public Guid FeedId { get; set; }

    /// <summary>The feed's id for it (its &lt;guid&gt;, else the sound file's address), which says it's the same episode.</summary>
    public required string EpisodeGuid { get; set; }

    public required string AudioUrl { get; set; }
    public string? MimeType { get; set; }

    /// <summary>The file's size as the feed gives it; often missing or wrong.</summary>
    public long? LengthBytes { get; set; }

    public DateTime? PublishedAt { get; set; }
    public double? DurationSeconds { get; set; }
    public PodcastEpisodeType EpisodeType { get; set; }
    public bool Explicit { get; set; }

    /// <summary>Podcasting 2.0's chapters (<c>podcast:chapters</c>, JSON).</summary>
    public string? ChaptersUrl { get; set; }

    /// <summary>Podcasting 2.0's transcript (<c>podcast:transcript</c>), preferring SRT or WebVTT.</summary>
    public string? TranscriptUrl { get; set; }

    public string? TranscriptType { get; set; }
}

/// <summary>
/// A profile following a podcast: its new episodes show up for them, and their phone fetches them (as the profile's
/// podcast settings say, <see cref="Media.Profile.PodcastAutoDownload"/>).
/// </summary>
public sealed class PodcastSubscription
{
    public required string ProfileId { get; set; }

    /// <summary>The podcast (its <see cref="PodcastFeed.MediaItemId"/>).</summary>
    public Guid FeedId { get; set; }

    public PodcastFeed? Feed { get; set; }

    public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// The show is heard in order (a serial): its episodes list oldest first, and Play and phones' downloads take the
    /// oldest unplayed ones rather than the newest.
    /// </summary>
    public bool OldestFirst { get; set; }
}
