using System.Threading.Channels;
using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Podcasts;

/// <summary>Podcasts someone is looking at, to be brought up to date soon though nobody subscribes to them.</summary>
public sealed class PodcastRefreshQueue
{
    private readonly Channel<Guid> _queue = Channel.CreateBounded<Guid>(new BoundedChannelOptions(100) { FullMode = BoundedChannelFullMode.DropWrite });

    public void Enqueue(Guid podcastId) => _queue.Writer.TryWrite(podcastId);

    public bool TryDequeue(out Guid podcastId) => _queue.Reader.TryRead(out podcastId);
}

/// <summary>
/// Keeps subscribed feeds up to date, each as often as its show publishes (see <see cref="PodcastLibrary.NextCheckIn"/>),
/// and clears out podcasts that were only looked at: after a month with nobody subscribed and nothing played or
/// downloaded, they go.
/// </summary>
public sealed partial class PodcastRefreshWorker(IServiceScopeFactory scopes, PodcastRefreshQueue queue, TimeProvider clock, ILogger<PodcastRefreshWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan UnsubscribedKeptFor = TimeSpan.FromDays(30);

    /// <summary>Feeds fetched each minute at most, so a library of hundreds catches up without a burst.</summary>
    private const int PerTick = 20;

    private DateTime _prunedAt;

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
        var now = clock.GetUtcNow().UtcDateTime;

        var due = new List<Guid>();
        while (due.Count < PerTick && queue.TryDequeue(out var asked))
        {
            due.Add(asked);
        }

        due.AddRange(await db.PodcastFeeds.AsNoTracking()
            .Where(f => f.NextCheckAt <= now && db.PodcastSubscriptions.Any(s => s.FeedId == f.MediaItemId))
            .OrderBy(f => f.NextCheckAt).Select(f => f.MediaItemId).Take(PerTick).ToListAsync(ct));

        foreach (var id in due.Distinct())
        {
            // Each in a scope of its own, so one feed's thousands of episodes don't stay tracked for the next.
            await using var feedScope = scopes.CreateAsyncScope();
            var library = feedScope.ServiceProvider.GetRequiredService<PodcastLibrary>();
            var added = await library.RefreshAsync(id, ct);
            if (added > 0)
            {
                LogNewEpisodes(id, added);
            }
        }

        if (now - _prunedAt > TimeSpan.FromHours(6))
        {
            _prunedAt = now;
            await PruneAsync(db, now, ct);
        }
    }

    private async Task PruneAsync(FlexarrDbContext db, DateTime now, CancellationToken ct)
    {
        var before = now - UnsubscribedKeptFor;
        var removed = await db.MediaItems.IgnoreQueryFilters()
            .Where(i => i.Kind == Core.Media.MediaKind.Podcast && i.AddedAt < before
                && !db.PodcastSubscriptions.Any(s => s.FeedId == i.Id)
                && !db.PlaybackStates.Any(p => p.MediaItem!.ParentId == i.Id && p.LastPlayedAt > before)
                && !db.Downloads.Any(d => d.MediaItem!.ParentId == i.Id))
            .ExecuteDeleteAsync(ct);
        if (removed > 0)
        {
            LogPruned(removed);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Podcast {Id} has {Count} new episodes")]
    private partial void LogNewEpisodes(Guid id, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Removed {Count} podcasts nobody subscribes to")]
    private partial void LogPruned(int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "Refreshing podcasts failed")]
    private partial void LogFailed(Exception exception);
}
