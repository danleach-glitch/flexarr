using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Configuration;

public sealed class MetadataSettings(IOptionsMonitor<FlexarrOptions> options) : IMetadataSettings
{
    public IReadOnlyList<ArrInstance> ArrInstances => options.CurrentValue.Arr;
    public IReadOnlyList<PathMapping> PathMappings => options.CurrentValue.PathMappings;
    public string CacheDirectory => options.CurrentValue.ResolvedCacheDirectory;
    public string FfmpegPath => options.CurrentValue.FfmpegPath;
    public string? TmdbApiKey => options.CurrentValue.Tmdb?.ApiKey is { Length: > 0 } key ? key : null;
    public string? Country => options.CurrentValue.Podcasts.ResolvedCountry.ToUpperInvariant();

    public Flexarr.Metadata.Books.BookMetadataSettings Books => options.CurrentValue.BookMetadata.Resolved(options.CurrentValue.Podcasts.ResolvedCountry);
}
