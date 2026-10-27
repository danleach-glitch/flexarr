namespace Flexarr.Api.Logging;

/// <summary>A part of the server whose detail can be turned up or down on its own, made of the loggers under its prefixes.</summary>
public sealed record LogArea(string Key, string Name, IReadOnlyList<string> Prefixes);

public static class LogAreas
{
    public const string General = "general";
    public const string Requests = "requests";
    public const string Clients = "clients";

    /// <summary>The logger device and browser reports are written under.</summary>
    public const string ClientSource = "Flexarr.Client";

    public static readonly IReadOnlyList<LogArea> All =
    [
        new(General, "General", []),
        new("playback", "Playback", ["Flexarr.Streaming", "Flexarr.Api.Playback", "Flexarr.Api.Downloads"]),
        new("library", "Library scans", ["Flexarr.Core.Scanning", "Flexarr.Core.Probing", "Flexarr.Core.Libraries", "Flexarr.Api.Scanning", "Flexarr.Api.Import"]),
        new("integrations", "Sonarr, Radarr and Seerr", ["Flexarr.Integrations", "System.Net.Http.HttpClient.ArrClient", "System.Net.Http.HttpClient.SeerrClient"]),
        new("metadata", "Metadata and artwork", ["Flexarr.Metadata"]),
        new("livetv", "Live TV", ["Flexarr.Api.LiveTv", "Flexarr.Core.LiveTv", "Flexarr.Streaming.Live"]),
        new("podcasts", "Podcasts", ["Flexarr.Api.Podcasts", "Flexarr.Core.Podcasts"]),
        new("notifications", "Notifications", ["Flexarr.Api.Notifications", "Flexarr.Core.Notifications"]),
        new(Requests, "Web requests", ["Serilog.AspNetCore.RequestLoggingMiddleware"]),
        new(Clients, "Apps and browsers", [ClientSource]),
    ];

    private static readonly (string Prefix, string Key)[] ByPrefix =
        [.. All.SelectMany(a => a.Prefixes.Select(p => (p, a.Key))).OrderByDescending(p => p.p.Length)];

    public static LogArea? Find(string key) => All.FirstOrDefault(a => a.Key.Equals(key, StringComparison.OrdinalIgnoreCase));

    /// <summary>The area a logger belongs to: its longest matching prefix, so Live TV's streaming isn't Playback.</summary>
    public static string Of(string? source)
    {
        if (source is null)
        {
            return General;
        }

        foreach (var (prefix, key) in ByPrefix)
        {
            if (source.StartsWith(prefix, StringComparison.Ordinal) && (source.Length == prefix.Length || source[prefix.Length] == '.'))
            {
                return key;
            }
        }

        return General;
    }
}
