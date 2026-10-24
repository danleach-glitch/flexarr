using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

public sealed record ExternalIds(string? TvdbId, string? TmdbId, string? ImdbId);

/// <summary>Reads and strips the <c>{tvdb-123}</c>, <c>{tmdb-123}</c>, <c>{imdb-tt123}</c> tags Sonarr/Radarr can put in names.</summary>
public static partial class ExternalIdParser
{
    public static (string Name, ExternalIds Ids) Extract(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        string? tvdb = null, tmdb = null, imdb = null;
        foreach (Match m in IdTag().Matches(name))
        {
            var value = m.Groups["id"].Value;
            switch (m.Groups["kind"].Value.ToUpperInvariant())
            {
                case "TVDB": tvdb ??= value; break;
                case "TMDB": tmdb ??= value; break;
                case "IMDB": imdb ??= value; break;
            }
        }

        var stripped = IdTag().Replace(name, string.Empty);
        return (Whitespace().Replace(stripped, " ").Trim(), new ExternalIds(tvdb, tmdb, imdb));
    }

    [GeneratedRegex(@"[\{\[](?<kind>tvdb|tmdb|imdb)(?:id)?[-=](?<id>[a-z]*\d+)[\}\]]", RegexOptions.IgnoreCase)]
    private static partial Regex IdTag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
