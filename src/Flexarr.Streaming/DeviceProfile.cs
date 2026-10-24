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
}
