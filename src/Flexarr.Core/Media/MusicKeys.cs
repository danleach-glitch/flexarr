using System.Text;
using Flexarr.Core.Paths;

namespace Flexarr.Core.Media;

/// <summary>
/// How the scanner keys artists and albums (by the tags' names, folded), so a merge can record what it would have used
/// (see <see cref="MusicAlias"/>).
/// </summary>
public static class MusicKeys
{
    /// <summary>The scanner's fold: trimmed, composed and lower case, so "Abbey Road" and "abbey road" are one album.</summary>
    public static string Fold(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Trim().Normalize(NormalizationForm.FormC).ToLowerInvariant();
    }

    public static string ArtistKey(string name) => $"artist:{Fold(name)}";

    public static string AlbumKey(Guid artistId, string title) => $"album:{artistId}:{Fold(title)}";

    public const string PathPrefix = "path:";

    public static string PathKey(string folder) => PathPrefix + PathComparison.Key(folder);
}
