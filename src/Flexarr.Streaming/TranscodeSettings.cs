namespace Flexarr.Streaming;

public enum HardwareAcceleration
{
    /// <summary>Use the best available: VideoToolbox on macOS, software elsewhere.</summary>
    Auto,
    None,
    VideoToolbox,
    Vaapi,
    Qsv,
    Nvenc,
}

public sealed record TranscodeSettings
{
    public string FfmpegPath { get; init; } = "ffmpeg";
    public required string CacheDirectory { get; init; }
    public HardwareAcceleration HardwareAcceleration { get; init; } = HardwareAcceleration.Auto;

    /// <summary>Path of the VAAPI render node on Linux.</summary>
    public string VaapiDevice { get; init; } = "/dev/dri/renderD128";

    /// <summary>libx264 preset when encoding in software.</summary>
    public string SoftwarePreset { get; init; } = "veryfast";

    /// <summary>A stream whose hardware encoder fails as it starts is converted in software instead.</summary>
    public bool SoftwareFallback { get; init; } = true;

    /// <summary>
    /// This FFmpeg has zscale, to map HDR to SDR on the CPU (software encoding, NVENC, Quick Sync, burned-in subtitles).
    /// Without it (the Mac app's LGPL build, Homebrew's) HDR is sent on unmapped rather than failing to play.
    /// </summary>
    public bool CanToneMapInSoftware { get; init; } = true;

    /// <summary>Most transcodes running at once (0 = no limit); playback beyond it is refused rather than slowing everyone.</summary>
    public int MaxConcurrentTranscodes { get; init; } = 3;

    public HardwareAcceleration Resolved => HardwareAcceleration switch
    {
        HardwareAcceleration.Auto when OperatingSystem.IsMacOS() => HardwareAcceleration.VideoToolbox,
        HardwareAcceleration.Auto => HardwareAcceleration.None,
        var explicitChoice => explicitChoice,
    };
}
