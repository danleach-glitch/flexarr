using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Flexarr.Core.Parsing;

/// <summary>
/// What a Kodi-style .nfo file says about a film, show or episode. Every field is null when the file doesn't say;
/// a file that only holds a link (to IMDb, TMDB or TheTVDB) gives just the id.
/// </summary>
public sealed record NfoInfo(
    string? Title,
    string? OriginalTitle,
    int? Year,
    string? Plot,
    string? ContentRating,
    string? Studio,
    int? RuntimeMinutes,
    DateOnly? Premiered,
    double? Rating,
    IReadOnlyList<string> Genres,
    ExternalIds Ids);

/// <summary>
/// Reads the .nfo files Kodi, Jellyfin, Emby, tinyMediaManager (and Sonarr's and Radarr's Kodi metadata option) write
/// next to media: <c>&lt;movie&gt;</c>, <c>&lt;tvshow&gt;</c> and <c>&lt;episodedetails&gt;</c>.
/// </summary>
public static partial class NfoParser
{
    /// <summary>The largest .nfo read: real ones are a few kilobytes, so anything far bigger isn't one.</summary>
    private const long MaxBytes = 1024 * 1024;

    /// <param name="path">The .nfo file.</param>
    /// <param name="root">The element expected at the top (movie, tvshow, episodedetails).</param>
    /// <returns>Null when the file is missing, unreadable, or is about something else.</returns>
    public static NfoInfo? Read(string path, string root)
    {
        ArgumentNullException.ThrowIfNull(path);
        try
        {
            var file = new FileInfo(path);
            return file.Exists && file.Length <= MaxBytes ? Parse(File.ReadAllText(path), root) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static NfoInfo? Parse(string text, string root)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(root);

        // Kodi allows a link after the XML ("…</movie>\nhttps://www.imdb.com/title/tt…"), and a file holding a link alone.
        var start = text.IndexOf($"<{root}", StringComparison.OrdinalIgnoreCase);
        var endTag = $"</{root}>";
        var end = start < 0 ? -1 : text.IndexOf(endTag, start, StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0)
        {
            var linked = Linked(text);
            return linked == new ExternalIds(null, null, null) ? null : Empty(linked);
        }

        XElement element;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text[start..(end + endTag.Length)]),
                new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            element = XElement.Load(reader);
        }
        catch (XmlException)
        {
            return null;
        }

        string? Text(string name) => element.Elements().FirstOrDefault(e => Is(e, name))?.Value is { } v && !string.IsNullOrWhiteSpace(v) ? v.Trim() : null;

        var premiered = Date(Text("premiered") ?? Text("aired") ?? Text("releasedate"));
        var year = Number(Text("year")) is > 1800 and var y ? y : premiered?.Year;
        var genres = element.Elements().Where(e => Is(e, "genre"))
            .SelectMany(e => e.Value.Split(" / ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new NfoInfo(
            Text("title"),
            Text("originaltitle"),
            year,
            Text("plot") ?? Text("outline"),
            Certification(Text("mpaa")),
            Text("studio"),
            Number(Text("runtime")) is > 0 and var runtime ? runtime : null,
            premiered,
            Rating(element),
            genres,
            Ids(element, Linked(text[(end + endTag.Length)..]), root));
    }

    private static NfoInfo Empty(ExternalIds ids) => new(null, null, null, null, null, null, null, null, null, [], ids);

    private static bool Is(XElement e, string name) => string.Equals(e.Name.LocalName, name, StringComparison.OrdinalIgnoreCase);

