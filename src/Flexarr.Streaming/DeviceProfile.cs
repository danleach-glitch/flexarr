namespace Flexarr.Streaming;

/// <summary>What a client can play natively, reported by the client itself (e.g. from canPlayType in a browser).</summary>
public sealed record DeviceProfile
{
    public string Client { get; init; } = "web";

    /// <summary>Containers the client can direct play, e.g. "mp4", "webm", "mkv".</summary>
    public IReadOnlyList<string> Containers { get; init; } = ["mp4"];

    /// <summary>ffprobe codec names: "h264", "hevc", "av1", "vp9".</summary>
    public IReadOnlyList<string> VideoCodecs { get; init; } = ["h264"];

    /// <summary>ffprobe codec names: "aac", "mp3", "opus", "flac", "ac3", "eac3".</summary>
    public IReadOnlyList<string> AudioCodecs { get; init; } = ["aac", "mp3"];

    public bool Hdr { get; init; }
    public int MaxHeight { get; init; } = 2160;
    public int MaxAudioChannels { get; init; } = 2;

    /// <summary>Bits per second; 0 = no limit.</summary>
    public long MaxBitrate { get; init; }

    /// <summary>
    /// The client plays HLS in fragmented MP4 (Apple's players). That can carry what MPEG-TS can't: lossless FLAC up to
    /// 7.1, and Dolby Digital Plus with its Atmos signalled.
    /// </summary>
    public bool Fmp4 { get; init; }

    /// <summary>The display shows Dolby Vision: profile 5 (which has no HDR10 fallback) can be sent as it is.</summary>
    public bool DolbyVision { get; init; }

    /// <summary>
    /// The display shows HDR10 (PQ) and HLG right now. An Apple TV set to a fixed Dolby Vision output refuses plain
    /// HDR10 streams outright, so these say what's presentable, not just what the hardware can do. Left out by older
    /// apps: assumed from <see cref="Hdr"/>.
    /// </summary>
    public bool? Hdr10 { get; init; }
    public bool? Hlg { get; init; }
}
