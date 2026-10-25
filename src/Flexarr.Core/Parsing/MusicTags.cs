using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

/// <summary>
/// What a file's tags (or its path) say about a track. Artist is the album's artist: the album artist tag, or the
/// track's own when there's none. TrackArtist is the track's own as tagged (null when it isn't), which on a
/// compilation or a featured-artist track isn't the album's.
/// </summary>
public sealed record TrackInfo(string Artist, string Album, string Title, int? Disc, int? Track, int? Year, string? AlbumMusicBrainzId, string? ArtistMusicBrainzId,
    string? TrackArtist = null);

/// <summary>Reads artist/album/track from tags, falling back to the Artist/Album/NN Title.ext folder layout.</summary>
public static partial class MusicTags
{
    public static TrackInfo Read(IReadOnlyDictionary<string, string> tags, string root, string filePath)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var segments = Path.GetRelativePath(root, filePath).Split(Path.DirectorySeparatorChar);
        var folderArtist = segments.Length >= 3 ? segments[^3] : segments.Length == 2 ? segments[0] : null;
        var folderAlbum = segments.Length >= 2 ? segments[^2] : null;
        var (fileTrack, fileTitle) = FromFileName(Path.GetFileNameWithoutExtension(filePath));

        string? Tag(params string[] names) => names.Select(n => tags.TryGetValue(n, out var v) ? v : null).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        // Album artist groups compilations and featured-artist tracks under one artist. A compilation flagged as one
        // but without an album artist is filed under Various Artists rather than split between its tracks' artists.
        var trackArtist = Tag("artist")?.Trim();
        var artist = Tag("album_artist", "albumartist", "album artist")
            ?? (IsCompilation(Tag("compilation")) ? VariousArtists : trackArtist)
            ?? folderArtist ?? "Unknown artist";
        var album = Tag("album") ?? (folderAlbum is null ? "Unknown album" : StripYear(folderAlbum));
        return new TrackInfo(
            artist,
            album,
            Tag("title") ?? fileTitle,
            Number(Tag("disc", "discnumber")),
            Number(Tag("track", "tracknumber")) ?? fileTrack,
            Year(Tag("date", "originaldate", "year")) ?? Year(folderAlbum),
            Tag("musicbrainz_albumid", "MusicBrainz Album Id"),
            Tag("musicbrainz_albumartistid", "musicbrainz_artistid", "MusicBrainz Album Artist Id", "MusicBrainz Artist Id"),
            trackArtist);
    }

    /// <summary>The album artist a compilation gets when its tags name none.</summary>
    public const string VariousArtists = "Various Artists";

    /// <summary>Whether two artist names name the same artist, give or take case and spacing.</summary>
    public static bool SameArtist(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>iTunes and MusicBrainz write "1" (or "true"); anything else isn't flagged.</summary>
    private static bool IsCompilation(string? value) =>
        value is not null && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));

    /// <summary>"3/12" → 3.</summary>
    private static int? Number(string? value) =>
        value is not null && int.TryParse(value.Split('/')[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    private static int? Year(string? value) =>
        value is not null && YearPattern().Match(value) is { Success: true } m ? int.Parse(m.Value, CultureInfo.InvariantCulture) : null;

    private static string StripYear(string folder) => FolderYear().Replace(folder, string.Empty).Trim(' ', '-');

    /// <summary>"03 - Paranoid Android" → (3, "Paranoid Android").</summary>
    private static (int? Track, string Title) FromFileName(string name)
    {
        var match = LeadingTrack().Match(name);
        return match.Success
            ? (int.Parse(match.Groups["n"].Value, CultureInfo.InvariantCulture) % 100, match.Groups["title"].Value.Trim())
            : (null, name);
    }

    [GeneratedRegex(@"\b(19|20)\d{2}\b")]
    private static partial Regex YearPattern();

    [GeneratedRegex(@"\s*[\(\[]\s*(19|20)\d{2}\s*[\)\]]\s*")]
    private static partial Regex FolderYear();

    [GeneratedRegex(@"^(?<n>\d{1,3})[\s._-]+(?<title>.+)$")]
    private static partial Regex LeadingTrack();
}
