using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Flexarr.Api.Logging;

/// <summary>
/// The levels in force, changed in place when the settings change, so turning up Playback's detail needs no restart.
/// </summary>
public sealed class LogLevels
{
    /// <summary>
    /// Framework loggers that say something for every request or outside call. They stay at Warning until the server
    /// as a whole is turned up to Debug; at Information they were most of the old log.
    /// </summary>
    private static readonly string[] Chatty =
    [
        "Microsoft.AspNetCore", "Microsoft.EntityFrameworkCore", "Microsoft.Extensions.Http", "System.Net.Http.HttpClient",
        "Flexarr.Api.Authentication.ApiKeyAuthenticationHandler",
    ];

    private readonly LoggingLevelSwitch _default = new();
    private readonly LoggingLevelSwitch _chatty = new(LogEventLevel.Warning);
    private readonly Dictionary<string, LoggingLevelSwitch> _areas = LogAreas.All.ToDictionary(a => a.Key, _ => new LoggingLevelSwitch());

    public LoggerConfiguration Apply(LoggerConfiguration configuration)
    {
        configuration.MinimumLevel.ControlledBy(_default);
        foreach (var prefix in Chatty)
        {
            configuration.MinimumLevel.Override(prefix, _chatty);
        }

        // Startup and shutdown ("Now listening on…") are worth keeping.
        configuration.MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information);
        foreach (var area in LogAreas.All)
        {
            foreach (var prefix in area.Prefixes.Where(p => p.StartsWith("Flexarr.", StringComparison.Ordinal) || p.StartsWith("Serilog.", StringComparison.Ordinal)))
            {
                configuration.MinimumLevel.Override(prefix, _areas[area.Key]);
            }
        }

        return configuration;
    }

    public void Update(LogOptions options)
    {
        _default.MinimumLevel = options.Level;
        _chatty.MinimumLevel = options.Level <= LogEventLevel.Debug ? LogEventLevel.Information : LogEventLevel.Warning;
        foreach (var (key, level) in _areas)
        {
            level.MinimumLevel = options.Areas.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) is { Key: not null } area
                ? area.Value
                : options.Level;
        }
    }
}
