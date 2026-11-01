using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Backups;
using Flexarr.Api.Books;
using Flexarr.Api.Collections;
using Flexarr.Api.Configuration;
using Flexarr.Api.Devices;
using Flexarr.Api.Downloads;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Events;
using Flexarr.Api.Import;
using Flexarr.Api.LiveTv;
using Flexarr.Api.Logging;
using Flexarr.Api.Mcp;
using Flexarr.Api.Notifications;
using Flexarr.Api.Playback;
using Flexarr.Api.Playlists;
using Flexarr.Api.Podcasts;
using Flexarr.Api.Profiles;
using Flexarr.Api.Scanning;
using Flexarr.Api.Search;
using Flexarr.Api.Security;
using Flexarr.Api.Trakt;
using Flexarr.Api.Updates;
using Flexarr.Api.Watchlist;
using Flexarr.Core.Data;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Flexarr.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

var dataDirectory = builder.Configuration[$"{FlexarrOptions.SectionName}:{nameof(FlexarrOptions.DataDirectory)}"] ?? "data";
Directory.CreateDirectory(dataDirectory);
// A backup chosen for restore goes in place now, before anything reads the settings or opens the database.
var restored = PendingRestore.Apply(dataDirectory, DateTime.UtcNow);
var userConfig = new ConfigurationBuilder()
    .AddJsonFile(ServerConfigStore.PathFor(dataDirectory), optional: true, reloadOnChange: true)
    .Build();

// config.json overrides appsettings and is reloaded when it changes, so settings apply without a restart.
builder.Services.AddOptions<FlexarrOptions>()
    .BindConfiguration(FlexarrOptions.SectionName)
    .Configure(options => userConfig.Bind(options));
builder.Services.AddSingleton<IOptionsChangeTokenSource<FlexarrOptions>>(new ConfigurationChangeTokenSource<FlexarrOptions>(userConfig));
builder.Services.AddSingleton<IPostConfigureOptions<FlexarrOptions>, ServerConfigStore>();

builder.AddFlexarrLogging(dataDirectory, userConfig);

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(FlexarrClaims.AdminPolicy, policy => policy.AddRequirements(new AdminRequirement()))
    .AddPolicy(FlexarrClaims.MainProfilePolicy, policy => policy.RequireAssertion(context =>
        context.User.HasClaim(FlexarrClaims.Admin, "true") || context.User.HasClaim(FlexarrClaims.CanUnlock, "true")));
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminHandler>();
builder.Services.AddSingleton<SignIns>();
builder.Services.AddSingleton<PinGuard>();
builder.Services.AddSingleton<AdminUnlocks>();

// Tailscale serve and cloudflared proxy from this machine; trust their forwarded headers, and otherwise only those of
// proxies listed in TrustedProxies (a reverse proxy in Docker or on another machine). Anything else forwarded counts
// as the internet (see NetworkZones).
var trustedProxies = builder.Configuration.GetSection($"{FlexarrOptions.SectionName}:{nameof(FlexarrOptions.TrustedProxies)}").Get<List<string>>() ?? [];
trustedProxies.AddRange(userConfig.GetSection("trustedProxies").Get<List<string>>() ?? []);
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownIPNetworks.Clear();
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
    foreach (var proxy in trustedProxies)
    {
        if (System.Net.IPNetwork.TryParse(proxy, out var network))
        {
            o.KnownIPNetworks.Add(network);
        }
        else if (System.Net.IPAddress.TryParse(proxy, out var address))
        {
            o.KnownProxies.Add(address);
        }
    }
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddMemoryCache();
// Pages that call the *arrs cut each call shorter themselves; this bounds the big ones (every film, for a refresh).
// Both send an API key header that .NET would carry to wherever a redirect leads: redirects stay on the same host.
builder.Services.AddHttpClient<ArrClient>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler(() => new SameHostRedirects());
builder.Services.AddHttpClient<SeerrClient>(c => c.Timeout = TimeSpan.FromSeconds(15))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler(() => new SameHostRedirects());
// Searching every subtitle provider, and re-syncing a film to its audio, can each take minutes.
builder.Services.AddHttpClient<BazarrClient>(c => c.Timeout = TimeSpan.FromMinutes(10))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false })
    .AddHttpMessageHandler(() => new SameHostRedirects());
