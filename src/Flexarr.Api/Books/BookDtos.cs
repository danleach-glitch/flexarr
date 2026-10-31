using Flexarr.Api.Endpoints;
using Flexarr.Core.Books;

namespace Flexarr.Api.Books;

/// <summary>
/// What a list shows about a book (on <see cref="ItemSummaryDto.Book"/>). Absent from servers before 0.58.
/// </summary>
/// <param name="Authors">Up to two authors' names, "Lee Child, Andrew Child".</param>
/// <param name="AuthorCount">How many authors it has in all, for "and 2 more".</param>
/// <param name="Narrator">Its first narrator.</param>
/// <param name="Series">A series it's in, and <paramref name="Sequence"/> its number there ("3", "1.5").</param>
/// <param name="FinishedAt">When this profile last finished it.</param>
public sealed record BookSummaryDto(string? Authors, int AuthorCount, string? Narrator, string? Series, string? Sequence, double DurationSeconds, DateTime? FinishedAt);

public sealed record BookChapterDto(int Index, string Title, double StartSeconds, double EndSeconds);

public sealed record BookmarkDto(Guid Id, double PositionSeconds, string? Note, DateTime CreatedAt);

/// <summary>A series a book is in, with its number there and how many books the library has of it.</summary>
public sealed record BookSeriesRefDto(Guid Id, string Name, string? Sequence, int BookCount);

/// <summary>A book's own page (on <see cref="ItemDetailDto.Book"/>). Absent from servers before 0.58.</summary>
/// <param name="Speed">This profile's speed for the book; null, its default (<see cref="BookListeningDto.Speed"/>).</param>
public sealed record BookDetailDto(
    string? Subtitle,
    string? Asin,
    string? Isbn,
    string? Publisher,
    string? Language,
    bool Abridged,
    bool Explicit,
    double DurationSeconds,
    ChapterSource ChapterSource,
    DateTime? FinishedAt,
    double? Speed,
    IReadOnlyList<BookSeriesRefDto> Series,
    IReadOnlyList<BookChapterDto> Chapters,
    IReadOnlyList<BookmarkDto> Bookmarks,
    IReadOnlyList<ItemSummaryDto> MoreInSeries,
    IReadOnlyList<ItemSummaryDto> ByAuthor,
    IReadOnlyList<ItemSummaryDto> ReadByNarrator);

/// <summary>
/// One of a book's files on its timeline. <paramref name="Url"/> serves it as it is when the player said it can play
/// its sound, and converted to AAC (<paramref name="Converted"/>) when it can't.
/// </summary>
public sealed record BookFileDto(int Index, Guid FileId, double OffsetSeconds, double DurationSeconds, string? Container, string? AudioCodec, long Size, string Url, bool Converted);

/// <summary>Where this profile is in a book.</summary>
/// <param name="PositionSeconds">On the whole book's timeline.</param>
/// <param name="UpdatedAt">When a device recorded it.</param>
/// <param name="Device">The device that recorded it, so another can offer "Jump to where you were on the iPhone".</param>
public sealed record BookProgressDto(double PositionSeconds, bool Finished, DateTime? FinishedAt, DateTime? UpdatedAt, string? Device);

/// <summary>How this profile listens to books.</summary>
/// <param name="Speed">The default speed, for books without their own.</param>
/// <param name="SmartRewind">After a pause, start a little earlier: more the longer the pause.</param>
public sealed record BookListeningDto(double Speed, int SkipBackSeconds, int SkipForwardSeconds, bool SmartRewind);

/// <summary>Each field left out keeps what the profile has.</summary>
public sealed record BookListeningRequest(double? Speed = null, int? SkipBackSeconds = null, int? SkipForwardSeconds = null, bool? SmartRewind = null);

/// <summary>
/// Everything a player needs to play a book: its files in order on one timeline, chapters, bookmarks, where this
/// profile is, the speed to play at, and the next book in its series to offer at the end.
/// </summary>
public sealed record BookManifestDto(
    Guid ItemId,
    double DurationSeconds,
    IReadOnlyList<BookFileDto> Files,
    IReadOnlyList<BookChapterDto> Chapters,
    IReadOnlyList<BookmarkDto> Bookmarks,
    BookProgressDto Progress,
    double Speed,
    BookListeningDto Listening,
    ItemSummaryDto? NextInSeries);

/// <summary>A place in a book a device reached.</summary>
/// <param name="PositionSeconds">On the whole book's timeline.</param>
/// <param name="Device">A name for the device ("Dan's iPhone").</param>
/// <param name="RecordedAt">When the device was there: offline listening is sent later with its own time. Left out, now.</param>
/// <param name="Ended">The player reached the end of the book.</param>
public sealed record BookProgressRequest(double PositionSeconds, string? Device = null, DateTime? RecordedAt = null, bool Ended = false);

public sealed record BookFinishedRequest(bool Finished);

public sealed record BookmarkRequest(double PositionSeconds, string? Note = null);

public sealed record BookmarkNoteRequest(string? Note);

/// <summary>A series in a library's list of them.</summary>
/// <param name="Covers">Artwork of its first few books, for a stacked cover.</param>
/// <param name="Next">The next book in it this profile hasn't finished.</param>
public sealed record BookSeriesSummaryDto(Guid Id, string Name, int BookCount, int FinishedCount, IReadOnlyList<ArtworkDto> Covers, ItemSummaryDto? Next);

/// <summary>A series in reading order.</summary>
public sealed record BookSeriesDto(Guid Id, Guid LibraryId, string Name, string? Description, IReadOnlyList<BookSeriesEntryDto> Books, Guid? NextId);

public sealed record BookSeriesEntryDto(ItemSummaryDto Book, string? Sequence);

/// <summary>An author or narrator in a library's list of them.</summary>
public sealed record BookPersonDto(Guid Id, string Name, string? Image, int BookCount);

public sealed record GenreCountDto(string Name, int Count);

/// <summary>An audiobooks library's front page.</summary>
public sealed record BookShelvesDto(
    IReadOnlyList<ItemSummaryDto> ContinueListening,
    IReadOnlyList<ItemSummaryDto> NextInSeries,
    IReadOnlyList<ItemSummaryDto> RecentlyAdded,
    IReadOnlyList<BookSeriesSummaryDto> Series,
    IReadOnlyList<BookPersonDto> Authors,
    IReadOnlyList<BookPersonDto> Narrators,
    IReadOnlyList<GenreCountDto> Genres,
    IReadOnlyList<ItemSummaryDto> Finished);

/// <summary>A series a search found, with how many of its books this profile can see.</summary>
public sealed record BookSeriesSearchDto(Guid Id, string Name, int BookCount);

/// <summary>How the server looks books up online. Keys aren't sent back: only whether there is one.</summary>
public sealed record BookMetadataSettingsDto(string Region, IReadOnlyList<string> Providers, bool HasGoogleBooksKey, bool HasHardcoverToken,
    IReadOnlyList<string> Regions, IReadOnlyList<string> AllProviders);

/// <summary>Each field left out keeps what's set; an empty key removes it.</summary>
public sealed record BookMetadataSettingsRequest(string? Region = null, IReadOnlyList<string>? Providers = null, string? GoogleBooksKey = null, string? HardcoverToken = null);
