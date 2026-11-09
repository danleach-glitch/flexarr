using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// Keeps what Discover › Music asks Deezer ready, so its first open of the day isn't a long wait: about 90 calls, at the
/// pace Deezer allows. Shortly after the server starts and every half hour; what's still kept costs nothing. For the main
/// profile, whose rows hold most of what anyone's would. Only while music can be requested.
/// </summary>
public sealed partial class MusicDiscoverWarmer(IServiceScopeFactory scopes, ILogger<MusicDiscoverWarmer> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); // after startup's own work
            using var timer = new PeriodicTimer(Every);
            do
            {
                try
                {
                    await RunOnceAsync(stoppingToken);
                }
                catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
                {
                    LogFailed(logger, ex);
                }
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    internal async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var c = scope.ServiceProvider.GetRequiredService<MusicRequestContext>();
        if (!c.Options.MusicRequests.Enabled || c.Lidarrs.Count == 0
            || !await c.Db.Libraries.AnyAsync(l => l.Type == LibraryType.Music, ct))
        {
            return;
        }

        await MusicRequestEndpoints.WarmDiscoverAsync(c, PlaybackState.DefaultUser, ct);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Getting Discover's music ready failed")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
