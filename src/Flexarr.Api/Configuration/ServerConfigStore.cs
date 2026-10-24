using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Configuration;

/// <summary>Fills in settings that must survive restarts (like the API key) from &lt;DataDirectory&gt;/config.json.</summary>
public sealed partial class ServerConfigStore(ILogger<ServerConfigStore> logger) : IPostConfigureOptions<FlexarrOptions>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private sealed record PersistedConfig(string ApiKey);

    public void PostConfigure(string? name, FlexarrOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var path = Path.Combine(options.DataDirectory, "config.json");

        if (File.Exists(path))
        {
            var existing = JsonSerializer.Deserialize<PersistedConfig>(File.ReadAllText(path), JsonOptions);
            if (!string.IsNullOrWhiteSpace(existing?.ApiKey))
            {
                options.ApiKey = existing.ApiKey;
                return;
            }
        }

        options.ApiKey = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(path, JsonSerializer.Serialize(new PersistedConfig(options.ApiKey), JsonOptions));
        LogGeneratedApiKey(path);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a new API key and saved it to {Path}")]
    private partial void LogGeneratedApiKey(string path);
}
