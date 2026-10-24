namespace Flexarr.Core.Libraries;

public enum LibraryType
{
    Movies,
    TvShows,
    Music,
}

/// <summary>A named collection of root folders that share a media type, e.g. "Movies" at /data/movies.</summary>
public sealed record Library(Guid Id, string Name, LibraryType Type, IReadOnlyList<string> RootFolders);
