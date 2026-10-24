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

    public static string PathFor(string dataDirectory) => Path.GetFullPath(Path.Combine(dataDirectory, FileName));

    public void PostConfigure(string? name, FlexarrOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var path = PathFor(options.DataDirectory);

        // Preserve anything else the user has put in the file.
        var config = File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? [] : [];
        options.ApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        config["apiKey"] = options.ApiKey;
        File.WriteAllText(path, config.ToJsonString(JsonOptions));
        LogGeneratedApiKey(path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a new API key and saved it to {Path}")]
    private partial void LogGeneratedApiKey(string path);
}