builder.Services.AddHttpClient<TmdbClient>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddHttpClient<TvMazeClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Flexarr/0.1 (+https://github.com/danleach-glitch/flexarr)"));
// ImageCache follows redirects itself, only to https on the same site, so an artwork host can't bounce it elsewhere.
builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<PlexAvatars>(c => c.Timeout = TimeSpan.FromSeconds(30));
// Podcast feeds and episodes come from anywhere on the internet, and only from the internet (see PodcastFetcher).
// No overall timeout: a relayed episode streams for as long as it's listened to.
builder.Services.AddHttpClient<PodcastFetcher>(c => c.Timeout = Timeout.InfiniteTimeSpan)
    .ConfigurePrimaryHttpMessageHandler(PodcastFetcher.CreateHandler);
builder.Services.AddHttpClient<PodcastDirectory>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddScoped<PodcastLibrary>();
// Live TV from Dispatcharr's Xtream Codes API. Every address it calls carries the password, so the client's request
// logging is off; the guide can be tens of megabytes, hence the long timeout.
builder.Services.AddHttpClient<XtreamClient>(c => c.Timeout = TimeSpan.FromMinutes(5)).RemoveAllLoggers();
// Current channel logos from the community collection on GitHub (its file list, fetched weekly).
builder.Services.AddHttpClient(CommunityLogoIndex.ClientName, c =>
{
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Flexarr/0.1 (+https://github.com/danleach-glitch/flexarr)");
});
builder.Services.AddSingleton<CommunityLogoIndex>();
builder.Services.AddSingleton<SearchIndex>();
builder.Services.AddSingleton<BookFileConverter>();
builder.Services.AddSingleton<LiveTvStatus>();
builder.Services.AddSingleton<LiveTvRefreshSignal>();
builder.Services.AddSingleton<LiveTvViewers>();
builder.Services.AddScoped<LiveTvGuide>();
builder.Services.AddHostedService<LiveTvGuideWorker>();
// Live channels and films share the one transcode limit: each counts the other's (resolved when asked, as each needs the other).
builder.Services.AddSingleton(sp => new Flexarr.Streaming.Live.LiveStreams(
    () => PlaybackEndpoints.TranscodeSettingsFor(sp.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue),
    sp.GetRequiredService<ILoggerFactory>(),
    () => sp.GetRequiredService<TranscodeManager>().ActiveCount));
builder.Services.AddHostedService<LiveTvSweeper>();
builder.Services.AddSingleton<DvrSignal>();
builder.Services.AddSingleton<DvrService>();
builder.Services.AddHostedService<DvrScheduler>();
builder.Services.AddSingleton<PodcastRefreshQueue>();
builder.Services.AddHostedService<PodcastRefreshWorker>();
builder.Services.AddHostedService<PodcastChartWarmer>();
builder.Services.AddSingleton<ProfileImages>();
builder.Services.AddSingleton<PersonImages>();
builder.Services.AddSingleton<BackupStore>();
builder.Services.AddSingleton<BackupUploads>();
builder.Services.AddSingleton<IServerRestart, ServerRestart>();
builder.Services.AddHostedService<BackupWorker>();
// New releases from GitHub (Settings › Updates), and installing them on a Mac. The release lookup is small; a build is
// tens of megabytes.
builder.Services.AddHttpClient(UpdateChecker.ClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient(UpdateChecker.DownloadClientName, c =>
{
    c.Timeout = TimeSpan.FromMinutes(30);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("Flexarr (+https://github.com/danleach-glitch/flexarr)");
});
builder.Services.AddSingleton<IInstallEnvironment, InstallEnvironment>();
builder.Services.AddSingleton<MacAppHost>();
builder.Services.AddSingleton<IPostConfigureOptions<FlexarrOptions>, MacAppTranscoding>();
builder.Services.AddSingleton<UpdateChecker>();
builder.Services.AddSingleton<RuntimeInstaller>();
builder.Services.AddSingleton<MacUpdater>();
builder.Services.AddSingleton<ServerActivity>();
builder.Services.AddHostedService<UpdateWorker>();
builder.Services.AddSingleton<HealthCheck>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Pairings>();
builder.Services.AddSingleton<PairingAlerts>();
builder.Services.AddSingleton<GuestAlerts>();
builder.Services.AddSingleton<PinAlerts>();
builder.Services.AddSingleton<FirstRun>();
builder.Services.AddSingleton<DeviceCommands>();
builder.Services.AddSingleton<NowPlayingFeed>();
builder.Services.AddSingleton<LiveActivities>();
builder.Services.AddHostedService<LiveActivityWorker>();
builder.Services.AddHostedService<NowPlayingPublisher>();
builder.Services.AddHttpClient<PushRelay>(c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped<Notifier>();
builder.Services.AddHostedService<NotificationWorker>();
// What's playing where, as it changes, for Home Assistant: GET /api/v1/events.
builder.Services.AddSingleton<PlaybackEvents>();
builder.Services.AddHostedService<PlaybackEventPublisher>();
// Trakt, per profile (docs/watchlist-trakt.md): connect by code, scrobbles, history and the watchlist both ways.
builder.Services.AddHttpClient<TraktClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddScoped<TraktAccounts>();
builder.Services.AddScoped<TraktSync>();
builder.Services.AddScoped<TraktLists>();
builder.Services.AddScoped<TraktRecommendations>();
builder.Services.AddSingleton<TraktConnector>();
builder.Services.AddSingleton<TraktImports>();
builder.Services.AddHostedService<TraktScrobbler>();
builder.Services.AddHostedService<TraktOutboxWorker>();
builder.Services.AddHostedService<TraktSyncWorker>();
// An AI assistant's way in (Claude and the like): the same sign-in as a script, at /api/v1/mcp.
builder.Services.AddMcpServer(o =>
{
    o.ServerInfo = new ModelContextProtocol.Protocol.Implementation
    {
        Name = "flexarr",
        Title = "Flexarr",
        Version = typeof(Program).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion ?? "0.0.0",
    };
    o.ServerInstructions = "Flexarr is this household's media server (films, TV, music, podcasts). Find a title with search_library (podcasts with "
        + "podcasts and new_podcast_episodes), then play it on an Apple TV, iPhone or iPad from list_devices with play_on_device; a show's id plays "
        + "the episode that device's profile is up to, a podcast's the episode to hear next. add_to_up_next queues podcast episodes. Devices only "
        + "take commands while Flexarr is open on them.";
})
    .WithHttpTransport(o => o.Stateless = true)
    // MapMcp sits outside the /api/v1 group, so its tools get the profile's hidden libraries here.
    .WithRequestFilters(f => f.AddCallToolFilter(next => async (request, ct) =>
    {
        await LibraryVisibility.ApplyAsync(request.Services!, ct);
        return await next(request, ct);
    }))
    .WithTools([typeof(FlexarrTools), typeof(LiveTvTools), typeof(WatchlistTools)], new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Converters = { new JsonStringEnumConverter() },
    });
builder.Services.AddHostedService<BonjourAdvertiser>();
builder.Services.AddSingleton<FranchiseSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FranchiseSync>());
builder.Services.AddSingleton<IMetadataSettings, MetadataSettings>();
builder.Services.AddHttpClient("books", c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddScoped(sp => new Flexarr.Metadata.Books.BookProviders(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("books"), sp.GetRequiredService<IMetadataSettings>));
builder.Services.AddScoped<MetadataRefresher>();
builder.Services.AddScoped(sp => new AuthorInfo(sp.GetRequiredService<Flexarr.Core.Data.FlexarrDbContext>(), sp.GetRequiredService<Flexarr.Integrations.Arr.ArrClient>(),
    sp.GetRequiredService<IMetadataSettings>(), sp.GetRequiredService<Flexarr.Metadata.Books.BookProviders>(), sp.GetRequiredService<ILogger<AuthorInfo>>()));
builder.Services.AddScoped<PathMappingFinder>();

builder.Services.AddDbContext<FlexarrDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<FlexarrOptions>>().Value;
    db.UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "flexarr.db")}");
    // Items hidden from a profile should also drop out of the things that point at them (watch history, credits).
    db.ConfigureWarnings(w => w.Ignore(CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
});

builder.Services.AddSingleton<IMediaProbe>(sp => new FfprobeMediaProbe(sp.GetRequiredService<IOptions<FlexarrOptions>>().Value.FfprobePath));
builder.Services.AddSingleton<ScanProgress>();
builder.Services.AddScoped<LibraryScanner>();
builder.Services.AddSingleton(sp => new TranscodeManager(
    () => PlaybackEndpoints.TranscodeSettingsFor(sp.GetRequiredService<IOptionsMonitor<FlexarrOptions>>().CurrentValue),
    sp.GetRequiredService<ILoggerFactory>(),
    () => sp.GetRequiredService<Flexarr.Streaming.Live.LiveStreams>().ConvertingCount));
builder.Services.AddSingleton<PlaybackSessions>();
builder.Services.AddSingleton<MarkerQueue>();
builder.Services.AddSingleton<TrickplayQueue>();
builder.Services.AddSingleton<SubtitleCache>();
builder.Services.AddSingleton<SubtitleSyncs>();
builder.Services.AddSingleton<LyricsCache>();
builder.Services.AddSingleton<LoudnessCache>();
builder.Services.AddHostedService<TrickplayWorker>();
builder.Services.AddScoped<PlaybackService>();
builder.Services.AddScoped<WatchlistService>();
builder.Services.AddSingleton(sp => new Flexarr.Streaming.Remux.RemuxIndex(
    Flexarr.Api.Playback.PlaybackEndpoints.TranscodeSettingsFor(sp.GetRequiredService<IOptions<FlexarrOptions>>().Value),
    sp.GetRequiredService<IOptions<FlexarrOptions>>().Value.FfprobePath,
    sp.GetRequiredService<ILogger<Flexarr.Streaming.Remux.RemuxIndex>>()));
builder.Services.AddSingleton<DownloadPreparer>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadPreparer>());
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Flexarr.Api.Profiles.CurrentProfile>();
builder.Services.AddSingleton<TailscaleService>();
builder.Services.AddSingleton<ITailnetWhois>(sp => sp.GetRequiredService<TailscaleService>());
builder.Services.AddHttpClient(TailscaleApi.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(20));
builder.Services.AddSingleton<TailscaleApi>();
builder.Services.AddSingleton<ITailnet>(sp => sp.GetRequiredService<TailscaleService>());
builder.Services.AddSingleton<GuestPolicy>();
builder.Services.AddSingleton<GuestInvites>();
builder.Services.AddHttpClient(GuestTvCodes.HttpClientName, c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddSingleton<GuestTvCodes>();
builder.Services.AddScoped<GuestLinks>();
builder.Services.AddHostedService<GuestPolicyWatcher>();
builder.Services.AddHostedService<PlaybackSweeper>();
builder.Services.AddHostedService<MarkerDetectionWorker>();

builder.Services.AddSingleton<ILibraryScanQueue, LibraryScanQueue>();
builder.Services.AddHostedService<LibraryScanWorker>();

// JSON, the web app and playlists shrink a lot; video segments and images are compressed already, so aren't listed.
builder.Services.AddResponseCompression(o =>
{
    // Behind Tailscale or cloudflared most requests arrive as https.
    o.EnableForHttps = true;
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    o.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    o.MimeTypes = [.. Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults.MimeTypes,
        "application/problem+json", "application/vnd.apple.mpegurl", "application/x-mpegURL", "text/vtt", "image/svg+xml", "application/manifest+json"];
});
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);
builder.Services.Configure<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProviderOptions>(o => o.Level = System.IO.Compression.CompressionLevel.Fastest);

