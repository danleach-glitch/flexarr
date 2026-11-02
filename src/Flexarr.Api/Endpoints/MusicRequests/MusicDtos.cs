namespace Flexarr.Api.Endpoints.MusicRequests;

/// <summary>
/// An artist or album from outside the library (Deezer's, or Lidarr's for an album Deezer doesn't have), with whether
/// the household has it. Music never goes in <see cref="ExternalTitleDto"/>: older apps read its TMDB id as required.
/// </summary>
/// <param name="Kind">"artist" or "album".</param>
/// <param name="DeezerId">Deezer's id; 0 for an album only Lidarr knows (request it by <paramref name="MusicBrainzId"/>).</param>
/// <param name="Artist">An album's artist.</param>
/// <param name="RecordType">An album's kind on Deezer: "album", "ep", "single" or "compile".</param>
/// <param name="MusicBrainzId">An album's MusicBrainz release group, when Lidarr listed it.</param>
public sealed record MusicExternalDto(
    string Kind,
    long DeezerId,
    string Title,
    string? Artist,
    long? ArtistDeezerId,
    int? Year,
    string? Image,
    AvailabilityDto Availability,
    Guid? LibraryItemId,
    string? RecordType = null,
    string? MusicBrainzId = null);

/// <param name="Shape">"artists" (round photos) or "albums" (square covers).</param>
public sealed record MusicRowDto(string Id, string Title, string Shape, IReadOnlyList<MusicExternalDto> Items);

public sealed record MusicGenreDto(long Id, string Name, string? Image);

public sealed record MusicDiscoverDto(IReadOnlyList<MusicRowDto> Rows, IReadOnlyList<MusicGenreDto> Genres);

public sealed record MusicSearchDto(IReadOnlyList<MusicExternalDto> Artists, IReadOnlyList<MusicExternalDto> Albums);

public sealed record MusicGenreDetailDto(long Id, string Name, IReadOnlyList<MusicExternalDto> Artists);

public sealed record MusicTrackDto(string Title, int DurationSeconds, int Number, int Disc, bool Explicit);

/// <summary>An artist the household may not have: their releases, the artists like them, and whether Lidarr follows them.</summary>
/// <param name="Fans">Deezer's followers, as a measure of how well known they are.</param>
/// <param name="Followed">Lidarr watches for their new releases.</param>
public sealed record MusicArtistDetailDto(
    long DeezerId,
    string Name,
    string? Image,
    int Fans,
    IReadOnlyList<MusicExternalDto> Albums,
    IReadOnlyList<MusicExternalDto> Similar,
    bool Followed,
    AvailabilityDto Availability,
    Guid? LibraryItemId);

public sealed record MusicAlbumDetailDto(
    long DeezerId,
    string Title,
    string Artist,
    long? ArtistDeezerId,
    string? Image,
    int? Year,
    string? RecordType,
    IReadOnlyList<string> Genres,
    IReadOnlyList<MusicTrackDto> Tracks,
    int DurationSeconds,
    AvailabilityDto Availability,
    Guid? LibraryItemId);

/// <summary>A library artist's page, from outside: the albums of theirs it doesn't have, artists like them, and whether they're followed.</summary>
/// <param name="DeezerId">The artist on Deezer, for their outside page; null when Deezer doesn't know them.</param>
public sealed record MusicArtistExtrasDto(IReadOnlyList<MusicExternalDto> Missing, IReadOnlyList<MusicExternalDto> Similar, bool Followed, long? DeezerId);

/// <summary>Request an album (just it) or follow an artist (their new releases).</summary>
/// <param name="Kind">"album" or "artist".</param>
/// <param name="DeezerId">The album or artist on Deezer.</param>
/// <param name="MusicBrainzId">Instead of a Deezer id: an album's MusicBrainz release group (one only Lidarr lists).</param>
/// <param name="ArtistId">Instead of a Deezer id: a library artist, to follow.</param>
public sealed record MusicRequestBody(string Kind, long? DeezerId = null, string? MusicBrainzId = null, Guid? ArtistId = null);

public sealed record MusicRequestStatusDto(bool Configured, bool Reachable);

/// <summary>A music library requests can go to, for the "new music goes to" setting.</summary>
public sealed record MusicDestinationDto(Guid Id, string Name, bool IsDefault);

public sealed record RequestMusicLibraryBody(Guid? LibraryId);

/// <summary>Take back a follow (an artist, by any of the three) or a request (an album, by release group or Deezer id).</summary>
/// <param name="MusicBrainzId">An artist's MusicBrainz id, or an album's release group.</param>
/// <param name="ArtistId">A library artist, to unfollow.</param>
public sealed record MusicUndoBody(long? DeezerId = null, string? MusicBrainzId = null, Guid? ArtistId = null);

/// <summary>An album Lidarr is waiting for: asked for, monitored without files, or coming out soon.</summary>
/// <param name="MusicBrainzId">Its release group: what unrequesting takes.</param>
/// <param name="State">"upcoming" (not out yet), "searching", "downloading" or "importing".</param>
/// <param name="Progress">While downloading: how far, 0 to 1.</param>
/// <param name="ArtistLibraryItemId">The artist in the library, when they're there (a placeholder counts).</param>
public sealed record MusicRequestedDto(
    string MusicBrainzId,
    string Title,
    string Artist,
    string? ArtistMusicBrainzId,
    string? Image,
    DateOnly? ReleaseDate,
    string State,
    double? Progress,
    Guid? ArtistLibraryItemId);
