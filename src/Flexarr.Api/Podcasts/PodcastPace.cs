using System.Threading.RateLimiting;

namespace Flexarr.Api.Podcasts;

/// <summary>
/// How fast one profile can bring new feeds onto the server, and how many shows it can follow. Each new feed is a
/// download (up to <see cref="PodcastFetcher"/>'s limit) and thousands of episodes in the database, so a profile can't
/// add them without end.
/// </summary>
public sealed class PodcastPace : IDisposable
{
    /// <summary>New feeds one profile can add in an hour. Feeds already on the server don't count.</summary>
    public const int NewFeedsPerHour = 60;

    /// <summary>The most shows one profile follows.</summary>
    public const int MaxFollowed = 1000;

    /// <summary>A feed checked this recently isn't fetched again when someone asks it to be.</summary>
    public TimeSpan RefreshAtMostEvery { get; set; } = TimeSpan.FromSeconds(30);

    private readonly PartitionedRateLimiter<string> _adds = PartitionedRateLimiter.Create<string, string>(profile =>
        RateLimitPartition.GetSlidingWindowLimiter(profile, _ => new SlidingWindowRateLimiterOptions
        {
            PermitLimit = NewFeedsPerHour,
            Window = TimeSpan.FromHours(1),
            SegmentsPerWindow = 6,
            QueueLimit = 0,
            AutoReplenishment = true,
        }));

    /// <summary>Takes one of the profile's new feeds for this hour; false when it's had them all.</summary>
    public bool TryAdd(string profileId)
    {
        using var lease = _adds.AttemptAcquire(profileId);
        return lease.IsAcquired;
    }

    public void Dispose() => _adds.Dispose();
}