var app = builder.Build();
// The Mac app's sandbox can't run other programs, Tailscale's command included.
TailscaleService.Sandboxed = app.Services.GetRequiredService<IInstallEnvironment>().Kind == InstallKind.MacApp;
// config.json's watcher belongs to this server: let it go when the server stops (tests start many in one process, and
// Linux allows only so many watchers).
app.Lifetime.ApplicationStopped.Register(() => (userConfig as IDisposable)?.Dispose());

// Materialise options at startup so a first-run API key is generated and logged immediately.
Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value.DataDirectory);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
    var backups = app.Services.GetRequiredService<BackupStore>();
    if (restored)
    {
        backups.LogRestored();
    }

    // An upgrade is about to change the database: keep a copy of it as it was, to go back to.
    var migrating = (await db.Database.GetPendingMigrationsAsync()).Any();
    if (migrating && (await db.Database.GetAppliedMigrationsAsync()).Any())
    {
        await backups.BeforeUpgradeAsync(CancellationToken.None);
    }

    await db.Database.MigrateAsync();
    // Readers (the web app, players) don't wait on a scan's writes, or the other way round. Stored in the file, but
    // a database created or restored elsewhere may not have it.
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
    await PodcastLibrary.EnsureLibraryAsync(db, app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value, CancellationToken.None);
    // Age limits read each title's age: work them out for titles saved before, or rated by an older table of ratings.
    // Saving a title works out its own, so otherwise there's nothing to do.
    var started = app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value;
    if (migrating || restored || started.RatingAges != Flexarr.Core.Media.RatingAges.Version)
    {
        await db.ReconcileRatingAgesAsync(CancellationToken.None);
        started.RatingAges = Flexarr.Core.Media.RatingAges.Version;
        ServerConfigStore.Update(started.DataDirectory, config => config["ratingAges"] = Flexarr.Core.Media.RatingAges.Version);
    }

    // Managing the server now takes the admin PIN, and choosing the main profile doesn't by default. A server
    // whose owner had already given it a PIN goes on asking for it when it's chosen, as before.
    var flexarr = app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value;
    if (flexarr.AskPinForMainProfile is null)
    {
        var askPin = await db.Profiles.AnyAsync(p => p.Id == Flexarr.Core.Media.PlaybackState.DefaultUser && p.PinHash != null);
        flexarr.AskPinForMainProfile = askPin;
        ServerConfigStore.Update(flexarr.DataDirectory, config => config["askPinForMainProfile"] = askPin);
    }

    await Flexarr.Api.Profiles.PinSplit.SettleAsync(db, flexarr.AskPinForMainProfile == true, CancellationToken.None);
}

