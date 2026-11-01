using System.Text.Json.Nodes;
using Flexarr.Integrations.Arr;

namespace Flexarr.Api.Configuration;

/// <summary>
/// Binds appsettings and config.json onto <see cref="FlexarrOptions"/>, taking the <c>arr</c> connections one at a time:
/// the binder throws on a <c>kind</c> it doesn't know, which would stop the server starting. A connection of an unknown
/// kind (an app Flexarr no longer talks to, or one from a newer version) is skipped with a warning instead, and written
/// out of config.json so the warning shows once.
/// </summary>
public static partial class FlexarrConfigBinder
{
    private const string ArrKey = "arr";

    /// <summary>Settings of features that are gone, removed from config.json along with skipped connections.</summary>
    private static readonly string[] RetiredKeys = ["bookRequests"];

    /// <param name="dataDirectory">config.json's folder, to tidy it; null for configuration that can't be rewritten.</param>
    public static void Bind(IConfiguration configuration, FlexarrOptions options, ILogger logger, string? dataDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        // Everything but the connections binds as before.
        var rest = configuration.AsEnumerable(makePathsRelative: true)
            .Where(kv => kv.Value is not null && !IsArrKey(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
        new ConfigurationBuilder().AddInMemoryCollection(rest).Build().Bind(options);

        var skipped = false;
        foreach (var entry in configuration.GetSection(ArrKey).GetChildren())
        {
            if (!IsKnownKind(entry["kind"]))
            {
                LogSkipped(logger, entry["name"] ?? entry.Key, entry["kind"] ?? "(none)");
                skipped = true;
                continue;
            }

            if (entry.Get<ArrInstance>() is { } instance)
            {
                options.Arr.Add(instance);
            }
        }

        if (dataDirectory is not null && (skipped || RetiredKeys.Any(k => configuration.GetSection(k).Exists())))
        {
            Tidy(dataDirectory);
        }
    }

    /// <summary>Whether <paramref name="kind"/> names an <see cref="ArrKind"/> (by name, as the binder reads it).</summary>
    public static bool IsKnownKind(string? kind) =>
        !string.IsNullOrWhiteSpace(kind) && !char.IsAsciiDigit(kind.TrimStart()[0]) && !kind.TrimStart().StartsWith('-')
        && Enum.TryParse<ArrKind>(kind, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed);

    /// <summary>Removes connections of unknown kinds and retired settings from config.json.</summary>
    private static void Tidy(string dataDirectory)
    {
        if (!File.Exists(ServerConfigStore.PathFor(dataDirectory)))
        {
            return;
        }

        ServerConfigStore.Update(dataDirectory, config =>
        {
            foreach (var key in config.Select(p => p.Key).Where(k => RetiredKeys.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList())
            {
                config.Remove(key);
            }

            var arrKey = config.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, ArrKey, StringComparison.OrdinalIgnoreCase));
            if (arrKey is not null && config[arrKey] is JsonArray arr)
            {
                foreach (var node in arr.ToList())
                {
                    if (node is not JsonObject entry || !IsKnownKind(KindOf(entry)))
                    {
                        arr.Remove(node);
                    }
                }
            }
        });
    }

    private static string? KindOf(JsonObject entry) =>
        entry.FirstOrDefault(p => string.Equals(p.Key, "kind", StringComparison.OrdinalIgnoreCase)).Value is JsonValue v
        && v.TryGetValue<string>(out var kind) ? kind : null;

    private static bool IsArrKey(string key) =>
        string.Equals(key, ArrKey, StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(ArrKey + ConfigurationPath.KeyDelimiter, StringComparison.OrdinalIgnoreCase);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Skipped the connection {Name} in the settings: Flexarr doesn't connect to {Kind}")]
    private static partial void LogSkipped(ILogger logger, string name, string kind);
}
