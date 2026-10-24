using System.Text.Json.Serialization;
using Flexarr.Api.Authentication;
using Flexarr.Api.Configuration;
using Flexarr.Api.Endpoints;
using Flexarr.Api.Scanning;
using Flexarr.Core.Scanning;
using Flexarr.Integrations.Arr;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<FlexarrOptions>().BindConfiguration(FlexarrOptions.SectionName);
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

builder.Services.AddSingleton<ILibraryScanQueue, LibraryScanQueue>();
builder.Services.AddHostedService<LibraryScanWorker>();

var app = builder.Build();

// Materialise options at startup so a first-run API key is generated and logged immediately.
_ = app.Services.GetRequiredService<IOptions<FlexarrOptions>>().Value;

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
    .MapWebhookEndpoints();

// The web client is a SPA; let it own every non-API route.
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program;
