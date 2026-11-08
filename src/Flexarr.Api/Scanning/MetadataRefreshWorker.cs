using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Scanning;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Scanning;

/// <summary>
/// One at a time with scans: a scan and a timed refresh never write the same titles together. A refresh only holds a
/// scan up for one small batch.
/// </summary>
public sealed class LibraryWorkGate : IDisposable
{
    private readonly SemaphoreSlim _one = new(1, 1);

    public void Dispose() => _one.Dispose();

    public async Task<IDisposable> EnterAsync(CancellationToken ct)
    {
        await _one.WaitAsync(ct).ConfigureAwait(false);
        return new Leave(_one);
    }

    private sealed class Leave(SemaphoreSlim one) : IDisposable
    {
        private int _left;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _left, 1) == 0)
            {
                one.Release();
            }
        }
    }
}

/// <summary>
/// Refreshes titles that have gone out of date a few at a time, the longest due first, on a timer of its own. Every
/// title in a library falls due a week after the last refresh; refreshed inside one scan, that held up every scan
/// after it (a new episode's, say) for as long as the whole library took.
/// </summary>
public sealed partial class MetadataRefreshWorker(IServiceScopeFactory scopes, LibraryWorkGate gate, ILogger<MetadataRefreshWorker> logger) : BackgroundService
{
    /// <summary>The most titles of each kind one library refreshes at each tick.</summary>
    public const int BatchSize = 25;

    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    /// <summary>How long titles left due (their Radarr or Sonarr didn't answer), or a library whose refresh failed, wait.</summary>
    private static readonly TimeSpan RetryAfter = TimeSpan.FromHours(1);

    private readonly Dictionary<Guid, (DateTime Until, HashSet<Guid> Titles)> _leftDue = [];
    private readonly Dictionary<Guid, DateTime> _failedUntil = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RefreshDueAsync(stoppingToken);
        }
    }

    /// <summary>One tick: each library's longest-due titles.</summary>
    public async Task RefreshDueAsync(CancellationToken ct)
    {
        List<Library> libraries;
        await using (var scope = scopes.CreateAsyncScope())
        {
            try
            {
                libraries = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Libraries.AsNoTracking()
                    .Where(l => l.Type != LibraryType.Podcasts).ToListAsync(ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                LogFailed(ex, "every library");
                return;
            }
        }

        foreach (var library in libraries)
        {
            var now = DateTime.UtcNow;
            if (_failedUntil.TryGetValue(library.Id, out var until) && until > now)
            {
                continue;
            }

            if (_leftDue.TryGetValue(library.Id, out var left) && left.Until <= now)
            {
                _leftDue.Remove(library.Id);
                left = default;
            }

            using var entered = await gate.EnterAsync(ct);
            await using var scope = scopes.CreateAsyncScope();
            var refresher = scope.ServiceProvider.GetRequiredService<MetadataRefresher>();
            try
            {
                var started = TimeProvider.System.GetTimestamp();
                var refreshed = await refresher.RefreshDueAsync(library, BatchSize, (IReadOnlyCollection<Guid>?)left.Titles ?? [], ct);
                if (refresher.LeftDue.Count > 0)
                {
                    var titles = left.Titles ?? [];
                    titles.UnionWith(refresher.LeftDue);
                    _leftDue[library.Id] = (left.Titles is null ? now + RetryAfter : left.Until, titles);
                }

                if (refreshed.FromArr + refreshed.FromLookup + refreshed.FromLocal + refreshed.Unmatched > 0)
                {
                    LogRefreshed(library.Name, refreshed.FromArr, refreshed.FromLookup, refreshed.FromLocal, refreshed.Unmatched,
                        TimeProvider.System.GetElapsedTime(started).TotalSeconds);

                    // As after a scan: a film or show back under a new folder only has its id once Radarr or Sonarr matched it.
                    var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                    await WatchHistoryCarryOver.CarryOverAsync(db, ct);
                    await TraktHolds.ApplyAsync(db, ct);
                }
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _failedUntil[library.Id] = now + RetryAfter;
                LogFailed(ex, library.Name);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Refreshed out-of-date metadata for {Library}: {FromArr} from *arr, {FromLookup} looked up, {FromLocal} from local files, {Unmatched} unmatched in {Seconds:F1}s")]
    private partial void LogRefreshed(string library, int fromArr, int fromLookup, int fromLocal, int unmatched, double seconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't refresh out-of-date metadata for {Library}")]
    private partial void LogFailed(Exception ex, string library);
}
