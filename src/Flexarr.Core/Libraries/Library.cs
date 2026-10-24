namespace Flexarr.Core.Libraries;

public enum LibraryType
{
    Movies,
    TvShows,
    Music,
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
}

public sealed class LibraryFolder
{
    public int Id { get; set; }
    public Guid LibraryId { get; set; }
    public required string Path { get; set; }
}
