using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Integrations.Arr;

namespace Flexarr.Api.Configuration;

/// <summary>
/// Settings of things Flexarr no longer does. The binder already leaves out a connection whose <c>kind</c> it can't
/// read (Readarr, Bookshelf and Chaptarr, from dev builds before 0.58), so the server starts; this says so in the log,
/// once for each, and takes retired connections and settings out of config.json. Any other kind it can't read (a typo,
/// one from a newer Flexarr) is only warned about, never deleted: it may be someone's connection, with its key.
/// </summary>
public static partial class RetiredSettings
{
    private const string ArrKey = "arr";

    /// <summary>Kinds of connection that are gone for good, taken out of config.json.</summary>
    private static readonly string[] RetiredKinds = ["Readarr"];

    /// <summary>Settings of features that are gone, taken out of config.json.</summary>
    private static readonly string[] RetiredKeys = ["bookRequests"];

    /// <summary>Connections already warned about, so a reload (every settings save) doesn't warn again.</summary>
    private static readonly ConcurrentDictionary<string, byte> Warned = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="sources">appsettings' Flexarr section and config.json, as they're bound.</param>
    /// <param name="dataDirectory">config.json's folder.</param>
    public static void Check(IEnumerable<IConfiguration> sources, string dataDirectory, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(logger);
        foreach (var source in sources)
        {
            foreach (var entry in source.GetSection(ArrKey).GetChildren())
            {
                var kind = entry["kind"];
                if (kind is not null && !Readable(kind) && Warned.TryAdd($"{entry["name"] ?? entry.Key}\n{kind}", 0))
                {
                    LogSkipped(logger, entry["name"] ?? entry.Key, kind);
                }
            }
        }

        try
        {
            Tidy(dataDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Left in the file it does no harm: the binder leaves it out all the same.
            LogTidyFailed(logger, ServerConfigStore.PathFor(dataDirectory), ex.Message);
        }
    }

    /// <summary>
    /// Leaves out connections the binder read as a number Flexarr has no kind for (a hand-edited <c>"kind": 3</c>, which
    /// was Readarr's), as <see cref="Check"/> told the log it would.
    /// </summary>
    public static void DropUnknownKinds(FlexarrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Arr.RemoveAll(a => !Enum.IsDefined(a.Kind));
    }

    /// <summary>Whether the binder reads <paramref name="kind"/> as an <see cref="ArrKind"/>: a name or a number it has, any case.</summary>
    public static bool Readable(string kind) => Enum.TryParse<ArrKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed);

    /// <summary>Whether config.json names a retired connection or setting, so it's only rewritten when it does.</summary>
    private static bool Retired(JsonObject config) =>
        config.Any(p => RetiredKeys.Contains(p.Key, StringComparer.OrdinalIgnoreCase))
        || RetiredEntries(config).Count > 0;

    private static List<JsonObject> RetiredEntries(JsonObject config) =>
        config.FirstOrDefault(p => string.Equals(p.Key, ArrKey, StringComparison.OrdinalIgnoreCase)).Value is JsonArray arr
            ? arr.OfType<JsonObject>().Where(e => KindOf(e) is { } kind && RetiredKinds.Contains(kind, StringComparer.OrdinalIgnoreCase)).ToList()
            : [];

    private static void Tidy(string dataDirectory)
    {
        var path = ServerConfigStore.PathFor(dataDirectory);
        if (!File.Exists(path) || JsonNode.Parse(File.ReadAllText(path), documentOptions: Lenient) is not JsonObject current || !Retired(current))
        {
            return;
        }

        ServerConfigStore.Update(dataDirectory, config =>
        {
            foreach (var key in config.Select(p => p.Key).Where(k => RetiredKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                config.Remove(key);
            }

            if (config.FirstOrDefault(p => string.Equals(p.Key, ArrKey, StringComparison.OrdinalIgnoreCase)).Value is JsonArray arr)
            {
                foreach (var entry in RetiredEntries(config))
                {
                    arr.Remove(entry);
                }
            }
        });
    }

    /// <summary>Comments and trailing commas, as the config reader allows them.</summary>
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private static string? KindOf(JsonObject entry) =>
        entry.FirstOrDefault(p => string.Equals(p.Key, "kind", StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
        && v.TryGetValue<string>(out var kind) ? kind.Trim() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the connection {Name} in the settings: Flexarr doesn't connect to {Kind}")]
    private static partial void LogSkipped(ILogger logger, string name, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't take retired settings out of {Path}: {Error}")]
    private static partial void LogTidyFailed(ILogger logger, string path, string error);
}
