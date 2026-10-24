using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Import;
using Flexarr.Api.Playback;
using Flexarr.Api.Scanning;
using Flexarr.Core.Data;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Flexarr.Streaming;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
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
builder.Services.AddAuthorization();

builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddOpenApi();
builder.Services.AddHealthChecks();
builder.Services.AddHttpClient<ArrClient>();
builder.Services.AddHttpClient<TvMazeClient>(c => c.DefaultRequestHeaders.UserAgent.ParseAdd("Flexarr/0.1 (+https://github.com/danleach-glitch/flexarr)"));
builder.Services.AddHttpClient<ImageCache>(c => c.Timeout = TimeSpan.FromSeconds(30));
builder.Services.AddSingleton<IMetadataSettings, MetadataSettings>();
builder.Services.AddScoped<MetadataRefresher>();

builder.Services.AddDbContext<FlexarrDbContext>((sp, db) =>
{
    var options = sp.GetRequiredService<IOptions<FlexarrOptions>>().Value;
    db.UseSqlite($"Data Source={Path.Combine(options.DataDirectory, "flexarr.db")}");
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
builder.Services.AddHostedService<PlaybackSweeper>();
builder.Services.AddHostedService<MarkerDetectionWorker>();

builder.Services.AddSingleton<ILibraryScanQueue, LibraryScanQueue>();
builder.Services.AddHostedService<LibraryScanWorker>();

var app = builder.Build();

// Materialise options at startup so a first-run API key is generated and logged immediately.
Directory.CreateDirectory(app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value.DataDirectory);

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().Database.MigrateAsync();
}

app.UseDefaultFiles();
app.UseStaticFiles();
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
    .MapHealthEndpoints();

// Artwork and media are fetched by <img>/<video> tags and native players, which can't send the API key header.
app.MapGroup("/api/v1").MapImageEndpoints().MapStreamEndpoints();

// The web client is a SPA; let it own every non-API route.
app.MapFallbackToFile("index.html");

await app.RunAsync();

public partial class Program;
