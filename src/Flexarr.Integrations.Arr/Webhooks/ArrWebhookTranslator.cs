using Flexarr.Core.Scanning;

namespace Flexarr.Integrations.Arr.Webhooks;

/// <summary>Turns *arr webhook events into library scan requests.</summary>
public static class ArrWebhookTranslator
{
    private static readonly HashSet<string> ScanEvents = new(StringComparer.OrdinalIgnoreCase)
    {
        "Download",          // import / upgrade
        "ImportComplete",    // Sonarr: all files of a release imported
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

        // The old file of an upgrade: the import that follows sends its own event once the new file is in place.
        // Scanning now could catch the folder with neither file.
        if (payload.EventType.EndsWith("FileDelete", StringComparison.OrdinalIgnoreCase)
            && string.Equals(payload.DeleteReason, "upgrade", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var reason = $"{source} {payload.EventType}";
        var deleted = payload.EventType.EndsWith("Delete", StringComparison.OrdinalIgnoreCase);
        var paths = source switch
        {
            // Scan the media root folder: it covers the imported file and any removed/renamed siblings.
            ArrKind.Sonarr => [payload.Series?.Path],
            ArrKind.Radarr => [payload.Movie?.FolderPath],
            ArrKind.Lidarr => [payload.Artist?.Path],
            _ => Array.Empty<string?>(),
        };

        // A title just added has no ids in Flexarr yet; Radarr's or Sonarr's let its metadata be fetched on its own.
        var tmdbId = source == ArrKind.Radarr && payload.Movie?.TmdbId > 0 ? payload.Movie.TmdbId : null;
        var tvdbId = source == ArrKind.Sonarr && payload.Series?.TvdbId > 0 ? payload.Series.TvdbId : null;
        return paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Normalize(System.Text.NormalizationForm.FormC))
            .Distinct(StringComparer.Ordinal)
            .Select(p => new LibraryScanRequest(p!, reason, Deleted: deleted, TmdbId: tmdbId, TvdbId: tvdbId))
            .ToList();
    }
}