    private static ExternalIds Ids(XElement element, ExternalIds linked, string root)
    {
        string? tmdb = null, imdb = null, tvdb = null;
        foreach (var id in element.Elements().Where(e => Is(e, "uniqueid")))
        {
            var value = id.Value.Trim();
            switch (id.Attribute("type")?.Value.ToUpperInvariant())
            {
                case "TMDB": tmdb ??= Digits(value); break;
                case "IMDB": imdb ??= Imdb(value); break;
                case "TVDB": tvdb ??= Digits(value); break;
            }
        }

        string? Text(string name) => element.Elements().FirstOrDefault(e => Is(e, name))?.Value.Trim();
        tmdb ??= Digits(Text("tmdbid"));
        imdb ??= Imdb(Text("imdbid")) ?? Imdb(Text("imdb_id"));
        tvdb ??= Digits(Text("tvdbid"));

        // The old <id>: an IMDb id for films, TheTVDB's for shows.
        if (Text("id") is { Length: > 0 } legacy)
        {
            imdb ??= Imdb(legacy);
            if (string.Equals(root, "tvshow", StringComparison.OrdinalIgnoreCase))
            {
                tvdb ??= Digits(legacy);
            }
        }

        return new ExternalIds(tvdb ?? linked.TvdbId, tmdb ?? linked.TmdbId, imdb ?? linked.ImdbId);
    }

    /// <summary>The ids in links to IMDb, TMDB and TheTVDB.</summary>
    private static ExternalIds Linked(string text) =>
        new(TvdbLink().Match(text) is { Success: true } tvdb ? tvdb.Groups[1].Value : null,
            TmdbLink().Match(text) is { Success: true } tmdb ? tmdb.Groups[1].Value : null,
            ImdbLink().Match(text) is { Success: true } imdb ? imdb.Groups[1].Value : null);

    /// <summary>The default rating (or the first), out of 10.</summary>
    private static double? Rating(XElement element)
    {
        var ratings = element.Elements().FirstOrDefault(e => Is(e, "ratings"))?.Elements().Where(e => Is(e, "rating")).ToList() ?? [];
        var chosen = ratings.FirstOrDefault(r => string.Equals(r.Attribute("default")?.Value, "true", StringComparison.OrdinalIgnoreCase)) ?? ratings.FirstOrDefault();
        if (chosen is not null)
        {
            var value = Decimal(chosen.Elements().FirstOrDefault(e => Is(e, "value"))?.Value);
            var max = Decimal(chosen.Attribute("max")?.Value) is > 0 and var m ? m : 10;
            return value is > 0 ? Math.Round(value.Value * 10 / max, 1) : null;
        }

        return Decimal(element.Elements().FirstOrDefault(e => Is(e, "rating"))?.Value) is > 0 and <= 10 and var plain ? plain : null;
    }

    /// <summary>"Rated PG-13" and "US:PG-13" are both PG-13.</summary>
    private static string? Certification(string? mpaa)
    {
        if (mpaa is null)
        {
            return null;
        }

        var value = mpaa.StartsWith("Rated ", StringComparison.OrdinalIgnoreCase) ? mpaa[6..] : mpaa;
        var colon = value.IndexOf(':', StringComparison.Ordinal);
        value = (colon is > 0 and <= 3 ? value[(colon + 1)..] : value).Trim();
        return value.Length > 0 ? value : null;
    }

    private static DateOnly? Date(string? value) =>
        DateOnly.TryParseExact(value?.Length >= 10 ? value[..10] : value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

    private static int? Number(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static double? Decimal(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;

    private static string? Digits(string? value) => value is { Length: > 0 } && value.All(char.IsAsciiDigit) && value != "0" ? value : null;

    private static string? Imdb(string? value) => value is not null && ImdbId().IsMatch(value) ? value : null;

    [GeneratedRegex(@"^tt\d+$")]
    private static partial Regex ImdbId();

    [GeneratedRegex(@"imdb\.com/title/(tt\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbLink();

    [GeneratedRegex(@"themoviedb\.org/(?:movie|tv)/(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TmdbLink();

    [GeneratedRegex(@"thetvdb\.com/(?:\?tab=series&id=|series/|dereferrer/series/)(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex TvdbLink();
}
