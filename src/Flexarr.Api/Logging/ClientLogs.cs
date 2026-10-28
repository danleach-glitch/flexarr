using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using Serilog.Events;

namespace Flexarr.Api.Logging;

public sealed record ClientLogBatch(string Platform, string? AppVersion, IReadOnlyList<ClientLogLine> Entries);

public sealed record ClientLogLine(DateTimeOffset? Timestamp, LogEventLevel Level, string? Category, string Message, string? Error,
    Dictionary<string, string>? Properties);

/// <summary>Diagnostics a device sent: the file is the device's own log, the rest is who sent it.</summary>
public sealed record ClientUpload(string Id, string Device, string Platform, string? Profile, string? AppVersion, string? Note, DateTimeOffset UploadedAt,
    long SizeBytes);

/// <summary>Who's reporting: the sign-in's name and profile, worked out by the endpoint.</summary>
public sealed record ClientIdentity(string Key, string Device, string? Profile);

/// <summary>
/// What the apps and browsers report. Warnings and errors arrive on their own and go into the server's log under
/// "Apps and browsers", so one place shows a failed playback from both ends. A full device log arrives only when
/// someone taps Send diagnostics, and is kept as its own file beside the log.
/// </summary>
public sealed partial class ClientLogs(LogPaths paths, TimeProvider clock, ILoggerFactory loggers)
{
    public const int MaxBatch = 100;
    public const int MaxUploadBytes = 5 * 1024 * 1024;

    /// <summary>Per device, per minute. A browser stuck in a render loop mustn't fill the disk.</summary>
    private const int MaxPerMinute = 300;

    /// <summary>Diagnostics from one device in an hour: plenty for someone sending them by hand.</summary>
    public const int MaxUploadsPerHour = 6;

    /// <summary>
    /// All diagnostics together. They're cleared oldest first to stay under it, apart from the server's own log, so
    /// uploads can't push out the record of what the server did.
    /// </summary>
    public const long UploadsBudgetBytes = 100L * 1024 * 1024;

    private const int MaxText = 8 * 1024;
    private const int MaxProperties = 20;

    /// <summary>Stands in for a device's line breaks, so its text can't pass for lines of the server's own.</summary>
    public const string LineBreak = "⏎";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<string, (long Minute, int Count)> _rates = new();
    private readonly ConcurrentDictionary<string, (long Hour, int Count)> _uploadRates = new();

    /// <summary>Reports go through the server's own logging, so they're filtered, redacted and stored like the rest.</summary>
    private readonly ILogger _log = loggers.CreateLogger(LogAreas.ClientSource);

    /// <summary>Writes the batch to the log; false when the device has sent too much this minute.</summary>
    public bool Write(ClientIdentity who, ClientLogBatch batch)
    {
        var minute = clock.GetUtcNow().ToUnixTimeSeconds() / 60;
        var rate = _rates.AddOrUpdate(who.Key, _ => (minute, batch.Entries.Count),
            (_, r) => r.Minute == minute ? (minute, r.Count + batch.Entries.Count) : (minute, batch.Entries.Count));
        if (rate.Count > MaxPerMinute)
        {
            return false;
        }

        foreach (var line in batch.Entries.Take(MaxBatch))
        {
            var properties = new Dictionary<string, object>
            {
                ["Platform"] = OneLine(Clip(batch.Platform, 16)),
                ["Device"] = OneLine(who.Device),
            };
            Add(properties, "Profile", who.Profile);
            Add(properties, "AppVersion", batch.AppVersion);
            Add(properties, "Category", line.Category);
            Add(properties, "ClientError", line.Error);

            // A device's clock can be off; its own time is kept, the entry is filed when it arrived.
            Add(properties, "ClientTime", line.Timestamp?.ToString("O", CultureInfo.InvariantCulture));
            foreach (var (name, value) in (line.Properties ?? []).Take(MaxProperties))
            {
                // Their names become ours: keep them to plain words, and never let a device overwrite ours.
                var key = name.Length is > 0 and <= 40 && name.All(char.IsAsciiLetterOrDigit) ? char.ToUpperInvariant(name[0]) + name[1..] : null;
                if (key is not null && !properties.ContainsKey(key) && key != "ClientMessage")
                {
                    properties[key] = OneLine(Clip(value, 200));
                }
            }

            using (_log.BeginScope(properties))
            {
                LogReport(_log, Level(line.Level), OneLine(Clip(line.Message)));
            }
        }

        return true;
    }

    /// <summary>Counts an upload against the device's hourly allowance; false when it's used up.</summary>
    public bool TryStartUpload(ClientIdentity who)
    {
        ArgumentNullException.ThrowIfNull(who);
        var hour = clock.GetUtcNow().ToUnixTimeSeconds() / 3600;
        var rate = _uploadRates.AddOrUpdate(who.Key, _ => (hour, 1), (_, r) => r.Hour == hour ? (hour, r.Count + 1) : (hour, 1));
        return rate.Count <= MaxUploadsPerHour;
    }

