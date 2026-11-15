using System.Collections.Concurrent;
using System.Diagnostics;

namespace Flexarr.Streaming;

/// <summary>
/// What "Auto" hardware acceleration means on Linux: a Docker or NAS install with a GPU passed in shouldn't need its
/// owner to find the setting. Each encoder ffmpeg offers is tried on one frame, once per ffmpeg and device, and the
/// first that works is used (NVENC, then VA-API, then Quick Sync). macOS always has VideoToolbox; elsewhere, or with
/// nothing working, Auto stays software.
/// </summary>
public static class HardwareProbe
{
    private static readonly ConcurrentDictionary<(string Ffmpeg, string Device), Lazy<Task<HardwareAcceleration>>> Found = new();

    /// <summary>Encoders tried, in order of preference, with the arguments that encode one software frame on each.</summary>
    internal static IReadOnlyList<(HardwareAcceleration Kind, string Encoder, IReadOnlyList<string> Args)> Candidates(string vaapiDevice) =>
    [
        (HardwareAcceleration.Nvenc, "h264_nvenc", ["-vf", "format=yuv420p", "-c:v", "h264_nvenc"]),
        (HardwareAcceleration.Vaapi, "h264_vaapi", ["-vaapi_device", vaapiDevice, "-vf", "format=nv12,hwupload", "-c:v", "h264_vaapi"]),
        (HardwareAcceleration.Qsv, "h264_qsv", ["-vf", "format=nv12", "-c:v", "h264_qsv"]),
    ];

    /// <summary>
    /// What Auto has been found to mean for this ffmpeg, once <see cref="ProbeAsync"/> has finished; null before then
    /// (and on macOS, where <see cref="TranscodeSettings.Resolved"/> doesn't ask).
    /// </summary>
    public static HardwareAcceleration? Known(string ffmpegPath, string vaapiDevice) =>
        Found.TryGetValue((ffmpegPath, vaapiDevice), out var probe) && probe.Value.IsCompletedSuccessfully ? probe.Value.Result : null;

    /// <summary>Tries each hardware encoder (once per ffmpeg and device; on Linux only) and remembers the first that works.</summary>
    public static Task<HardwareAcceleration> ProbeAsync(string ffmpegPath, string vaapiDevice) =>
        OperatingSystem.IsLinux()
            ? Found.GetOrAdd((ffmpegPath, vaapiDevice), key => new Lazy<Task<HardwareAcceleration>>(() => Task.Run(() => FirstWorkingAsync(key.Ffmpeg, key.Device)))).Value
            : Task.FromResult(HardwareAcceleration.None);

    private static async Task<HardwareAcceleration> FirstWorkingAsync(string ffmpegPath, string vaapiDevice)
    {
        var encoders = await RunAsync(ffmpegPath, ["-hide_banner", "-encoders"]).ConfigureAwait(false);
        if (encoders is null)
        {
            return HardwareAcceleration.None;
        }

        foreach (var (kind, encoder, args) in Candidates(vaapiDevice))
        {
            // VA-API and Quick Sync need a render device, which a container only has when it's passed in.
            if (!encoders.Contains(encoder, StringComparison.Ordinal)
                || (kind == HardwareAcceleration.Vaapi && !File.Exists(vaapiDevice))
                || (kind == HardwareAcceleration.Qsv && !(Directory.Exists("/dev/dri") && Directory.EnumerateFileSystemEntries("/dev/dri", "renderD*").Any())))
            {
                continue;
            }

            if (await RunAsync(ffmpegPath, TestArgs(args)).ConfigureAwait(false) is not null)
            {
                return kind;
            }
        }

        return HardwareAcceleration.None;
    }

    /// <summary>One small black frame through the encoder, written nowhere.</summary>
    internal static IReadOnlyList<string> TestArgs(IReadOnlyList<string> encoder) =>
        ["-hide_banner", "-v", "error", "-nostdin", "-f", "lavfi", "-i", "color=black:s=256x256:d=0.1", "-frames:v", "1", .. encoder, "-f", "null", "-"];

    /// <summary>Its output when it ran and succeeded within ten seconds; null otherwise.</summary>
    private static async Task<string?> RunAsync(string ffmpegPath, IReadOnlyList<string> args)
    {
        try
        {
            var start = new ProcessStartInfo(ffmpegPath) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in args)
            {
                start.ArgumentList.Add(arg);
            }

            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                ProcessSignals.KillTree(process);
                return null;
            }

            return process.ExitCode == 0 ? await output.ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
