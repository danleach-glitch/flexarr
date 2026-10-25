using Flexarr.Core.Media;
using Flexarr.Core.Paths;
using Flexarr.Integrations.Arr.Models;

namespace Flexarr.Metadata;

/// <summary>
/// Pairs the artists and albums the scanner built from tags with what Lidarr manages. Tags are often wrong or
/// incomplete ("Making movies", a remaster's year), so the folder on disk decides first: Lidarr knows exactly which
/// folder each of its albums lives in. MusicBrainz ids come next, when the tags carry them, and the title last.
/// </summary>
public static class LidarrMatcher
{
    /// <summary>
    /// The Lidarr artist for one of ours: by MusicBrainz id, then by folder (any of the artist's album folders inside
    /// the Lidarr artist's folder), then by name.
    /// </summary>
    /// <param name="artist">Our artist.</param>
    /// <param name="albumPaths">The folders of its albums, as Flexarr sees them.</param>
    /// <param name="candidates">Lidarr's artists.</param>
    /// <param name="mappings">Path mappings from Lidarr's paths to Flexarr's.</param>
    public static LidarrArtist? MatchArtist(MediaItem artist, IEnumerable<string?> albumPaths, IReadOnlyList<LidarrArtist> candidates, IReadOnlyList<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(artist);
        ArgumentNullException.ThrowIfNull(albumPaths);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(mappings);

        if (artist.MusicBrainzId is { Length: > 0 } mbid
            && candidates.FirstOrDefault(c => string.Equals(c.ForeignArtistId, mbid, StringComparison.OrdinalIgnoreCase)) is { } byId)
        {
            return byId;
        }

        var folders = albumPaths.OfType<string>().ToList();
        var byFolder = candidates
            .Where(c => c.Path is { Length: > 0 })
            .Select(c => (Artist: c, Inside: folders.Count(f => PathComparison.IsUnder(f, Mapped(c.Path!, mappings)))))
            .Where(c => c.Inside > 0)
            .OrderByDescending(c => c.Inside)
            .Select(c => c.Artist)
            .FirstOrDefault();
        if (byFolder is not null)
        {
            return byFolder;
        }

        return ByTitle(candidates, artist.Title, c => c.ArtistName, _ => null, null);
    }

    /// <summary>
    /// The Lidarr album for one of ours, among a matched artist's: by the folder Lidarr's files for it are in, then by
    /// MusicBrainz id (the tags' release id, or the release group's), then by title, nearest year first.
    /// </summary>
    /// <param name="album">Our album.</param>
    /// <param name="candidates">The Lidarr artist's albums.</param>
    /// <param name="files">The Lidarr artist's music files, which say which album each folder holds.</param>
    /// <param name="mappings">Path mappings from Lidarr's paths to Flexarr's.</param>
    public static LidarrAlbum? MatchAlbum(MediaItem album, IReadOnlyList<LidarrAlbum> candidates, IReadOnlyList<LidarrTrackFile> files, IReadOnlyList<PathMapping> mappings)
    {
        ArgumentNullException.ThrowIfNull(album);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(mappings);

        if (album.Path is { Length: > 0 } path)
        {
            // The album's own folder first; a multi-disc album may keep its files in disc folders inside it.
            var inFolder = files
                .Where(f => f.Path is { Length: > 0 })
                .Select(f => (f.AlbumId, Folder: Mapped(Path.GetDirectoryName(f.Path!) ?? f.Path!, mappings)))
                .Where(f => PathComparison.IsUnder(f.Folder, path))
                .GroupBy(f => f.AlbumId)
                .OrderByDescending(g => g.Count(f => PathComparison.Key(f.Folder) == PathComparison.Key(path)))
                .ThenByDescending(g => g.Count())
                .Select(g => candidates.FirstOrDefault(c => c.Id == g.Key))
                .FirstOrDefault(c => c is not null);
            if (inFolder is not null)
            {
                return inFolder;
            }
        }

        if (album.MusicBrainzId is { Length: > 0 } mbid)
        {
            var byId = candidates.FirstOrDefault(c => string.Equals(c.ForeignAlbumId, mbid, StringComparison.OrdinalIgnoreCase))
                ?? candidates.FirstOrDefault(c => c.Releases.Any(r => string.Equals(r.ForeignReleaseId, mbid, StringComparison.OrdinalIgnoreCase)));
            if (byId is not null)
            {
                return byId;
            }
        }

        return ByTitle(candidates, album.Title, c => c.Title, c => c.ReleaseDate?.Year, album.Year);
    }

    /// <summary>
    /// The candidate with the same title (accents and all), else the same title ignoring accents and punctuation; when
    /// several, the one whose year is nearest ours. Never a candidate with a different title: a wrong match is worse
    /// than none, since the tags are still there to fall back on.
    /// </summary>
    private static T? ByTitle<T>(IReadOnlyList<T> candidates, string title, Func<T, string> candidateTitle, Func<T, int?> candidateYear, int? year) where T : class
    {
        var wanted = Fold(title);
        var exact = candidates.Where(c => Fold(candidateTitle(c)) == wanted).ToList();
        var pool = exact.Count > 0 ? exact : candidates.Where(c => Loose(candidateTitle(c)) == Loose(title)).ToList();
        return pool
            .OrderBy(c => year is { } y && candidateYear(c) is { } cy ? Math.Abs(cy - y) : int.MaxValue)
            .FirstOrDefault();
    }

    private static string Mapped(string path, IReadOnlyList<PathMapping> mappings) => PathMapper.Map(path, mappings);

    /// <summary>The same folding the scanner groups tags by, so what it treats as one title matches as one here.</summary>
    private static string Fold(string value) => value.Trim().Normalize(System.Text.NormalizationForm.FormC).ToLowerInvariant();

    private static string Loose(string value) => new(value.Normalize(System.Text.NormalizationForm.FormD)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
}
