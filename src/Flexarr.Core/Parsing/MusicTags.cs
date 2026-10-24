using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

public sealed record TrackInfo(string Artist, string Album, string Title, int? Disc, int? Track, int? Year, string? AlbumMusicBrainzId, string? ArtistMusicBrainzId);

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

        // Album artist groups compilations and featured-artist tracks under one artist.
        var artist = Tag("album_artist", "albumartist", "album artist", "artist") ?? folderArtist ?? "Unknown artist";
        var album = Tag("album") ?? (folderAlbum is null ? "Unknown album" : StripYear(folderAlbum));
        return new TrackInfo(
            artist,
            album,
            Tag("title") ?? fileTitle,
            Number(Tag("disc", "discnumber")),
            Number(Tag("track", "tracknumber")) ?? fileTrack,
            Year(Tag("date", "originaldate", "year")) ?? Year(folderAlbum),
            Tag("musicbrainz_albumid", "MusicBrainz Album Id"),
            Tag("musicbrainz_albumartistid", "musicbrainz_artistid", "MusicBrainz Album Artist Id", "MusicBrainz Artist Id"));
    }

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
