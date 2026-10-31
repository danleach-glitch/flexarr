using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Books;

/// <summary>
/// Reads what an audiobook's embedded tags say about the book. Taggers disagree, so each field is looked for under the
/// names the common ones use: album for the title, album artist or artist for the author, composer for the narrator
/// (Audiobookshelf, Mp3tag), MVNM/MVIN or grouping for the series, and ASIN, ISBN and so on.
/// </summary>
public static partial class BookTags
{
    /// <param name="tags">The first file's tags, case-insensitive.</param>
    /// <param name="singleFile">The book is one file, so its title tag is the book's (in a book of many files it's a chapter's).</param>
    public static BookInfo Read(IReadOnlyDictionary<string, string> tags, bool singleFile)
    {
        ArgumentNullException.ThrowIfNull(tags);
        string? Get(params string[] names) =>
            names.Select(n => tags.TryGetValue(n, out var v) ? v.Trim() : null).FirstOrDefault(v => !string.IsNullOrEmpty(v));

        var title = Get("album") ?? (singleFile ? Get("title") : null);
        var series = new List<SeriesPart>();
        if (Get("series", "mvnm", "movementname", "©mvn", "seriesname") is { } name)
        {
            series.Add(new SeriesPart(name, Get("series-part", "seriespart", "series_part", "mvin", "movement", "©mvi", "seriesindex") is { } part ? Sequence(part) : null));
        }
        else if (Get("grouping", "contentgroup", "tit1") is { } grouping && GroupingSeries().Match(grouping) is { Success: true } g)
        {
            series.Add(new SeriesPart(g.Groups["name"].Value.Trim(), g.Groups["n"].Value));
        }

        var date = Get("date", "originaldate", "year", "releasedate", "tdrl");
        var comment = Get("comment");
        return new BookInfo
        {
            Title = title is null ? null : StripUnabridged(title),
            Subtitle = Get("subtitle"),
            Authors = People(Get("album_artist", "albumartist", "artist", "author", "writer")),
            Narrators = People(Get("narrator", "narratedby", "narrated_by", "composer", "performer", "reader")),
            Translators = People(Get("translator")),
            Series = series,
            Description = Get("description", "desc", "synopsis", "longdescription", "ldes") ?? (comment is { Length: > 80 } ? comment : null),
            Publisher = Get("publisher", "label", "organization"),
            Year = date is not null && YearPart().Match(date) is { Success: true } y ? int.Parse(y.Value, CultureInfo.InvariantCulture) : null,
            ReleaseDate = date is not null && DateOnly.TryParseExact(date.Length >= 10 ? date[..10] : date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null,
            Language = Get("language", "lang"),
            Asin = Get("asin", "audible_asin", "audibleasin", "audible-asin")?.ToUpperInvariant(),
            Isbn = Get("isbn", "isbn13", "isbn10")?.Replace("-", "", StringComparison.Ordinal),
            Explicit = Get("itunesadvisory", "rtng", "explicit") is { } e ? e is "1" or "true" or "explicit" : null,
            Abridged = Get("abridged") is { } a ? a is "1" or "true" or "yes" : null,
            Genres = Get("genre") is { } genre ? [.. genre.Split([';', '/', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase)] : [],
        };
    }

    /// <summary>"Stephen Fry; Hugh Laurie", "Stephen Fry/Hugh Laurie", "Stephen Fry, Hugh Laurie" as separate people.</summary>
    internal static IReadOnlyList<string> People(string? value) =>
        value is null ? [] : [.. value.Split('/').SelectMany(BookLayout.SplitAuthors).Select(p => p.Trim()).Where(p => p.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>"3", "Book 3", "#3", "3.5" → "3", "3.5".</summary>
    internal static string Sequence(string value) =>
        SequenceNumber().Match(value) is { Success: true } m ? m.Value.Replace(',', '.') : value.Trim();

    /// <summary>"Dune (Unabridged)" → "Dune": Audible's titles say it, the book's name doesn't.</summary>
    private static string StripUnabridged(string title) => Unabridged().Replace(title, "").Trim();

    [GeneratedRegex(@"^(?<name>.+?)(?:,\s*|\s+)(?:book|bk\.?|vol\.?|volume|#)\s*(?<n>\d+(?:\.\d+)?)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex GroupingSeries();

    [GeneratedRegex(@"\d+(?:[.,]\d+)?(?:-\d+)?")]
    private static partial Regex SequenceNumber();

    [GeneratedRegex(@"(?:19|20)\d{2}")]
    private static partial Regex YearPart();

    [GeneratedRegex(@"\s*[\(\[](?:un)?abridged[\)\]]\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex Unabridged();
}
