using System.Globalization;
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
    ScanStatusDto? Scan,
    // Everything in it is fine for profiles with an age limit, rated or not. Absent from servers before 0.56.
    bool FineForKids = false);

/// <param name="FineForKids">Everything in it is fine for profiles with an age limit; null leaves it as it is.</param>
public sealed record LibraryRequest(string Name, LibraryType Type, IReadOnlyList<string> Folders, bool? FineForKids = null);

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
    string? SeriesTitle,
    int? DiscNumber,
    int? TrackNumber,
    Guid? SeriesId = null,
    string? AlbumType = null,
    string? TrackArtist = null,
    bool HomeVideo = false,
    // A book's authors, narrator, series and length. Absent from servers before 0.58.
    Books.BookSummaryDto? Book = null);

/// <summary>The viewer's state for an item. UnwatchedCount is set for series and seasons.</summary>
public sealed record UserDataDto(double PositionSeconds, bool Watched, int PlayCount, DateTime? LastPlayedAt, int? UnwatchedCount);

public sealed record PagedDto<T>(int Total, IReadOnlyList<T> Items);

public sealed record StreamDto(int Index, StreamType Type, string Codec, string? Profile, string? Language, string? Title,
    bool IsDefault, bool IsForced, int? Width, int? Height, int? Channels);

public sealed record FileDto(Guid Id, string Path, long Size, string? Container, double? DurationSeconds, long? Bitrate,
    string? VideoCodec, int? Width, int? Height, VideoRange VideoRange, string? AudioCodec, int? AudioChannels,
    string? ProbeError, IReadOnlyList<StreamDto> Streams, bool Hdr10Plus);

public sealed record CreditDto(Guid PersonId, string Name, CreditRole Role, string? Character, string? Image);

/// <param name="OnWatchlist">For a film or show, whether it's on the profile's watchlist (absent from servers before 0.56).</param>
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
    IReadOnlyDictionary<Guid, string?> ChildOverviews,
    bool MatchLocked,
    ItemSummaryDto? NextUp = null,
    ItemDetailDto? Season = null,
    bool CanSearchSubtitles = false,
    bool? OnWatchlist = null,
    // The age it's suitable from, on the UK's steps (0 is U, 8 PG); null, unrated. Absent from servers before 0.56.
    int? RatingAge = null,
    // A parent's Allow or Block for profiles with an age limit; only for the main profile. Absent from servers before 0.56.
    KidsOverride? KidsOverride = null,
    // A book's edition, series, chapters and bookmarks. Absent from servers before 0.58.
    Books.BookDetailDto? Book = null);

/// <summary>A parent's say over a title for profiles with an age limit.</summary>
public sealed record KidsOverrideRequest(KidsOverride Override);

public sealed record PersonCreditDto(ItemSummaryDto Item, CreditRole Role, string? Character);

public sealed record PersonDto(Guid Id, string Name, string? Image, IReadOnlyList<PersonCreditDto> Credits,
    // An author's or narrator's biography. Absent from servers before 0.58.
    string? Biography = null);

public static class ArtworkUrls
{
    public static ArtworkDto For(Guid id, long version, bool poster, bool backdrop, bool logo, bool thumb)
    {
        string? Url(bool has, string kind) => has ? $"/api/v1/images/items/{id}/{kind}?v={version}" : null;
        return new ArtworkDto(Url(poster, "poster"), Url(backdrop, "backdrop"), Url(logo, "logo"), Url(thumb, "thumb"));
    }

    /// <summary>
    /// The ?v= of a title's artwork: the later of when it was last looked up and when the scan saw its local artwork
    /// change, in seconds. Called in a query's final projection, so it runs on the rows read.
    /// </summary>
    public static long Version(DateTime? metadataUpdatedAt, DateTime? artworkChangedAt)
    {
        var latest = metadataUpdatedAt is null || artworkChangedAt > metadataUpdatedAt ? artworkChangedAt : metadataUpdatedAt;
        return (latest?.Ticks ?? 0) / 10_000_000;
    }

    /// <summary>A person's photo, at a ?v= of when it last changed (none for one from before that was kept).</summary>
    public static string? Person(Guid id, bool has, DateTime? changedAt) =>
        !has ? null : changedAt is { } changed ? $"/api/v1/images/people/{id}?v={PersonVersion(changed)}" : $"/api/v1/images/people/{id}";

    /// <summary>The ?v= of a person's photo: when it last changed, in seconds.</summary>
    public static string PersonVersion(DateTime changedAt) => (changedAt.Ticks / 10_000_000).ToString(System.Globalization.CultureInfo.InvariantCulture);
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
