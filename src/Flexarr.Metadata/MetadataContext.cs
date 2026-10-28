using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;

namespace Flexarr.Metadata;

/// <summary>Server settings the metadata layer needs, supplied by the host.</summary>
public interface IMetadataSettings
{
    IReadOnlyList<ArrInstance> ArrInstances { get; }
    IReadOnlyList<PathMapping> PathMappings { get; }
    string CacheDirectory { get; }
    string FfmpegPath { get; }

    /// <summary>The TMDB key the server's owner pasted into Settings, for films Radarr doesn't have; null without one.</summary>
    string? TmdbApiKey => null;
}
