using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr;
using Flexarr.Metadata;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Configuration;

public sealed class MetadataSettings(IOptions<FlexarrOptions> options) : IMetadataSettings
{
    public IReadOnlyList<ArrInstance> ArrInstances => options.Value.Arr;
    public IReadOnlyList<PathMapping> PathMappings => options.Value.PathMappings;
    public string CacheDirectory => options.Value.ResolvedCacheDirectory;
    public string FfmpegPath => options.Value.FfmpegPath;
}
