using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Display;

namespace Flexarr.Api.Logging;

public static class LoggingSetup
{
    /// <summary>
    /// One file a day as JSON lines (what the Logs page reads), split at 10 MB, kept as long as the retention settings
    /// say (see <see cref="LogJanitor"/>); plain text on standard output; and the live feed for the Logs page.
    /// </summary>
    public static void AddFlexarrLogging(this WebApplicationBuilder builder, string dataDirectory, IConfiguration userConfig)
    {
        var settings = new LogOptions();
        builder.Configuration.GetSection($"{FlexarrOptions.SectionName}:Logs").Bind(settings);
        userConfig.GetSection("logs").Bind(settings);

        var paths = new LogPaths(settings.ResolvedDirectory(dataDirectory));
        var levels = new LogLevels();
        levels.Update(settings);
        var feed = new LogFeed();
        var errorReports = new ErrorReports(settings.ErrorReportsDsn);

        builder.Services.AddSingleton(paths);
        builder.Services.AddSingleton(levels);
        builder.Services.AddSingleton(feed);
        builder.Services.AddSingleton(errorReports);
        builder.Services.AddSingleton<LogReader>();
        builder.Services.AddSingleton<ClientLogs>();
        builder.Services.AddScoped<Diagnostics>();
        builder.Services.AddHostedService<LogJanitor>();

        builder.Services.AddSerilog((services, configuration) => levels.Apply(configuration)
            .Enrich.FromLogContext()
            .Enrich.With<AreaEnricher>()
            .WriteTo.File(
                LogFormats.Json(),
                paths.FilePattern,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: 10 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: null,
                flushToDiskInterval: TimeSpan.FromSeconds(1))
            .WriteTo.Console(
                new RedactingFormatter(new MessageTemplateTextFormatter("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")),
                restrictedToMinimumLevel: settings.ConsoleLevel)
            .WriteTo.Sink(feed)
            .WriteTo.Sink(errorReports),
            // This server's own logger, not Serilog's global one: several servers in one process (the tests) otherwise
            // write into whichever was set up last.
            preserveStaticLogger: true);
    }

    /// <summary>Keeps the levels and the list of secrets to redact in step with the settings.</summary>
    public static void UseFlexarrLogging(this WebApplication app)
    {
        var options = app.Services.GetRequiredService<IOptionsMonitor<FlexarrOptions>>();
        var levels = app.Services.GetRequiredService<LogLevels>();
        var errorReports = app.Services.GetRequiredService<ErrorReports>();
        Apply(options.CurrentValue);
        options.OnChange(Apply);

        void Apply(FlexarrOptions current)
        {
            levels.Update(current.Logs);
            errorReports.Apply(current.Logs);
            Redactor.UseSecrets([
                current.ApiKey, current.WebhookSecret, current.ImageSecret, current.Seerr?.ApiKey, current.LiveTv?.Password,
                .. current.Arr.Select(a => a.ApiKey),
            ]);
        }

        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "{RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0} ms";
            o.GetLevel = (http, _, ex) => ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                // Apps poll and pages load dozens of things; a failure is news, a success is detail for Debug.
                : http.Response.StatusCode >= 400 && http.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal)
                    && !http.Request.Path.StartsWithSegments("/api/v1/logs", StringComparison.Ordinal) ? LogEventLevel.Information
                : LogEventLevel.Debug;
            o.EnrichDiagnosticContext = (context, http) =>
            {
                context.Set("RequestId", http.TraceIdentifier);
                if (http.User.FindFirst(Security.FlexarrClaims.Profile)?.Value is { } profile)
                {
                    context.Set("ProfileId", profile);
                }
            };
        });
    }
}
