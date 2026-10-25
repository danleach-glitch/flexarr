using Flexarr.Api.Configuration;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Podcasts;

/// <summary>
/// Keeps Apple's podcast charts ready, so Browse opens on them at once: every category for the country Settings chose,
/// fetched shortly after the server starts, again every few hours (before the kept ones run out), and straight away
/// when the country changes. The overall chart's artwork is fetched into the image cache too. Only while there's a
/// Podcasts library.
/// </summary>
public sealed partial class PodcastChartWarmer(IServiceScopeFactory scopes, IOptionsMonitor<FlexarrOptions> options, TimeProvider clock,
    ILogger<PodcastChartWarmer> logger) : BackgroundService
{
    private static readonly TimeSpan Every = PodcastDirectory.ChartsKeptFor - TimeSpan.FromHours(2);
    private static readonly TimeSpan Look = TimeSpan.FromMinutes(1);

    private string? _warmedCountry;
    private DateTime _warmedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), clock, stoppingToken); // after startup's own work
        using var timer = new PeriodicTimer(Look, clock);
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
        var country = options.CurrentValue.Podcasts.ResolvedCountry;
        var now = clock.GetUtcNow().UtcDateTime;
        if (country == _warmedCountry && now - _warmedAt < Every)
        {
            return;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        if (!await db.Libraries.AnyAsync(l => l.Type == LibraryType.Podcasts, ct))
        {
            return;
        }

        var directory = scope.ServiceProvider.GetRequiredService<PodcastDirectory>();
        var images = scope.ServiceProvider.GetRequiredService<ImageCache>();
        var overall = await directory.TopAsync(country, null, ct, fresh: true);
        foreach (var genre in PodcastDirectory.Genres)
        {
            try
            {
                await directory.TopAsync(country, genre.Id, ct, fresh: true);
            }
            catch (HttpRequestException)
            {
                // That one waits for someone to open it; the rest are still worth having.
            }
        }

        // The first screenful's artwork, at the size Browse shows it.
        foreach (var show in overall.Take(24))
        {
            if (ArtworkUrl.TrustedRemote(show.ArtworkUrl) is { } artwork)
            {
                await images.GetAsync(artwork, 342, ct);
            }
        }

        _warmedCountry = country;
        _warmedAt = now;
        LogWarmed(country);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Podcast charts for {Country} are ready")]
    private partial void LogWarmed(string country);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Getting the podcast charts ready failed")]
    private partial void LogFailed(Exception exception);
}
