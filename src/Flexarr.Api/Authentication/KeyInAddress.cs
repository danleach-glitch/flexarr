using System.Collections.Concurrent;
using System.Text.Json;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Authentication;

/// <summary>Where the API key (or a device's token) was last sent in an address, for Settings › API key.</summary>
/// <param name="LastUsed">When, in UTC.</param>
/// <param name="Path">The address's path, never its query (which held the key).</param>
public sealed record KeyInAddressDto(DateTime LastUsed, string Path);

/// <summary>
/// Requests signed in with the key in the address (<c>?apikey=</c>) rather than the <c>X-Api-Key</c> header. That form
/// still works for now but will stop in a coming release, so each use is logged as a warning (at most once an hour for
/// each path and kind of key, and never with the key) and the last one is kept in &lt;DataDirectory&gt;/key-in-address.json
/// for Settings to show for 30 days.
/// </summary>
public sealed partial class KeyInAddress(IOptionsMonitor<FlexarrOptions> options, TimeProvider clock, ILogger<KeyInAddress> logger)
{
    public const string FileName = "key-in-address.json";

    /// <summary>How long Settings keeps mentioning the last use.</summary>
    public static readonly TimeSpan ShownFor = TimeSpan.FromDays(30);

    /// <summary>How often the same path and kind of key is logged (and the last use written down).</summary>
    public static readonly TimeSpan WarnEvery = TimeSpan.FromHours(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<(string Path, string Kind), DateTimeOffset> _warned = new();
    private readonly Lock _lock = new();
    private KeyInAddressDto? _last;
    private bool _loaded;

    private string FilePath => Path.Combine(options.CurrentValue.DataDirectory, FileName);

    /// <summary>A request just signed in with the key in its address.</summary>
    /// <param name="kind">The sign-in method: the server's API key or a device's token.</param>
    public void Note(PathString path, string kind)
    {
        var now = clock.GetUtcNow();
        var shown = Shorten(path.HasValue ? path.Value! : "/");
        lock (_lock)
        {
            Load();
            _last = new KeyInAddressDto(now.UtcDateTime, shown);
        }

        var key = (shown, kind);
        if (_warned.TryGetValue(key, out var at) && now - at < WarnEvery)
        {
            return;
        }

        _warned[key] = now;
        if (kind == ApiKeyAuthenticationHandler.Methods.Device)
        {
            LogDeviceTokenInAddress(logger, shown);
        }
        else
        {
            LogKeyInAddress(logger, shown);
        }

        Save();
    }

    /// <summary>The last use, while it's recent enough to mention; null when there's been none for 30 days.</summary>
    public KeyInAddressDto? Recent()
    {
        lock (_lock)
        {
            Load();
            return _last is { } last && clock.GetUtcNow().UtcDateTime - last.LastUsed < ShownFor ? last : null;
        }
    }

    /// <summary>Long paths are cut short: they're for recognising the app that sent them, not for keeping.</summary>
    private static string Shorten(string path) => path.Length <= 200 ? path : path[..200];

    private void Load()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(FilePath))
            {
                _last = JsonSerializer.Deserialize<KeyInAddressDto>(File.ReadAllText(FilePath), Json);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            _last = null;
        }
    }

    private void Save()
    {
        string text;
        lock (_lock)
        {
            if (_last is null)
            {
                return;
            }

            text = JsonSerializer.Serialize(_last, Json);
        }

        try
        {
            Directory.CreateDirectory(options.CurrentValue.DataDirectory);
            File.WriteAllText(FilePath, text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only a reminder: the warning in the log still says it.
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "The API key was sent in an address (?apikey=) for {Path}. That will stop working in a coming release; send it as the X-Api-Key header instead (Sonarr, Radarr and Lidarr webhooks: use the webhook secret from Settings › Connections).")]
    private static partial void LogKeyInAddress(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "A device's sign-in was sent in an address (?apikey=) for {Path}. That will stop working in a coming release; the app sending it needs updating to send the X-Api-Key header.")]
    private static partial void LogDeviceTokenInAddress(ILogger logger, string path);
}
