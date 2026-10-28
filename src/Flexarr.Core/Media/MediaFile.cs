namespace Flexarr.Core.Media;

public enum VideoRange
{
    Sdr,
    Hdr10,
    Hlg,
    DolbyVision,
}

public enum StreamType
{
    Video,
    Audio,
    Subtitle,
}

/// <summary>A video file on disk plus the technical details ffprobe reported for it.</summary>
public sealed class MediaFile
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public required string Path { get; set; }
    public long Size { get; set; }
    public DateTime ModifiedAt { get; set; }

    public string? Container { get; set; }
    public double? DurationSeconds { get; set; }
    public long? Bitrate { get; set; }
    public string? VideoCodec { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public VideoRange VideoRange { get; set; }

    /// <summary>HDR10+ metadata in the video. Null until checked: HDR files scanned before it was looked for get checked again.</summary>
    public bool? Hdr10Plus { get; set; }

    public string? AudioCodec { get; set; }
    public int? AudioChannels { get; set; }

    /// <summary>
    /// Seconds this file's sound is played later (or, negative, earlier) to match its picture: a fault of the file, so
    /// one offset for everyone. Cleared when the file changes.
    /// </summary>
    public double AudioDelaySeconds { get; set; }

    /// <summary>Set when ffprobe could not read the file; the scanner retries when the file changes.</summary>
    public string? ProbeError { get; set; }

    /// <summary>When intro/credits detection last ran for this file (null = never).</summary>
    public DateTime? MarkersScannedAt { get; set; }

    /// <summary>When a scan last found this file gone (null = present). Hidden until it reappears or is removed.</summary>
    public DateTime? MissingSince { get; set; }

    public List<MediaStream> Streams { get; set; } = [];
    public List<MediaMarker> Markers { get; set; } = [];
}

public sealed class MediaStream
{
    public int Id { get; set; }
    public Guid MediaFileId { get; set; }
    public int Index { get; set; }
    public StreamType Type { get; set; }
    public required string Codec { get; set; }
    public string? Profile { get; set; }
    public string? Language { get; set; }
    public string? Title { get; set; }
    public bool IsDefault { get; set; }
    public bool IsForced { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public int? Channels { get; set; }
}
