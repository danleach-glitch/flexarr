namespace Flexarr.Streaming;

public enum HardwareAcceleration
{
    /// <summary>
    /// Use the best available: VideoToolbox on macOS; on Linux, the first hardware encoder that works there (see
    /// <see cref="HardwareProbe"/>); software otherwise.
    /// </summary>
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

    /// <summary>
    /// Most streams converting the picture at once (0 = no limit); playback beyond it is refused rather than slowing
    /// everyone. Repackaged streams and sound-only ones don't count.
    /// </summary>
    public int MaxConcurrentTranscodes { get; init; } = 3;

    /// <summary>What Auto was found to mean here (<see cref="HardwareProbe"/>), once it has been; null until then.</summary>
    public HardwareAcceleration? Detected { get; init; }

    public HardwareAcceleration Resolved => HardwareAcceleration switch
    {
        HardwareAcceleration.Auto when OperatingSystem.IsMacOS() => HardwareAcceleration.VideoToolbox,
        HardwareAcceleration.Auto => Detected is { } found and not HardwareAcceleration.Auto ? found : HardwareAcceleration.None,
        var explicitChoice => explicitChoice,
    };

    /// <summary>
    /// Tallest picture converted in software: there's no resolution ceiling otherwise, so a browser without HEVC would
    /// get 4K re-encoded to 4K H.264 on the processor, several times the work of 1080p.
    /// </summary>
    public const int SoftwareMaxHeight = 1080;

    /// <summary>The cap on a converted picture's height that encoding here sets; null with a hardware encoder.</summary>
    public int? MaxConvertedHeight => Resolved == HardwareAcceleration.None ? SoftwareMaxHeight : null;

    /// <summary>
    /// The limit on conversions at once when none is set: 3 suits a Mac mini's media engine or a GPU; in software a
    /// 1080p conversion keeps about four cores busy, so it's a core count's worth, from one to three.
    /// </summary>
    public static int DefaultLimit(HardwareAcceleration resolved, int processors) =>
        resolved == HardwareAcceleration.None ? Math.Clamp(processors / 4, 1, 3) : 3;
}