    public async Task<ClientUpload> SaveUploadAsync(ClientIdentity who, string platform, string? appVersion, string? note, Stream body, CancellationToken ct)
    {
        Directory.CreateDirectory(paths.UploadsDirectory);
        MakeRoomForUpload();
        var now = clock.GetUtcNow();
        var id = $"{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..24];
        var path = Path.Combine(paths.UploadsDirectory, id + ".log");

        // Redacted line by line on the way in: the app does its best, but this file goes into bug reports.
        long size = 0;
        using (var reader = new StreamReader(body))
        await using (var writer = new StreamWriter(path))
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                var clean = Redactor.Redact(line);
                size += clean.Length + 1;
                if (size > MaxUploadBytes)
                {
                    await writer.WriteLineAsync("… (cut short: diagnostics are limited to 5 MB)");
                    break;
                }

                await writer.WriteLineAsync(clean);
            }
        }

        var upload = new ClientUpload(id, who.Device, Clip(platform, 16), who.Profile, appVersion is null ? null : Clip(appVersion, 40),
            note is null ? null : Redactor.Redact(Clip(note, 500)), now, new FileInfo(path).Length);
        await File.WriteAllTextAsync(Path.Combine(paths.UploadsDirectory, id + ".json"), JsonSerializer.Serialize(upload, Json), ct);
        LogUploaded(_log, who.Device, upload.Platform, upload.SizeBytes / 1024);
        return upload;
    }

    /// <summary>Deletes the oldest diagnostics until a new one fits in <see cref="UploadsBudgetBytes"/>.</summary>
    private void MakeRoomForUpload()
    {
        var files = new DirectoryInfo(paths.UploadsDirectory).EnumerateFiles().OrderBy(f => f.LastWriteTimeUtc).ToList();
        var total = files.Sum(f => f.Length);
        foreach (var file in files)
        {
            if (total + MaxUploadBytes <= UploadsBudgetBytes)
            {
                return;
            }

            try
            {
                file.Delete();
                total -= file.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Tried again before the next upload.
            }
        }
    }

    public IReadOnlyList<ClientUpload> Uploads()
    {
        var folder = new DirectoryInfo(paths.UploadsDirectory);
        if (!folder.Exists)
        {
            return [];
        }

        var uploads = new List<ClientUpload>();
        foreach (var file in folder.EnumerateFiles("*.json"))
        {
            try
            {
                if (JsonSerializer.Deserialize<ClientUpload>(File.ReadAllText(file.FullName), Json) is { } upload && File.Exists(UploadPath(upload.Id)))
                {
                    uploads.Add(upload);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                // Half-written or being tidied away.
            }
        }

        return [.. uploads.OrderByDescending(u => u.UploadedAt)];
    }

    /// <summary>The upload's file, or null for an id that isn't one (ids are only ever ours, never a path).</summary>
    public string? UploadPath(string id) =>
        id.Length == 24 && id.All(c => char.IsAsciiDigit(c) || c == '-' || char.IsAsciiHexDigitLower(c))
            ? Path.Combine(paths.UploadsDirectory, id + ".log")
            : null;

    private static LogLevel Level(LogEventLevel level) => level switch
    {
        <= LogEventLevel.Debug => LogLevel.Debug,
        LogEventLevel.Information => LogLevel.Information,
        LogEventLevel.Warning => LogLevel.Warning,
        _ => LogLevel.Error,
    };

    private static void Add(Dictionary<string, object> properties, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            properties[name] = OneLine(Clip(value, name == "ClientError" ? MaxText : 200));
        }
    }

    // :l writes the text as it came, without quotes; the app's message is data, never a template.
    [LoggerMessage(Message = "{ClientMessage:l}")]
    private static partial void LogReport(ILogger logger, LogLevel level, string clientMessage);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Device} sent diagnostics ({Platform}, {SizeKb} KB)")]
    private static partial void LogUploaded(ILogger logger, string device, string platform, long sizeKb);

    /// <summary>
    /// A device's text as one line: its line breaks become <see cref="LineBreak"/> (the log reader turns a reported error's back
    /// into real ones) and other control characters, terminal colour codes among them, are dropped.
    /// </summary>
    private static string OneLine(string text)
    {
        text = text.Replace("\r\n", LineBreak, StringComparison.Ordinal).Replace("\n", LineBreak, StringComparison.Ordinal)
            .Replace("\r", LineBreak, StringComparison.Ordinal).Replace("\u2028", LineBreak, StringComparison.Ordinal)
            .Replace("\u2029", LineBreak, StringComparison.Ordinal);
        return text.Any(c => char.IsControl(c) && c != '\t') ? string.Concat(text.Where(c => !char.IsControl(c) || c == '\t')) : text;
    }

    private static string Clip(string text, int max = MaxText) => text.Length <= max ? text : text[..max] + "…";
}
