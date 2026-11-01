namespace Flexarr.Integrations.Arr;

public enum ArrKind
{
    Sonarr,
    Radarr,
    Lidarr,
}

/// <summary>A configured *arr instance Flexarr can talk to.</summary>
public sealed record ArrInstance
{
    public required string Name { get; init; }
    public required ArrKind Kind { get; init; }
    public required Uri BaseUrl { get; init; }
    public required string ApiKey { get; init; }

    /// <summary>Sonarr and Radarr expose /api/v3; Lidarr /api/v1.</summary>
    public string ApiVersion => Kind is ArrKind.Lidarr ? "v1" : "v3";
}
