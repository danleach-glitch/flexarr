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
        // A temporary file left by an interrupted write keeps its own permissions if reused, so start afresh: the new
        // one is created private, before anything is written to it.
        File.Delete(temp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var writer = new StreamWriter(temp, new System.Text.UTF8Encoding(false), options))
        {
            writer.Write(contents);
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
            options.SetupOpen = true;
            Update(options.DataDirectory, config =>
            {
                config["apiKey"] = key;
                config["setupOpen"] = true;
            });
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

        if (string.IsNullOrWhiteSpace(options.ImageSecret))
        {
            Directory.CreateDirectory(options.DataDirectory);
            var secret = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            options.ImageSecret = secret;
            Update(options.DataDirectory, config => config["imageSecret"] = secret);
        }

        if (string.IsNullOrWhiteSpace(options.ServerId))
        {
            Directory.CreateDirectory(options.DataDirectory);
            var id = Guid.NewGuid().ToString("N");
            options.ServerId = id;
            Update(options.DataDirectory, config => config["serverId"] = id);
        }

        // Browsers and profile tokens from before per-device sign-ins get a month to move over, counted from now.
        if (options.LegacySignInsUntil is null)
        {
            Directory.CreateDirectory(options.DataDirectory);
            var until = DateTime.UtcNow.AddDays(30);
            options.LegacySignInsUntil = until;
            Update(options.DataDirectory, config => config["legacySignInsUntil"] = until.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
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
