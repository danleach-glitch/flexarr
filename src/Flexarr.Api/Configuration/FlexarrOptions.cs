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

    /// <summary>
    /// The revision of the last settings change written to config.json (see
    /// <see cref="ServerConfigStore.UpdateAndWaitAsync"/>): what tells a reload of that change from a late one of an
    /// earlier change.
    /// </summary>
    public string? ConfigRevision { get; set; }

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
    /// This server's random id, generated on first run. Not a secret: anyone who can reach the server may read it (GET
    /// /setup), so an app that learns another address for its server can check it's the same server before sending its
    /// sign-in there. Separate from the error reports' id, which stays between the server's apps and Sentry.
    /// </summary>
    public string? ServerId { get; set; }

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

    /// <summary>
    /// Choosing the main profile on a browser or device the owner signed in asks for its PIN, not only managing the
    /// server. Off on new servers; servers whose main profile had a PIN before managing the server took it keep asking
    /// (set at the first start of that version, see Program.cs).
    /// </summary>
    public bool? AskPinForMainProfile { get; set; }

    /// <summary>
    /// The table of ratings (<see cref="Flexarr.Core.Media.RatingAges.Version"/>) every title's age was last worked out
    /// with, so startup only does it again when that changes (or the database did).
    /// </summary>
    public string? RatingAges { get; set; }

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

    /// <summary>Optional Bazarr server, for finding, downloading and re-syncing subtitles from the players.</summary>
    public BazarrOptions? Bazarr { get; set; }

    /// <summary>The owner's own TMDB key, for films Radarr doesn't have (or when there's no Radarr).</summary>
    public TmdbOptions? Tmdb { get; set; }

    /// <summary>Rewrites paths received from *arr apps (typically running in Docker) to local paths.</summary>
    public List<PathMapping> PathMappings { get; set; } = [];

    /// <summary>The <see cref="PathMapping.From"/> of each mapping Flexarr worked out and saved itself, so it's only saved once.</summary>
    public List<string> PathMappingsDetected { get; set; } = [];

    public LayoutOptions Layout { get; set; } = new();

    public PodcastOptions Podcasts { get; set; } = new();

    public BookMetadataOptions BookMetadata { get; set; } = new();

    public NotificationOptions Notifications { get; set; } = new();

    public TraktOptions Trakt { get; set; } = new();

    public Logging.LogOptions Logs { get; set; } = new();

    /// <summary>Optional Live TV source: Dispatcharr (or another server with an Xtream Codes API).</summary>
    public LiveTvOptions? LiveTv { get; set; }

    public BackupOptions Backups { get; set; } = new();

    public UpdateOptions Updates { get; set; } = new();

    /// <summary>Where the main profile is in the first-run setup guide (see <see cref="Endpoints.SetupGuideEndpoints"/>).</summary>
    public SetupGuideOptions SetupGuide { get; set; } = new();

    /// <summary>Family invites: guests watching from their own homes through Tailscale built into the apps.</summary>
    public RemoteGuestOptions RemoteGuests { get; set; } = new();
}

/// <summary>The first-run setup guide: whether it's been opened, put away, and which of its steps were passed over.</summary>
public sealed class SetupGuideOptions
{
    /// <summary>It's been shown, so Home lists what's left rather than opening it again.</summary>
    public bool Opened { get; set; }

    /// <summary>Put away for good: no list on Home either.</summary>
    public bool Dismissed { get; set; }

    /// <summary>Steps passed over, which count as done.</summary>
    public List<string> Skipped { get; set; } = [];
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

/// <summary>How books are looked up online (config.json's <c>bookMetadata</c>).</summary>
public sealed class BookMetadataOptions
{
    /// <summary>Audible's store ("uk", "us"…). Unset, the server's own country's, or the US store.</summary>
    public string? Region { get; set; }

    /// <summary>Providers to ask, in order. Unset, all of them: Audible, Open Library, Google Books, Hardcover.</summary>
    public List<string>? Providers { get; set; }

