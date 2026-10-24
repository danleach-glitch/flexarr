using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Configuration;

/// <summary>
/// Owns &lt;DataDirectory&gt;/config.json: user settings that must survive upgrades (API key, path mappings, …).
/// Values there override appsettings.json. A missing API key is generated and written back on first run.
/// </summary>
public sealed partial class ServerConfigStore(ILogger<ServerConfigStore> logger) : IPostConfigureOptions<FlexarrOptions>
{
    public const string FileName = "config.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private static readonly Lock WriteLock = new();

    public static string PathFor(string dataDirectory) => Path.GetFullPath(Path.Combine(dataDirectory, FileName));

    /// <summary>Applies <paramref name="change"/> to config.json, keeping every setting it doesn't touch.</summary>
    public static void Update(string dataDirectory, Action<JsonObject> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        lock (WriteLock)
        {
            var path = PathFor(dataDirectory);
            var config = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];
            change(config);

            // Write then rename, so the reloading configuration never reads a half-written file.
            var temp = path + ".tmp";
            File.WriteAllText(temp, config.ToJsonString(JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
    }

    public void PostConfigure(string? name, FlexarrOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var path = PathFor(options.DataDirectory);

        var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        options.ApiKey = key;
        Update(options.DataDirectory, config => config["apiKey"] = key);
        LogGeneratedApiKey(path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a new API key and saved it to {Path}")]
    private partial void LogGeneratedApiKey(string path);
}
