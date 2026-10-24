using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Streaming;

namespace Flexarr.Api.Configuration;

public enum AuthenticationRequired
{
    Enabled,
    DisabledForLocalAddresses,
}

public sealed class FlexarrOptions
{
    public const string SectionName = "Flexarr";

    /// <summary>Where Flexarr keeps its config, database and caches. Mounted at /config in Docker.</summary>
    public string DataDirectory { get; set; } = "data";

    /// <summary>API key for clients and *arr webhooks. Generated and persisted on first run when empty.</summary>
    public string? ApiKey { get; set; }

    /// <summary>ffprobe executable; resolved from PATH by default.</summary>
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>ffmpeg executable; resolved from PATH by default.</summary>
    public string FfmpegPath { get; set; } = "ffmpeg";

    /// <summary>Artwork, subtitles, previews and transcodes. Defaults to &lt;DataDirectory&gt;/cache.</summary>
    public string? CacheDirectory { get; set; }

    public string ResolvedCacheDirectory => CacheDirectory ?? Path.Combine(DataDirectory, "cache");

    public AuthenticationRequired AuthenticationRequired { get; set; } = AuthenticationRequired.Enabled;

    public List<ArrInstance> Arr { get; set; } = [];

    public TranscodingOptions Transcoding { get; set; } = new();

    /// <summary>Rewrites paths received from *arr apps (typically running in Docker) to local paths.</summary>
    public List<PathMapping> PathMappings { get; set; } = [];
}

public sealed class TranscodingOptions
{
    public HardwareAcceleration HardwareAcceleration { get; set; } = HardwareAcceleration.Auto;
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public string SoftwarePreset { get; set; } = "veryfast";

    /// <summary>Upper limit for any one stream in bits per second (0 = none), e.g. to protect a slow uplink.</summary>
    public long MaxStreamingBitrate { get; set; }
}