    public string? GoogleBooksKey { get; set; }
    public string? HardcoverToken { get; set; }

    /// <param name="country">The server's country ("gb", "us"), for a region nobody chose.</param>
    public Flexarr.Metadata.Books.BookMetadataSettings Resolved(string country)
    {
        var region = Region ?? (country == "gb" ? "uk" : country);
        return new(Flexarr.Metadata.Books.BookMetadataSettings.Regions.Contains(region) ? region : "us",
            Providers is { Count: > 0 } chosen ? chosen : Flexarr.Metadata.Books.BookMetadataSettings.AllProviders,
            GoogleBooksKey is { Length: > 0 } g ? g : null, HardcoverToken is { Length: > 0 } h ? h : null);
    }
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

    /// <summary>A music library's Browse.</summary>
    public List<RowSetting> Music { get; set; } = [];

    /// <summary>An audiobooks library's front page: its shelves.</summary>
    public List<RowSetting> Audiobooks { get; set; } = [];

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

    /// <summary>
    /// When the hardware encoder fails as a stream starts (a GPU that's busy, a device not passed in), convert in
    /// software instead. Off in the Mac app, whose FFmpeg has no software encoder.
    /// </summary>
    public bool SoftwareFallback { get; set; } = true;

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

/// <summary>
/// Trakt (see docs/watchlist-trakt.md). By default Flexarr's own Trakt app: its public client id comes from the relay,
/// which also holds the secret and makes the token calls. An admin's own app (both id and secret) bypasses the relay.
/// </summary>
public sealed class TraktOptions
{
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string RelayUrl { get; set; } = "https://push.flexarr.io/api/trakt";
    public string ApiUrl { get; set; } = "https://api.trakt.tv";

    /// <summary>Both an id and a secret of the admin's own Trakt app.</summary>
    public bool HasOwnApp => ClientId.Length > 0 && ClientSecret.Length > 0;
}

public sealed class TmdbOptions
{
    public string ApiKey { get; set; } = "";
}

public sealed class SeerrOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public SeerrInstance? Instance => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && ApiKey.Length > 0 ? new SeerrInstance(url, ApiKey) : null;
}

public sealed class BazarrOptions
{
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";

    public BazarrInstance? Instance => Uri.TryCreate(BaseUrl, UriKind.Absolute, out var url) && ApiKey.Length > 0 ? new BazarrInstance(url, ApiKey) : null;
}

/// <summary>
/// Family invites: guests whose apps join the owner's tailnet (Tailscale built into the apps) as
/// <see cref="Tag"/>, which the tailnet's policy confines to Flexarr's port. See docs/remote-guests.md.
/// </summary>
public sealed class RemoteGuestOptions
{
    /// <summary>Shows family invites in Settings. Off until the apps that carry Tailscale are in the stores.</summary>
    public bool Enabled { get; set; }

    /// <summary>A Tailscale OAuth client limited to <c>auth_keys</c> for <see cref="Tag"/> and <c>policy_file:read</c>.</summary>
    public string ClientId { get; set; } = "";

    /// <summary>The OAuth client's secret. Never sent back by the API, only whether there is one.</summary>
    public string ClientSecret { get; set; } = "";

    /// <summary>The tag guest devices join with.</summary>
    public string Tag { get; set; } = "tag:flexarr-guest";

    /// <summary>The UDP port of Tailscale's peer relay on this server, when the faster relay is on; null when off.</summary>
    public int? RelayPort { get; set; }

    /// <summary>Tailscale's API; changed only by tests.</summary>
    public string ApiUrl { get; set; } = "https://api.tailscale.com";

    /// <summary>The relay TV codes meet at.</summary>
    public string PairUrl { get; set; } = "https://push.flexarr.io/api/pair";

    public bool HasClient => ClientId.Length > 0 && ClientSecret.Length > 0;
}
