namespace Flexarr.Core.Libraries;

public enum LibraryType
{
    Movies,
    TvShows,
    Music,

    /// <summary>Shows people subscribe to by feed. Its folder only holds episodes on their way to a phone.</summary>
    Podcasts,

    /// <summary>
    /// Videos of people's own, organised only by folder: each folder in the library's folder is shown as a show, the
    /// folders in it as its seasons, and the videos as episodes named after their files. Nothing is looked up online.
    /// </summary>
    HomeVideos,
}

/// <summary>A named collection of root folders that share a media type, e.g. "Movies" at /data/movies.</summary>
public sealed class Library
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public required string Name { get; set; }
    public LibraryType Type { get; set; }
    public List<LibraryFolder> Folders { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastScannedAt { get; set; }

    /// <summary>Everything in it is fine for profiles with an age limit, rated or not (a cartoons folder, family videos).</summary>
    public bool FineForKids { get; set; }
}

/// <summary>
/// The library the DVR keeps its recordings in, made when the first one's done. It's part of Live TV (Live TV ›
/// Recordings), not a library of its own, so while Live TV is set up the library lists and Home leave it out.
/// </summary>
public static class RecordingsLibrary
{
    public const string Name = "Recordings";

    public static bool Is(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);
        return library.Type == LibraryType.TvShows && library.Name == Name;
    }
}

/// <summary>Why a scan kept a folder's files in the library rather than hide the ones it couldn't find.</summary>
public enum FolderHold
{
    /// <summary>The folder isn't there: a drive or share that isn't mounted.</summary>
    FolderMissing,

    /// <summary>The folder is empty but held files: often a mount point whose drive isn't mounted.</summary>
    FolderEmpty,

    /// <summary>Over half its files went at once: likelier a drive or permissions problem than a clear-out.</summary>
    MostFilesGone,
}

public sealed class LibraryFolder
{
    public int Id { get; set; }
    public Guid LibraryId { get; set; }
    public required string Path { get; set; }

    /// <summary>
    /// Set by a full scan that held the folder's files back (see <see cref="FolderHold"/>); null when the last one didn't.
    /// Shown in Library health, where the removal can be confirmed.
    /// </summary>
    public FolderHold? Hold { get; set; }

    /// <summary>When the scans started finding what <see cref="HeldMissing"/> and <see cref="HeldKnown"/> describe.</summary>
    public DateTime? HeldSince { get; set; }

    /// <summary>How many full scans in a row have found the same thing.</summary>
    public int HeldScans { get; set; }

    /// <summary>The files the held scans would have hidden, of <see cref="HeldKnown"/>.</summary>
    public int HeldMissing { get; set; }

    public int HeldKnown { get; set; }

    /// <summary>Someone confirmed in Library health that the files really are gone; the next scan lets it through.</summary>
    public bool RemovalConfirmed { get; set; }

    /// <summary>
    /// When a hold was last let through. Until the folder looks normal again, the files it hid wait out the grace period
    /// and go, rather than the folder (still empty) being held again.
    /// </summary>
    public DateTime? ReleasedAt { get; set; }
}
