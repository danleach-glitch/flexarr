using Flexarr.Core.Scanning;

namespace Flexarr.Integrations.Arr.Webhooks;

/// <summary>Turns *arr webhook events into library scan requests.</summary>
public static class ArrWebhookTranslator
{
    private static readonly HashSet<string> ScanEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Download",          // import / upgrade
        "Rename",
        "SeriesAdd",
        "SeriesDelete",
        "EpisodeFileDelete",
        "MovieAdded",
        "MovieDelete",
        "MovieFileDelete",
        "AlbumDownload",     // Lidarr import
        "ArtistAdd",
        "ArtistDelete",
        "AlbumDelete",
        "TrackRetag",
    };

    public static bool IsTest(ArrWebhookPayload payload) =>
        string.Equals(payload.EventType, "Test", StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<LibraryScanRequest> ToScanRequests(ArrKind source, ArrWebhookPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        if (!ScanEvents.Contains(payload.EventType))
        {
            return [];
        }

        var reason = $"{source} {payload.EventType}";
        var paths = source switch
        {
            // Scan the media root folder: it covers the imported file and any removed/renamed siblings.
            ArrKind.Sonarr => [payload.Series?.Path],
            ArrKind.Radarr => [payload.Movie?.FolderPath],
            ArrKind.Lidarr => [payload.Artist?.Path],
            _ => Array.Empty<string?>(),
        };

        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.Ordinal)
            .Select(p => new LibraryScanRequest(p!, reason))
            .ToList();
    }
}
