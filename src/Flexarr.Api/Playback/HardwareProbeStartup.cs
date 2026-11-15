using Flexarr.Api.Configuration;
using Flexarr.Streaming;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Playback;

/// <summary>
/// On Linux with hardware acceleration left on Auto, finds which hardware encoder works here as the server starts (see
/// <see cref="HardwareProbe"/>), so a GPU passed into a container is used without its owner finding the setting.
/// Streams started before it has finished are converted in software, as before.
/// </summary>
public sealed partial class HardwareProbeStartup(IOptionsMonitor<FlexarrOptions> options, ILogger<HardwareProbeStartup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var current = options.CurrentValue;
        if (!OperatingSystem.IsLinux() || current.Transcoding.HardwareAcceleration != HardwareAcceleration.Auto)
        {
            return;
        }

        try
        {
            var found = await HardwareProbe.ProbeAsync(current.FfmpegPath, current.Transcoding.VaapiDevice).WaitAsync(stoppingToken);
            LogFound(found == HardwareAcceleration.None ? "software (no hardware encoder worked)" : found.ToString());
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Stopping.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Hardware acceleration Auto: converting with {Encoder}")]
    private partial void LogFound(string encoder);
}
