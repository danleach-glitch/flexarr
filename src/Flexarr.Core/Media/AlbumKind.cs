namespace Flexarr.Core.Media;

/// <summary>
/// What sort of release an album is, as one word, so an artist's page can group them the way record shops (and
/// Spotify) do: albums, then EPs and singles, then live albums and compilations, then the rest. MusicBrainz, via
/// Lidarr, says it in two parts: a primary type (Album, EP, Single, Broadcast, Other) and any number of secondary
/// types (Live, Compilation, Soundtrack, Remix, Demo…); this boils them down to one.
/// </summary>
public static class AlbumKind
{
    public const string Album = "album";
    public const string Ep = "ep";
    public const string SingleRelease = "single";
    public const string Live = "live";
    public const string Compilation = "compilation";
    public const string Soundtrack = "soundtrack";
    public const string Other = "other";

    /// <summary>
    /// The kind for MusicBrainz's primary and secondary types, or null when the primary type is missing or unknown and
    /// no secondary type settles it. A live recording is live whatever else it is (a live EP, a live compilation); a
    /// compilation or soundtrack is that whether an album or an EP. A remix album is still an album of the artist's,
    /// so Remix leaves the primary type alone; demos, mixtapes, DJ mixes, interviews and the like are "other".
    /// </summary>
    /// <param name="primary">Lidarr's <c>albumType</c>.</param>
    /// <param name="secondaryTypes">Lidarr's <c>secondaryTypes</c>.</param>
    public static string? From(string? primary, IEnumerable<string>? secondaryTypes)
    {
        var secondary = secondaryTypes?.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t.Trim()).ToList() ?? [];
        if (secondary.Any(t => Is(t, "Live")))
        {
            return Live;
        }

        if (secondary.Any(t => Is(t, "Compilation")))
        {
            return Compilation;
        }

        if (secondary.Any(t => Is(t, "Soundtrack")))
        {
            return Soundtrack;
        }

        if (secondary.Any(t => !Is(t, "Remix")))
        {
            return Other;
        }

        return primary?.Trim() switch
        {
            null or "" => null,
            var p when Is(p, "Album") => Album,
            var p when Is(p, "EP") => Ep,
            var p when Is(p, "Single") => SingleRelease,
            var p when Is(p, "Broadcast") || Is(p, "Other") => Other,
            _ => null,
        };
    }

    private static bool Is(string value, string type) => string.Equals(value, type, StringComparison.OrdinalIgnoreCase);
}
