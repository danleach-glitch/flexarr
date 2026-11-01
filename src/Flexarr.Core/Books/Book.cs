using Flexarr.Core.Media;

namespace Flexarr.Core.Books;

/// <summary>
/// What an audiobook's edition says about it, beside its <see cref="MediaItem"/> (kind <see cref="MediaKind.Audiobook"/>).
/// One row per book. Filled from the files first (sidecars, then tags), then the *arr, then online providers: local
/// always wins, and nothing is ever written back to the files.
/// </summary>
public sealed class BookDetails
{
    public Guid MediaItemId { get; set; }
    public MediaItem? MediaItem { get; set; }

    public string? Subtitle { get; set; }

    /// <summary>Audible's id, e.g. B0DPXK6Y7B. Matches a book across re-encodes and renames, and looks it up online.</summary>
    public string? Asin { get; set; }

    public string? Isbn { get; set; }
    public string? Publisher { get; set; }

    /// <summary>The language it's read in, as written in its tags or sidecar ("English", "en", "eng").</summary>
    public string? Language { get; set; }

    public bool Abridged { get; set; }
    public bool Explicit { get; set; }

    /// <summary>The whole book's length: its files' durations added up.</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Where <see cref="MediaChapter"/>s came from last. Edited chapters are kept over rescans.</summary>
    public ChapterSource ChapterSource { get; set; }
}

/// <summary>Where a book's chapters came from.</summary>
public enum ChapterSource
{
    /// <summary>Not worked out yet.</summary>
    None,

    /// <summary>Chapters inside the files (an m4b's chapter list).</summary>
    Embedded,

    /// <summary>A .cue sheet beside the files.</summary>
    Cue,

    /// <summary>One chapter per file, named by its title tag or file name.</summary>
    Files,

    /// <summary>Evenly spaced "Part n" markers on a long file with nothing else.</summary>
    Even,

    /// <summary>Audible's chapters, looked up by ASIN.</summary>
    Audible,

    /// <summary>Changed by hand (renamed, shifted or replaced). A rescan keeps them.</summary>
    Edited,
}

/// <summary>A chapter of a book, on the whole book's timeline (not a file's).</summary>
public sealed class MediaChapter
{
    public int Id { get; set; }
    public Guid MediaItemId { get; set; }
    public int Index { get; set; }
    public required string Title { get; set; }
    public double StartSeconds { get; set; }
    public double EndSeconds { get; set; }
}

/// <summary>A series of books in one audiobooks library, e.g. "Thursday Murder Club".</summary>
public sealed class BookSeries
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LibraryId { get; set; }
    public required string Name { get; set; }
    public required string SortName { get; set; }
    public string? Description { get; set; }

    /// <summary>The series' id in the *arr or an online provider, as text.</summary>
    public string? ExternalId { get; set; }

    public List<BookSeriesEntry> Entries { get; set; } = [];
}

/// <summary>
/// A book's place in a series. A book can be in several (a sub-series and its universe).
/// </summary>
public sealed class BookSeriesEntry
{
    public Guid SeriesId { get; set; }
    public BookSeries? Series { get; set; }
    public Guid BookId { get; set; }
    public MediaItem? Book { get; set; }

    /// <summary>Its number as written: "3", "1.5", "0", "1-3". Null when the series gives none.</summary>
    public string? Sequence { get; set; }

    /// <summary><see cref="Sequence"/> as a number to sort by (the start of a range); null sorts last.</summary>
    public double? SortOrder { get; set; }
}

/// <summary>A place in a book someone saved, with an optional note.</summary>
public sealed class Bookmark
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string ProfileId { get; set; } = PlaybackState.DefaultUser;
    public Guid MediaItemId { get; set; }
    public double PositionSeconds { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
