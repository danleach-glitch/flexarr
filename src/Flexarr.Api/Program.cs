using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Collections;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Import;
using Flexarr.Api.Playback;
using Flexarr.Api.Profiles;
using Flexarr.Api.Scanning;
using Flexarr.Api.Security;
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
var userConfig = new ConfigurationBuilder()
    .AddJsonFile(ServerConfigStore.PathFor(dataDirectory), optional: true, reloadOnChange: true)
    .Build();

// config.json overrides appsettings and is reloaded when it changes, so settings apply without a restart.
builder.Services.AddOptions<FlexarrOptions>()
    .BindConfiguration(FlexarrOptions.SectionName)
    .Configure(options => userConfig.Bind(options));
builder.Services.AddSingleton<IOptionsChangeTokenSource<FlexarrOptions>>(new ConfigurationChangeTokenSource<FlexarrOptions>(userConfig));
builder.Services.AddSingleton<IPostConfigureOptions<FlexarrOptions>, ServerConfigStore>();

builder.Services
    .AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(FlexarrClaims.AdminPolicy, policy => policy.AddRequirements(new AdminRequirement()));
builder.Services.AddScoped<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, AdminHandler>();

// Tailscale serve and cloudflared proxy from this machine; trust their forwarded headers, and only theirs.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    o.ForwardLimit = 1;
    o.KnownProxies.Clear();
    o.KnownProxies.Add(System.Net.IPAddress.Loopback);
    o.KnownProxies.Add(System.Net.IPAddress.IPv6Loopback);
});

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddMemoryCache();
// Pages that call the *arrs cut each call shorter themselves; this bounds the big ones (every film, for a refresh).
builder.Services.AddHttpClient<ArrClient>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddHttpClient<SeerrClient>(c => c.Timeout = TimeSpan.FromSeconds(15));
builder.Services.AddHttpClient<TvMazeClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Flexarr/0.1 (+https://github.com/danleach-glitch/flexarr)"));
// ImageCache follows redirects itself, only to https on the same site, so an artwork host can't bounce it elsewhere.
builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<PlexAvatars>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<ProfileImages>();
builder.Services.AddSingleton<HealthCheck>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<Pairings>();
builder.Services.AddHostedService<BonjourAdvertiser>();
builder.Services.AddSingleton<FranchiseSync>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<FranchiseSync>());
builder.Services.AddSingleton<IMetadataSettings, MetadataSettings>();
builder.Services.AddScoped<MetadataRefresher>();

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
    sp.GetRequiredService<ILoggerFactory>()));
builder.Services.AddSingleton<PlaybackSessions>();
builder.Services.AddSingleton<MarkerQueue>();
builder.Services.AddSingleton<TrickplayQueue>();
builder.Services.AddSingleton<SubtitleCache>();
builder.Services.AddHostedService<TrickplayWorker>();
builder.Services.AddScoped<PlaybackService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<Flexarr.Api.Profiles.CurrentProfile>();
builder.Services.AddSingleton<TailscaleService>();
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

// Materialise options at startup so a first-run API key is generated and logged immediately.
Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value.DataDirectory);

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Database.MigrateAsync();
}

app.Use((http, next) =>
{
    RequestPeer.Capture(http);
    return next(http);
});
app.UseForwardedHeaders();
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
app.UseAuthorization();

app.MapOpenApi();
if (app.Environment.IsDevelopment())
{
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
    .MapFilesystemEndpoints()
    .MapPeopleEndpoints()
    .MapPlaybackEndpoints()
    .MapDiscoveryEndpoints()
    .MapSettingsEndpoints()
    .MapImportEndpoints()
    .MapUpcomingEndpoints()
    .MapHealthEndpoints()
    .MapDeleteEndpoints()
    .MapLayoutEndpoints()
    .MapPairingApprovalEndpoints()
    .MapCollectionEndpoints()
    .MapRequestEndpoints()
    .MapProfileEndpoints()
    .MapRemoteAccessEndpoints()
    .MapSessionEndpoints();

// Artwork and media are fetched by <img>/<video> tags and native players, which can't send the API key header.
app.MapGroup("/api/v1").MapImageEndpoints().MapStreamEndpoints().MapProfileImageEndpoints().MapPairingDeviceEndpoints();

// Emby/Jellyfin-compatible endpoints for the *arr apps' built-in connection.
app.MapMediaBrowserEndpoints();

// The web client is a SPA; let it own every non-API route.
app.MapFallbackToFile("index.html", webApp);

await app.RunAsync();

public partial class Program;
