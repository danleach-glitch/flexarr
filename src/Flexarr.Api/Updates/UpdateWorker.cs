using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Updates;

/// <summary>
/// Checks for a new release once a day (always: it's one small request, and how Settings knows to show its dot). With
/// automatic updates on, installs one between four and five in the morning (after the half-past-three backup) when
/// nothing's playing, being watched live or recording. A night that's busy all hour waits for the next.
/// </summary>
public sealed partial class UpdateWorker(
    UpdateChecker checker,
    MacUpdater updater,
    ServerActivity activity,
    IOptionsMonitor<FlexarrOptions> options,
    TimeProvider time,
    ILogger<UpdateWorker> logger) : BackgroundService
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);

    /// <summary>Within the window, a check older than this is made again first, so a release out since is caught.</summary>
    private static readonly TimeSpan FreshEnough = TimeSpan.FromHours(6);

    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    public static readonly TimeSpan WindowStart = new(4, 0, 0);
    public static readonly TimeSpan WindowEnd = new(5, 0, 0);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Out of the way of startup's own work.
            await Task.Delay(TimeSpan.FromMinutes(2), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A background service that throws stops the whole app.
                    LogFailed(ex);
                }

                await Task.Delay(Tick, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var settings = options.CurrentValue.Updates;
        var now = time.GetUtcNow();
        if (checker.LastChecked is not { } last || now - last >= CheckEvery)
        {
            await checker.CheckAsync(ct);
        }

        if (!settings.Automatic || !updater.CanInstall || !InWindow(time.GetLocalNow().TimeOfDay))
        {
            return;
        }

        if (checker.LastChecked is not { } checkedAt || now - checkedAt >= FreshEnough)
        {
            await checker.CheckAsync(ct);
        }

        if (!checker.Available(ReleaseVersion.Current) || checker.Latest is not { } latest || TriedAlready(latest.Version))
        {
            return;
        }

        if (await activity.InUseAsync(ct) is { Count: > 0 } busy)
        {
            LogBusy(latest.Version.ToString(), string.Join(" ", busy));
            return;
        }

        LogInstalling(latest.Version.ToString());
        updater.Start(latest);
    }

    public static bool InWindow(TimeSpan timeOfDay) => timeOfDay >= WindowStart && timeOfDay < WindowEnd;

    /// <summary>A release that already failed to install isn't tried again by itself, every ten minutes; installing it by hand still can.</summary>
    private bool TriedAlready(ReleaseVersion version) =>
        (updater.Error is not null && updater.Installing == version)
        || (updater.LastResult() is { Ok: false } result && ReleaseVersion.Parse(result.To) == version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Installing Flexarr {Version} automatically")]
    private partial void LogInstalling(string version);

    [LoggerMessage(Level = LogLevel.Information, Message = "Flexarr {Version} is ready to install, but the server is in use: {Reasons}")]
    private partial void LogBusy(string version, string reasons);

    [LoggerMessage(Level = LogLevel.Error, Message = "Checking for or installing an update failed")]
    private partial void LogFailed(Exception ex);
}
