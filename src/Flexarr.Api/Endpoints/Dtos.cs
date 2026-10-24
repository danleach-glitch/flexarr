using Flexarr.Core.Libraries;
using Flexarr.Core.Media;

namespace Flexarr.Api.Endpoints;

public sealed record ScanStatusDto(string Path, int Total, int Processed);

public sealed record LibraryDto(
    Guid Id,
    string Name,
    LibraryType Type,
    IReadOnlyList<string> Folders,
    int ItemCount,
    DateTime? LastScannedAt,
    ScanStatusDto? Scan);

public sealed record LibraryRequest(string Name, LibraryType Type, IReadOnlyList<string> Folders);

public sealed record ItemSummaryDto(
    Guid Id,
    Guid LibraryId,
    MediaKind Kind,
    string Title,
    int? Year,
    int? SeasonNumber,
    int? EpisodeNumber,
    int? EpisodeNumberEnd,
    DateOnly? AirDate,
    int ChildCount,
    double? DurationSeconds,
    string? Resolution,
    DateTime AddedAt);

public sealed record PagedDto<T>(int Total, IReadOnlyList<T> Items);

public sealed record StreamDto(int Index, StreamType Type, string Codec, string? Profile, string? Language, string? Title,
    bool IsDefault, bool IsForced, int? Width, int? Height, int? Channels);

public sealed record FileDto(Guid Id, string Path, long Size, string? Container, double? DurationSeconds, long? Bitrate,
    string? VideoCodec, int? Width, int? Height, VideoRange VideoRange, string? AudioCodec, int? AudioChannels,
    string? ProbeError, IReadOnlyList<StreamDto> Streams);

public sealed record ItemDetailDto(
    ItemSummaryDto Item,
    string? Path,
    string? TvdbId,
    string? TmdbId,
    string? ImdbId,
    IReadOnlyList<ItemSummaryDto> Ancestors,
    IReadOnlyList<ItemSummaryDto> Children,
    IReadOnlyList<FileDto> Files);

public static class Resolution
{
    public static string? Label(int? width, int? height) => (width, height) switch
    {
        (null, null) => null,
        ( >= 3200, _) or (_, >= 1800) => "4K",
        ( >= 1800, _) or (_, >= 1000) => "1080p",
        ( >= 1200, _) or (_, >= 700) => "720p",
        _ => "SD",
    };
}
