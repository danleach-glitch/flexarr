using Flexarr.Core.Data;
using Flexarr.Core.Scanning;

namespace Flexarr.Api.Scanning;

/// <summary>Links copies of a title at startup: libraries from before links existed, and anything a scan was cut off before reaching.</summary>
public sealed partial class TitleLinkBackfill(IServiceScopeFactory scopes, ILogger<TitleLinkBackfill> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Yield();
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var changed = await TitleLinker.LinkAsync(scope.ServiceProvider.GetRequiredService<FlexarrDbContext>(), stoppingToken);
            if (changed > 0)
            {
                LogLinked(logger, changed);
            }
        }
        catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
        {
            LogFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Linked the copies of titles: {Changed} items changed")]
    private static partial void LogLinked(ILogger logger, int changed);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't link the copies of titles")]
    private static partial void LogFailed(ILogger logger, Exception ex);
}
