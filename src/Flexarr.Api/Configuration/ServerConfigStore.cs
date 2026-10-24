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

            WritePrivately(path, config.ToJsonString(JsonOptions));
        }
    }

    /// <summary>
    /// Writes a file only this user can read (it holds keys), via a temporary file and a rename so a reader never sees
    /// it half-written.
    /// </summary>
    public static void WritePrivately(string path, string contents)
    {
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temp, new System.Text.UTF8Encoding(false), options))
        {
            writer.Write(contents);
        }

        // Create mode only applies to new files: tighten a temporary file left by an interrupted write too.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        File.Move(temp, path, overwrite: true);
    }

    public void PostConfigure(string? name, FlexarrOptions options)
    {
        var path = PathFor(options.DataDirectory);
        KeepPrivate(path);

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            Directory.CreateDirectory(options.DataDirectory);
            var key = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            options.ApiKey = key;
            Update(options.DataDirectory, config => config["apiKey"] = key);
            LogGeneratedApiKey(path);
        }

        // Sonarr and Radarr webhooks get their own secret, which can only start scans, rather than the API key.
        if (string.IsNullOrWhiteSpace(options.WebhookSecret))
        {
            Directory.CreateDirectory(options.DataDirectory);
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            options.WebhookSecret = secret;
            Update(options.DataDirectory, config => config["webhookSecret"] = secret);
        }
    }

    /// <summary>Earlier versions wrote config.json readable by everyone on the machine; make it this user's alone.</summary>
    private static void KeepPrivate(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return;
        }

        const UnixFileMode others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        try
        {
            if ((File.GetUnixFileMode(path) & others) != 0)
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not ours to change (a read-only mount, say); carry on.
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Generated a new API key and saved it to {Path}")]
    private partial void LogGeneratedApiKey(string path);
}
