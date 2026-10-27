using System.Reflection;
using System.Text.RegularExpressions;
using Sentry;
using Serilog.Core;
using Serilog.Events;

namespace Flexarr.Api.Logging;

/// <summary>
/// Opt-in error reports to Flexarr's developer, through Sentry. Off unless the main profile turns it on in Settings ›
/// Logs; the apps read the same switch from /system/status. Only errors go, never the log, and each report is
/// scrubbed here first (<see cref="Scrub"/>): no titles, paths, addresses, names, keys or passwords leave the server.
/// Switching off closes the SDK at once.
/// </summary>
public sealed partial class ErrorReports : ILogEventSink, IDisposable
{
    public const string ServerDsn = "https://de6f5ded7bc8210b2f1a96227d236645@o4512326167232512.ingest.de.sentry.io/4512326194167888";

    private static readonly string Version =
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "0.0.0";

    private readonly string _dsn;
    private readonly Lock _lock = new();
    private IDisposable? _sdk;
    private volatile string? _installId;

    public ErrorReports(string dsn) => _dsn = dsn;

    public bool Sending => _installId is not null;

    /// <summary>Starts or stops sending to match the settings.</summary>
    public void Apply(LogOptions options)
    {
        lock (_lock)
        {
            var on = options.ErrorReports && options.ErrorReportsId is { Length: > 0 } && _dsn.Length > 0;
            if (on && _sdk is null)
            {
                _installId = options.ErrorReportsId;
                _sdk = SentrySdk.Init(Configure);
            }
            else if (!on && _sdk is not null)
            {
                _installId = null;
                _sdk.Dispose();
                _sdk = null;
            }
        }
    }

    /// <summary>Errors in the log become reports: the message as the code wrote it (its template, without the values) and the exception.</summary>
    public void Emit(LogEvent logEvent)
    {
        if (logEvent.Level < LogEventLevel.Error || !Sending)
        {
            return;
        }

        var source = logEvent.Properties.TryGetValue("SourceContext", out var s) && s is ScalarValue { Value: string name } ? name : null;
        var area = LogAreas.Of(source);

        // The apps send their own, with their own SDKs; their reports here would be counted twice.
        if (area == LogAreas.Clients)
        {
            return;
        }

        var report = logEvent.Exception is { } exception ? new SentryEvent(exception) : new SentryEvent();
        report.Message = new SentryMessage { Message = logEvent.MessageTemplate.Text };
        report.Level = logEvent.Level == LogEventLevel.Fatal ? SentryLevel.Fatal : SentryLevel.Error;
        report.SetTag("area", area);
        if (source is not null)
        {
            report.SetTag("logger", source);
        }

        SentrySdk.CaptureEvent(report);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _installId = null;
            _sdk?.Dispose();
            _sdk = null;
        }
    }

    private void Configure(SentryOptions o)
    {
        o.Dsn = _dsn;
        o.Release = $"flexarr-server@{Version}";
#if DEBUG
        o.Environment = "development"; // dotnet run / watch, apart from published releases
#else
        o.Environment = "production";
#endif
        o.SendDefaultPii = false;
        o.ServerName = "";
        o.AutoSessionTracking = false;
        o.TracesSampleRate = 0;
        o.MaxBreadcrumbs = 0;
        o.SendClientReports = false;
        o.ReportAssembliesMode = ReportAssembliesMode.None;
        o.AttachStacktrace = true;
        o.IsGlobalModeEnabled = true;
        o.ShutdownTimeout = TimeSpan.FromSeconds(2);
        o.SetBeforeSend((report, _) => Clean(report));
    }

    private SentryEvent? Clean(SentryEvent report)
    {
        if (_installId is not { } id)
        {
            return null;
        }

        try
        {
            report.ServerName = null;
            report.User = new SentryUser { Id = id };
            report.Request = new SentryRequest();
            // The device's name, time zone, language and boot time say who and where; the OS and runtime are enough.
            report.Contexts.Device.Name = null;
            report.Contexts.Device.Timezone = null;
            report.Contexts.Device.BootTime = null;
            report.Contexts.Remove("Current Culture");
            report.Contexts.Remove("Current UI Culture");
            if (report.Message is { } message)
            {
                report.Message = new SentryMessage { Message = Scrub(message.Message ?? message.Formatted ?? "") };
            }

            foreach (var exception in report.SentryExceptions ?? [])
            {
                exception.Value = exception.Value is null ? null : Scrub(exception.Value);
            }

            return report;
        }
        catch (Exception)
        {
            // Better no report than one that might not be clean.
            return null;
        }
    }

    /// <summary>
    /// What's left of a message once everything about the user is gone: the log's redaction (keys, passwords, tokens),
    /// then addresses, file paths, email addresses and anything in quotes, which is where titles and names appear.
    /// </summary>
    public static string Scrub(string text)
    {
        text = Redactor.Redact(text);

        // Quotes first: .NET quotes the paths it complains about, and a quoted path may have spaces in it.
        text = Quoted().Replace(text, "\"…\"");
        text = Url().Replace(text, "<server>");
        text = WindowsPath().Replace(text, "<path>");
        text = UnixPath().Replace(text, "<path>");
        text = MediaFile().Replace(text, "<file>");
        text = Email().Replace(text, "<email>");
        text = IPv4().Replace(text, "<ip>");
        return IPv6().Replace(text, "<ip>");
    }

    [GeneratedRegex(@"\b[a-z][a-z0-9+.-]*://[^\s/""'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex Url();

    [GeneratedRegex(@"\b[A-Za-z]:\\[^\s""'<>]*")]
    private static partial Regex WindowsPath();

    // A path runs on through any space-separated pieces that still have a slash in them ("…/Dune (2021)/Dune.mkv").
    [GeneratedRegex(@"(?<![\w.<>])~?/(?:Users|Volumes|home|mnt|media|data|config|srv|opt|var|tmp|private|root|Library|downloads|tv|movies|music)(?:/[^\s""'<>:]*)?(?:\s+[^\s""'<>]*/[^\s""'<>]*)*", RegexOptions.IgnoreCase)]
    private static partial Regex UnixPath();

    [GeneratedRegex(@"[^\s""'<>/\\]+\.(?:mkv|mp4|m4v|avi|mov|wmv|ts|m2ts|webm|mpg|mpeg|mp3|flac|m4a|m4b|aac|ogg|opus|wav|srt|ass|ssa|vtt|sub|idx|sup|nfo|jpg|jpeg|png)\b", RegexOptions.IgnoreCase)]
    private static partial Regex MediaFile();

    [GeneratedRegex(@"[\w.+-]+@[\w-]+(?:\.[\w-]+)+")]
    private static partial Regex Email();

    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b")]
    private static partial Regex IPv4();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{1,4}:){3,7}[0-9A-Fa-f]{1,4}\b")]
    private static partial Regex IPv6();

    // A single quote only opens after a space or bracket, so "Couldn't" isn't taken for a quotation.
    [GeneratedRegex(@"“[^”]*”|‘[^’]*’|""[^""]*""|(?<![\w])'[^'\n]*'")]
    private static partial Regex Quoted();
}
