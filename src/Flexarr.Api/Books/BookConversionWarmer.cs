using Flexarr.Core.Data;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Books;

/// <summary>
/// Books someone is part-way through whose files not every player can play (anything but MP3 and AAC) have the file at
/// the listener's place, and the next, converted ahead of time, so pressing Play doesn't wait for a whole file to
/// convert. Shortly after the server starts, then every half hour; a couple of files at a time, the most recently
/// played books first, and never while anything plays (a real Play's conversion mustn't queue behind these).
/// </summary>
public sealed partial class BookConversionWarmer(IServiceScopeFactory scopes, BookFileConverter converter, TimeProvider clock,
    ILogger<BookConversionWarmer> logger, Playback.PlaybackSessions? sessions = null) : BackgroundService
{
    /// <summary>Most books looked at each time (across profiles).</summary>
    public const int MaxBooks = 6;

    /// <summary>Most files started converting each time.</summary>
    public const int MaxFiles = 2;

    /// <summary>A book played longer ago than this isn't in Continue listening any more.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromDays(30);

    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), clock, stoppingToken); // after startup's own work
        using var timer = new PeriodicTimer(Every, clock);
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

    /// <summary>Starts the conversions for the books in progress; returns how many books needed any.</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (sessions?.Playing.Any() == true)
        {
            return 0;
        }

        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var since = clock.GetUtcNow().UtcDateTime - RecentFor;
        var places = await db.PlaybackStates.AsNoTracking()
            .Where(p => !p.Watched && p.PositionSeconds > 0 && p.LastPlayedAt > since && p.MediaItem!.Kind == MediaKind.Audiobook)
            .OrderByDescending(p => p.LastPlayedAt)
            .Select(p => new { p.MediaItemId, p.PositionSeconds })
            .Take(MaxBooks * 3)
            .ToListAsync(ct);

        var warmed = 0;
        var files = 0;
        foreach (var place in places.DistinctBy(p => p.MediaItemId).Take(MaxBooks))
        {
            if (files >= MaxFiles)
            {
                break;
            }

            var parts = await BookPlayback.PartsAsync(db, place.MediaItemId, [], ct);
            if (parts.Any(p => p.Convert))
            {
                files += converter.Prepare(parts, place.PositionSeconds, MaxFiles - files);
                warmed++;
            }
        }

        return warmed;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't get books in progress converted ahead of time")]
    private partial void LogFailed(Exception ex);
}