app.Use((http, next) =>
{
    RequestPeer.Capture(http);
    return next(http);
});
app.UseForwardedHeaders();
app.UseFlexarrGuestGate();
app.UseFlexarrLogging();
app.UseFlexarrSecurityHeaders();
app.UseResponseCompression();

// Vite names /assets/* by content hash, so they never change; index.html must be fetched fresh to pick up a deploy.
var webApp = new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        var headers = context.Context.Response.Headers;
        if (context.Context.Request.Path.StartsWithSegments("/assets", StringComparison.Ordinal))
        {
            headers.CacheControl = "public, max-age=31536000, immutable";
        }
        else if (context.File.Name == "index.html")
        {
            headers.CacheControl = "no-cache";
        }
    },
};
app.UseDefaultFiles();
app.UseStaticFiles(webApp);
app.UseAuthentication();
app.UseFlexarrRequestForgeryGuard();
app.UseAuthorization();

// The API description is for development; a public server needn't hand out a map of itself.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

app.MapHealthChecks("/health");

app.MapGroup("/api/v1")
    .RequireAuthorization()
    .ApplyLibraryVisibility()
    .MapSystemEndpoints()
    .MapWebhookEndpoints()
    .MapLibraryEndpoints()
    .MapItemEndpoints()
    .MapLyricsEndpoints()
    .MapLoudnessEndpoints()
    .MapFilesystemEndpoints()
    .MapPeopleEndpoints()
    .MapPlaybackEndpoints()
    .MapDownloadEndpoints()
    .MapDiscoveryEndpoints()
    .MapRecentSearchEndpoints()
    .MapSettingsEndpoints()
    .MapSetupGuideEndpoints()
    .MapImportEndpoints()
    .MapBackupEndpoints()
    .MapUpdateEndpoints()
    .MapUpcomingEndpoints()
    .MapHealthEndpoints()
    .MapMatchEndpoints()
    .MapDeleteEndpoints()
    .MapLayoutEndpoints()
    .MapPairingApprovalEndpoints()
    .MapCollectionEndpoints()
    .MapPlaylistEndpoints()
    .MapPodcastEndpoints()
    .MapBookEndpoints()
    .MapBookMatchEndpoints()
    .MapBookRequestEndpoints()
    .MapLiveTvEndpoints()
    .MapDvrEndpoints()
    .MapMusicMixEndpoints()
    .MapRequestEndpoints()
    .MapWatchlistEndpoints()
    .MapTraktEndpoints()
    .MapSubtitleSearchEndpoints()
    .MapProfileEndpoints()
    .MapRemoteAccessEndpoints()
    .MapRemoteGuestEndpoints()
    .MapDeviceEndpoints()
    .MapNotificationEndpoints()
    .MapEventEndpoints()
    .MapLogEndpoints()
    .MapSessionEndpoints()
    .MapAdminEndpoints();

app.MapMcp("/api/v1/mcp").RequireAuthorization();

// Artwork and media are fetched by <img>/<video> tags and native players, which can't send the API key header.
app.MapGroup("/api/v1").MapImageEndpoints().MapStreamEndpoints().MapLiveTvStreamEndpoints().MapProfileImageEndpoints().MapPairingDeviceEndpoints().MapFirstRunEndpoints().MapGuestRedeemEndpoints();

// Emby/Jellyfin-compatible endpoints for the *arr apps' built-in connection.
app.MapMediaBrowserEndpoints();

// The web client is a SPA; let it own every non-API route.
app.MapFallbackToFile("index.html", webApp);

// A server with no owner yet says its setup code in the log, where whoever installed it will look.
// Once the server is listening, so the setup link in the log has the port it really uses.
app.Lifetime.ApplicationStarted.Register(() => app.Services.GetRequiredService<FirstRun>().Announce());
app.Services.GetRequiredService<PinAlerts>().Watch(app.Services.GetRequiredService<PinGuard>());

await app.RunAsync();

public partial class Program;
