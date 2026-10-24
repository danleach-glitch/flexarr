using Flexarr.Core.Libraries;
using System.Globalization;
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

/// <summary>Relative URLs of available artwork (null when there is none, so clients can show a placeholder).</summary>
public sealed record ArtworkDto(string? Poster, string? Backdrop, string? Logo, string? Thumb);

public sealed record ItemSummaryDto(
    Guid Id,
    Guid LibraryId,
    Guid? ParentId,
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
    DateTime AddedAt,
    ArtworkDto Artwork,
    string? AccentColor,
    double? CommunityRating,
    int? CriticRating,
    string? ContentRating,
    int? RuntimeMinutes,
    DateOnly? ReleaseDate,
    UserDataDto UserData,
    string? SeriesTitle);

/// <summary>The viewer's state for an item. UnwatchedCount is set for series and seasons.</summary>
public sealed record UserDataDto(double PositionSeconds, bool Watched, int PlayCount, DateTime? LastPlayedAt, int? UnwatchedCount);

public sealed record PagedDto<T>(int Total, IReadOnlyList<T> Items);

public sealed record StreamDto(int Index, StreamType Type, string Codec, string? Profile, string? Language, string? Title,
    bool IsDefault, bool IsForced, int? Width, int? Height, int? Channels);

public sealed record FileDto(Guid Id, string Path, long Size, string? Container, double? DurationSeconds, long? Bitrate,
    string? VideoCodec, int? Width, int? Height, VideoRange VideoRange, string? AudioCodec, int? AudioChannels,
    string? ProbeError, IReadOnlyList<StreamDto> Streams);

public sealed record CreditDto(Guid PersonId, string Name, CreditRole Role, string? Character, string? Image);

public sealed record ItemDetailDto(
    ItemSummaryDto Item,
    string? Path,
    string? OriginalTitle,
    string? Overview,
    string? Studio,
    string? Status,
    string? TrailerYouTubeId,
    IReadOnlyList<string> Genres,
    string? TvdbId,
    string? TmdbId,
    string? ImdbId,
    IReadOnlyList<ItemSummaryDto> Ancestors,
    IReadOnlyList<ItemSummaryDto> Children,
    IReadOnlyList<CreditDto> Credits,
    IReadOnlyList<FileDto> Files,
    IReadOnlyDictionary<Guid, string?> ChildOverviews);

public sealed record PersonCreditDto(ItemSummaryDto Item, CreditRole Role, string? Character);

public sealed record PersonDto(Guid Id, string Name, string? Image, IReadOnlyList<PersonCreditDto> Credits);

public static class ArtworkUrls
{
    public static ArtworkDto For(Guid id, long version, bool poster, bool backdrop, bool logo, bool thumb)
    {
        string? Url(bool has, string kind) => has ? $"/api/v1/images/items/{id}/{kind}?v={version}" : null;
        return new ArtworkDto(Url(poster, "poster"), Url(backdrop, "backdrop"), Url(logo, "logo"), Url(thumb, "thumb"));
    }

    public static string? Person(Guid id, bool has) => has ? $"/api/v1/images/people/{id}" : null;
}

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
