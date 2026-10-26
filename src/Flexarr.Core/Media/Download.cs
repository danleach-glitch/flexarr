namespace Flexarr.Core.Media;

/// <summary>How big a download is, from the original file down to one small enough for a long flight.</summary>
public enum DownloadQuality
{
    /// <summary>The file as it is (repackaged as MP4 if need be), when the phone can play it.</summary>
    Original,

    /// <summary>1080p, about 2 GB an hour.</summary>
    High,

    /// <summary>720p, about 1 GB an hour.</summary>
    Medium,

    /// <summary>480p, about 500 MB an hour.</summary>
    Small,
}

public enum DownloadStatus
{
    /// <summary>Waiting its turn to be prepared.</summary>
    Queued,

    /// <summary>Being converted.</summary>
    Preparing,

    /// <summary>Ready for the device to fetch.</summary>
    Ready,

    /// <summary>The device has it, so the server's copy has gone.</summary>
    Received,

    Failed,

    /// <summary>Not fetched in time, so the server's copy was deleted. Asking again prepares it afresh.</summary>
    Expired,

    /// <summary>
    /// A podcast episode taken off the phone: kept (without a file) so it's never fetched for it again by itself.
    /// Downloading it by hand starts afresh.
    /// </summary>
    Removed,
}

/// <summary>
/// A film or episode got ready for a profile's phone or tablet to keep for offline watching: converted to an MP4
/// of the chosen size (with its text subtitles inside), held until the device has fetched it.
/// </summary>
public sealed class Download
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string ProfileId { get; set; }
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }
    public DownloadQuality Quality { get; set; }
    public DownloadStatus Status { get; set; }

    /// <summary>How far the conversion has got, 0 to 1.</summary>
    public double Progress { get; set; }

    public long? SizeBytes { get; set; }

    /// <summary>Why it failed, in words for the viewer.</summary>
    public string? Error { get; set; }

    /// <summary>The file to send: the prepared MP4, or the library's own file when it can be sent as it is.</summary>
    public string? FilePath { get; set; }

    /// <summary><see cref="FilePath"/> is the library's own file, so it's never deleted.</summary>
    public bool IsLibraryFile { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReadyAt { get; set; }
}
