using Flexarr.Core.Parsing;

namespace Flexarr.Metadata;

/// <summary>What sits beside a film, show or episode: its .nfo and artwork, each as a file:// URL.</summary>
public sealed record LocalArt(NfoInfo? Nfo, string? Poster, string? Backdrop, string? Logo)
{
    public static readonly LocalArt None = new(null, null, null, null);

    public bool IsEmpty => Nfo is null && Poster is null && Backdrop is null && Logo is null;
}

/// <summary>
/// Finds the .nfo files and artwork that Kodi, Jellyfin, Plex and tinyMediaManager users keep next to their media
/// (poster.jpg, fanart.jpg, season01-poster.jpg, an episode's -thumb.jpg). Only ever reads: Flexarr never writes into
/// media folders.
/// </summary>
public static class LocalFiles
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] PosterNames = ["poster", "folder", "cover", "movie", "show"];
    private static readonly string[] BackdropNames = ["fanart", "backdrop", "background", "art"];
    private static readonly string[] LogoNames = ["clearlogo", "logo"];

    /// <param name="path">The film's folder, or the film itself when it sits loose in the library's folder.</param>
    public static LocalArt ForMovie(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (Directory.Exists(path))
        {
            var files = Listing(path);
            var nfo = Read(files, "movie.nfo", "movie")
                ?? files.Values.Where(f => f.EndsWith(".nfo", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
                    .Select(f => NfoParser.Read(f, "movie")).FirstOrDefault(n => n is not null);
            return new LocalArt(nfo, Image(files, PosterNames), Image(files, BackdropNames), Image(files, LogoNames));
        }

        // A loose file shares its folder with other films: only names that start with its own count.
        var folder = Path.GetDirectoryName(path);
        if (folder is null || !File.Exists(path))
        {
            return LocalArt.None;
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        var listing = Listing(folder);
        return new LocalArt(
            Read(listing, $"{stem}.nfo", "movie"),
            Image(listing, [$"{stem}-poster", stem]),
            Image(listing, [$"{stem}-fanart", $"{stem}-backdrop"]),
            Image(listing, [$"{stem}-clearlogo", $"{stem}-logo"]));
    }

    public static LocalArt ForSeries(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            return LocalArt.None;
        }

        var files = Listing(folder);
        return new LocalArt(Read(files, "tvshow.nfo", "tvshow"), Image(files, PosterNames), Image(files, BackdropNames), Image(files, LogoNames));
    }

    /// <summary>
    /// A season's poster: season01-poster.jpg (or season-specials-poster.jpg) in the show's folder, or a poster in the
    /// season's own folder.
    /// </summary>
    public static string? SeasonPoster(string seriesFolder, int season)
    {
        ArgumentNullException.ThrowIfNull(seriesFolder);
        if (!Directory.Exists(seriesFolder))
        {
            return null;
        }

        var files = Listing(seriesFolder);
        string[] names = season == 0
            ? ["season-specials-poster", "season00-poster", "season0-poster", "season-specials", "season00"]
            : [$"season{season:00}-poster", $"season{season}-poster", $"season{season:00}", $"season{season}"];
        if (Image(files, names) is { } found)
        {
            return found;
        }

        var own = Directory.EnumerateDirectories(seriesFolder).FirstOrDefault(d => TvNameParser.ParseSeasonFolder(Path.GetFileName(d)) == season);
        return own is null ? null : Image(Listing(own), PosterNames);
    }

    /// <summary>A poster in a folder of its own: poster.jpg, folder.jpg, cover.jpg.</summary>
    public static string? FolderPoster(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        return Directory.Exists(folder) ? Image(Listing(folder), PosterNames) : null;
    }

    /// <summary>An episode's .nfo and still (Episode.nfo and Episode-thumb.jpg, or Episode.jpg, beside Episode.mkv).</summary>
    public static (NfoInfo? Nfo, string? Thumb) ForEpisode(string file, IReadOnlyDictionary<string, string> listing)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(listing);
        var stem = Path.GetFileNameWithoutExtension(file);
        return (Read(listing, $"{stem}.nfo", "episodedetails"), Image(listing, [$"{stem}-thumb", stem]));
    }

    /// <summary>A folder's files by lower-case name, so poster.jpg is found as Poster.JPG on a case-sensitive disk.</summary>
    public static IReadOnlyDictionary<string, string> Listing(string folder)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                files.TryAdd(Path.GetFileName(file), file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Unreadable: nothing local, as if it were empty.
        }

        return files;
    }

    private static NfoInfo? Read(IReadOnlyDictionary<string, string> files, string name, string root) =>
        files.TryGetValue(name, out var path) ? NfoParser.Read(path, root) : null;

    private static string? Image(IReadOnlyDictionary<string, string> files, IEnumerable<string> names)
    {
        foreach (var name in names)
        {
            foreach (var extension in ImageExtensions)
            {
                if (files.TryGetValue(name + extension, out var path))
                {
                    return $"file://{path}";
                }
            }
        }

        return null;
    }
}
