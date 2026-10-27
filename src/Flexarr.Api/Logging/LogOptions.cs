using Serilog.Events;

namespace Flexarr.Api.Logging;

/// <summary>Where the server's log goes, how much is kept, and how much detail each part of the server writes.</summary>
public sealed class LogOptions
{
    /// <summary>
    /// The folder for the log files and the diagnostics devices send. Defaults to &lt;DataDirectory&gt;/logs. Read at
    /// startup.
    /// </summary>
    public string? Directory { get; set; }

    /// <summary>The least serious entries written, everywhere an area doesn't say otherwise.</summary>
    public LogEventLevel Level { get; set; } = LogEventLevel.Information;

    /// <summary>Areas (see <see cref="LogAreas"/>) that write more or less than <see cref="Level"/>, by key.</summary>
    public Dictionary<string, LogEventLevel> Areas { get; set; } = [];

    /// <summary>Log files and device diagnostics older than this are deleted.</summary>
    public int RetentionDays { get; set; } = 14;

    /// <summary>The most the log folder may hold; the oldest files go first.</summary>
    public int MaxSizeMb { get; set; } = 100;

    /// <summary>
    /// The least serious entries also written to standard output. The macOS service sets Warning, since launchd keeps
    /// standard output in a file nothing trims; Docker keeps Information for <c>docker logs</c>. Read at startup.
    /// </summary>
    public LogEventLevel ConsoleLevel { get; set; } = LogEventLevel.Information;

    /// <summary>
    /// Send errors to Flexarr's developer through Sentry (see <see cref="ErrorReports"/>). Off unless the main profile
    /// turns it on; the apps follow it too.
    /// </summary>
    public bool ErrorReports { get; set; }

    /// <summary>A random id for this server in error reports, so one install's repeats can be told apart. Nothing else.</summary>
    public string? ErrorReportsId { get; set; }

    /// <summary>Where the server's error reports go. Empty sends none (the tests). Read at startup.</summary>
    public string ErrorReportsDsn { get; set; } = Logging.ErrorReports.ServerDsn;

    public string ResolvedDirectory(string dataDirectory) => Path.GetFullPath(Directory ?? Path.Combine(dataDirectory, "logs"));
}
