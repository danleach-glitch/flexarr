using System.Text.Json;
using Serilog.Events;

namespace Flexarr.Api.Logging;

/// <summary>One line of the log as the Logs page shows it.</summary>
public sealed record LogEntry(
    string Id,
    DateTimeOffset Timestamp,
    LogEventLevel Level,
    string Area,
    string Source,
    string Message,
    string? Exception,
    IReadOnlyDictionary<string, string> Properties)
{
    /// <summary>Kept out of <see cref="Properties"/>: they're shown as columns, or are noise.</summary>
    private static readonly HashSet<string> Columns = ["SourceContext", AreaEnricher.Property, "EventId", "MessageTemplate", "ClientError", "ClientMessage", "ConnectionId"];

    /// <summary>
    /// Reads a line of the log file (Serilog's rendered compact JSON, already redacted). Null for anything else, such
    /// as a line cut short by a crash.
    /// </summary>
    public static LogEntry? Parse(string line, string id)
    {
        if (line.Length < 2 || line[0] != '{')
        {
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            if (!root.TryGetProperty("@t", out var t) || !t.TryGetDateTimeOffset(out var timestamp))
            {
                return null;
            }

            var level = root.TryGetProperty("@l", out var l) && Enum.TryParse<LogEventLevel>(l.GetString(), out var parsed) ? parsed : LogEventLevel.Information;
            var source = root.TryGetProperty("SourceContext", out var sc) ? sc.GetString() ?? "" : "";
            var area = root.TryGetProperty(AreaEnricher.Property, out var a) ? a.GetString() ?? LogAreas.Of(source) : LogAreas.Of(source);
            var exception = root.TryGetProperty("@x", out var x) ? x.GetString()
                : root.TryGetProperty("ClientError", out var ce) ? ce.GetString()?.Replace(ClientLogs.LineBreak, "\n", StringComparison.Ordinal) : null;
            var properties = new Dictionary<string, string>();
            foreach (var property in root.EnumerateObject())
            {
                if (property.Name.StartsWith('@') || Columns.Contains(property.Name))
                {
                    continue;
                }

                properties[char.ToLowerInvariant(property.Name[0]) + property.Name[1..]] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
            }

            return new LogEntry(id, timestamp, level, area, source, root.TryGetProperty("@m", out var m) ? m.GetString() ?? "" : "", exception, properties);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
