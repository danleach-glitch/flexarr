namespace Flexarr.Integrations.Arr.Webhooks;

/// <summary>
/// The subset of the Sonarr/Radarr/Lidarr "Webhook" connection payload Flexarr cares about.
/// All three apps share the <c>eventType</c> discriminator; the media sections differ per app.
/// </summary>
public sealed record ArrWebhookPayload
{
    public string EventType { get; init; } = string.Empty;
    public string? InstanceName { get; init; }

    /// <summary>Why a file was deleted (Sonarr/Radarr *FileDelete): e.g. <c>upgrade</c>, <c>manual</c>, <c>missingFromDisk</c>.</summary>
    public string? DeleteReason { get; init; }

    // Sonarr
    public PathItem? Series { get; init; }
    public FileItem? EpisodeFile { get; init; }
    public IReadOnlyList<FileItem>? EpisodeFiles { get; init; }

    // Radarr
    public MovieItem? Movie { get; init; }
    public FileItem? MovieFile { get; init; }

    // Lidarr
    public PathItem? Artist { get; init; }
    public IReadOnlyList<FileItem>? TrackFiles { get; init; }

    public sealed record PathItem(string? Title, string? Path);

    public sealed record MovieItem(string? Title, string? FolderPath);

    public sealed record FileItem(string? Path, string? RelativePath);
}
