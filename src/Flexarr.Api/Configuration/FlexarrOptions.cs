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

    /// <summary>
    /// What Sonarr/Radarr/Lidarr webhooks send instead of the API key; it can only queue scans. Generated on first run.
    /// </summary>
    public string? WebhookSecret { get; set; }

    /// <summary>
    /// Signs the links to outside artwork (Coming soon, requests, collections) the server hands out, so only those can
    /// be fetched through it. Generated on first run; changing it only breaks links already handed out.
    /// </summary>
    public string? ImageSecret { get; set; }

    /// <summary>
    /// Reverse proxies (addresses or CIDR ranges, such as "172.17.0.1" for Docker) whose X-Forwarded-For is believed.
    /// This machine is always trusted. Forwarded requests from anywhere else count as coming from the internet.
    /// Read at startup.
    /// </summary>
    public List<string> TrustedProxies { get; set; } = [];

    /// <summary>
    /// Extra names this server is reached by on the home network (split DNS, say). The home-network and Tailscale
    /// sign-ins only apply to requests addressed to a name Flexarr knows, which stops other websites from using a
    /// browser on your network to get in (DNS rebinding). IP addresses, single-word names, private suffixes (.local,
    /// .lan, .home, .home.arpa, .internal…), .ts.net and this machine's name are always known.
    /// </summary>
    public List<string> KnownHosts { get; set; } = [];

    /// <summary>
    /// Requests from this machine that don't say who they're forwarding for (no X-Forwarded-For) count as the
    /// internet, not as this machine. Turn on when a reverse proxy runs here: one that doesn't send X-Forwarded-For
    /// would otherwise make every visitor look local. A browser on this machine then needs the key too.
    /// </summary>
    public bool LoopbackNeedsForwarding { get; set; }

    /// <summary>
    /// Until when the sign-ins earlier versions handed out (one browser cookie shared by all, profile tokens that ignore
    /// the PIN) still work, so browsers can swap them for their own. Set to 30 days after the first run of a version
    /// with per-device sign-ins; after it, they're refused.
    /// </summary>
    public DateTime? LegacySignInsUntil { get; set; }

    /// <summary>
    /// A new server, not yet signed in to: the first browser on the home network to reach it becomes its owner
    /// without the API key. Phones and TVs then connect with a code, approved from that browser. Set when the key is first made (so never on a server that already existed); cleared
    /// by the first sign-in of any kind.
    /// </summary>
    public bool SetupOpen { get; set; }

    /// <summary>Whether <see cref="LegacySignInsUntil"/> hasn't passed yet.</summary>
    public bool AcceptsLegacySignIns(DateTime utcNow) =>
        LegacySignInsUntil is { } until && utcNow < (until.Kind == DateTimeKind.Local ? until.ToUniversalTime() : until);

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

    /// <summary>Optional Seerr/Overseerr/Jellyseerr server for "not in your library" discovery and requests.</summary>
    public SeerrOptions? Seerr { get; set; }

    /// <summary>Rewrites paths received from *arr apps (typically running in Docker) to local paths.</summary>
    public List<PathMapping> PathMappings { get; set; } = [];

    public LayoutOptions Layout { get; set; } = new();

    public PodcastOptions Podcasts { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public Logging.LogOptions Logs { get; set; } = new();

    /// <summary>Optional Live TV source: Dispatcharr (or another server with an Xtream Codes API).</summary>
    public LiveTvOptions? LiveTv { get; set; }

    public BackupOptions Backups { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();
}

/// <summary>Finding and installing new releases (see <see cref="Flexarr.Api.Updates.UpdateChecker"/>).</summary>
public sealed class UpdateOptions
{
    /// <summary>Install a newer release by itself, overnight while nothing's playing. Only where Flexarr can update itself.</summary>
    public bool Automatic { get; set; }

    /// <summary>GitHub's latest-release address for Flexarr's repository.</summary>
    public string ReleasesUrl { get; set; } = "https://api.github.com/repos/danleach-glitch/flexarr/releases/latest";
}

/// <summary>Backups of the database and settings (see <see cref="Flexarr.Api.Backups.BackupStore"/>).</summary>
public sealed class BackupOptions
{
    /// <summary>Where backups are kept; unset, &lt;DataDirectory&gt;/backups. A folder on another drive survives this one failing.</summary>
    public string? Folder { get; set; }

    /// <summary>Take one every night, at half past three server time.</summary>
    public bool Nightly { get; set; } = true;

    /// <summary>How many nightly backups are kept; older ones are deleted as new ones are made.</summary>
    public int KeepNightly { get; set; } = 7;

    public string ResolvedFolder(string dataDirectory) =>
        Path.GetFullPath(string.IsNullOrWhiteSpace(Folder) ? Path.Combine(dataDirectory, "backups") : Folder);
}

public sealed class LiveTvOptions
{
    public string BaseUrl { get; set; } = "";
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>How far back a channel being watched can be rewound.</summary>
    public int RewindMinutes { get; set; } = 60;

    /// <summary>Where recordings go; unset, a DVR folder on the drive the TV library is on (see <see cref="Flexarr.Api.LiveTv.DvrFolders"/>).</summary>
    public string? DvrFolder { get; set; }

    /// <summary>How many channels can be open at once; unset, what Dispatcharr says the login allows.</summary>
    public int? MaxStreams { get; set; }

    /// <summary>Recordings start this many minutes early and run this many on, as broadcasts drift.</summary>
    public int PaddingBeforeMinutes { get; set; } = 1;

    public int PaddingAfterMinutes { get; set; } = 5;

    /// <summary>No new recording starts when the DVR folder's drive has less than this free.</summary>
    public int MinFreeGigabytes { get; set; } = 100;

    public Flexarr.Api.LiveTv.XtreamAccount? Account =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && url.Scheme is "http" or "https" && Username.Length > 0 && Password.Length > 0
            ? new Flexarr.Api.LiveTv.XtreamAccount(url, Username, Password)
            : null;
}

public sealed class PodcastOptions
{
    /// <summary>
    /// The country whose podcast charts and directory Browse shows, as a two-letter code ("gb", "us"). Unset, it's
    /// the server's own region.
    /// </summary>
    public string? Country { get; set; }

    public string ResolvedCountry => Flexarr.Api.Podcasts.PodcastCountries.Resolve(Country);
}

/// <summary>Which rows Home and each library's Browse view show, in order. Rows not listed keep their built-in place.</summary>
public sealed class LayoutOptions
{
    public List<RowSetting> Home { get; set; } = [];
    public List<RowSetting> Movies { get; set; } = [];
    public List<RowSetting> Shows { get; set; } = [];

    /// <summary>Home shows one Latest unwatched row for all the film libraries, and one for all the TV ones.</summary>
    public bool CombineLibraries { get; set; } = true;
}

/// <param name="Id">A row's id ("continue", "top-movies", "recent-{libraryId}", "genres" for all genre rows…).</param>
public sealed class RowSetting
{
    public string Id { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class TranscodingOptions
{
    public HardwareAcceleration HardwareAcceleration { get; set; } = HardwareAcceleration.Auto;
    public string VaapiDevice { get; set; } = "/dev/dri/renderD128";
    public string SoftwarePreset { get; set; } = "veryfast";

    /// <summary>Upper limit for any one stream in bits per second (0 = none), e.g. to protect a slow uplink.</summary>
    public long MaxStreamingBitrate { get; set; }

    /// <summary>Most videos converted at once (0 = no limit). A Mac mini's media engine handles about three well.</summary>
    public int MaxConcurrentTranscodes { get; set; } = 3;
}

/// <summary>Push notifications to phones, through the relay that holds Apple's key (see docs/notifications.md).</summary>
public sealed class NotificationOptions
{
    public bool Enabled { get; set; } = true;
    public string RelayUrl { get; set; } = "https://push.flexarr.io/api/push";
}

public sealed class SeerrOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public SeerrInstance? Instance => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && ApiKey.Length > 0 ? new SeerrInstance(url, ApiKey) : null;
}
