using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Flexarr.Core.Books;

/// <summary>
/// Reads the files beside a book that describe it, most trusted first: Audiobookshelf's <c>metadata.json</c>, the
/// <c>metadata.opf</c> Readarr, Bookshelf and Calibre write, <c>desc.txt</c> and <c>reader.txt</c>, and a cover image.
/// Only a book that's a folder has sidecars; files beside a single-file book could be any book's.
/// </summary>
public static partial class BookSidecars
{
    private static readonly string[] ImageExtensions = [".jpg", ".jpeg", ".png", ".webp"];
    private static readonly string[] CoverNames = ["cover", "folder", "front", "poster"];

    /// <param name="folder">The book's folder.</param>
    public static BookInfo Read(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        if (!Directory.Exists(folder))
        {
            return BookInfo.Empty;
        }

        var info = BookInfo.Empty;
        foreach (var source in new Func<BookInfo?>[] { () => Json(Path.Combine(folder, "metadata.json")), () => Opf(folder) })
        {
            if (source() is { } read)
            {
                info = info.Over(read);
            }
        }

        if (info.Description is null && ReadText(Path.Combine(folder, "desc.txt")) is { } description)
        {
            info = info with { Description = description };
        }

        if (info.Narrators.Count == 0 && ReadText(Path.Combine(folder, "reader.txt")) is { } reader)
        {
            info = info with { Narrators = [.. reader.Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)] };
        }

        return info with { Cover = Cover(folder) };
    }

    /// <summary>
    /// The book's cover in its folder: one named cover, folder or front, then any image with "cover" in its name, then
    /// the only image there.
    /// </summary>
    public static string? Cover(string folder)
    {
        string[] images;
        try
        {
            images = [.. Directory.EnumerateFiles(folder).Where(f => ImageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)
                && !Path.GetFileName(f).StartsWith('.')).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return CoverNames.Select(n => images.FirstOrDefault(i => string.Equals(Path.GetFileNameWithoutExtension(i), n, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i is not null)
            ?? images.FirstOrDefault(i => Path.GetFileNameWithoutExtension(i).Contains("cover", StringComparison.OrdinalIgnoreCase))
            ?? (images.Length == 1 ? images[0] : null);
    }

    /// <summary>Audiobookshelf's <c>metadata.json</c>, written when it stores metadata with the files.</summary>
    internal static BookInfo? Json(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;
            List<string> Strings(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
                ? [.. v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()).Where(s => s.Length > 0)]
                : [];
            bool? Flag(string name) => root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

            var year = Text("publishedYear");
            var date = Text("publishedDate");
            return new BookInfo
            {
                Title = Text("title"),
                Subtitle = Text("subtitle"),
                Authors = Strings("authors"),
                Narrators = Strings("narrators"),
                Series = [.. Strings("series").Select(SeriesFromText)],
                Genres = Strings("genres"),
                Description = Text("description") is { } d ? PlainText(d) : null,
                Publisher = Text("publisher"),
                Year = int.TryParse(year ?? date?[..Math.Min(4, date.Length)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null,
                ReleaseDate = date is not null && DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null,
                Language = Text("language"),
                Isbn = Text("isbn"),
                Asin = Text("asin")?.ToUpperInvariant(),
                Explicit = Flag("explicit"),
                Abridged = Flag("abridged"),
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"Discworld #4", "Discworld #1.5" or "Discworld".</summary>
    private static SeriesPart SeriesFromText(string value) =>
        SeriesHash().Match(value) is { Success: true } m ? new SeriesPart(m.Groups["name"].Value.Trim(), m.Groups["n"].Value) : new SeriesPart(value, null);

    /// <summary>The book's OPF file: <c>metadata.opf</c>, or the only <c>.opf</c> in the folder.</summary>
    private static BookInfo? Opf(string folder)
    {
        var path = Path.Combine(folder, "metadata.opf");
        if (!File.Exists(path))
        {
            var opfs = Directory.EnumerateFiles(folder, "*.opf").Take(2).ToList();
            if (opfs.Count != 1)
            {
                return null;
            }

            path = opfs[0];
        }

        try
        {
            using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return Opf(XDocument.Load(reader));
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The Dublin Core metadata in an OPF package, with Calibre's series.</summary>
    internal static BookInfo Opf(XDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var metadata = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "metadata");
        if (metadata is null)
        {
            return BookInfo.Empty;
        }

        IEnumerable<XElement> Dc(string name) => metadata.Elements().Where(e => e.Name.LocalName == name);
        string? First(string name) => Dc(name).Select(e => e.Value.Trim()).FirstOrDefault(v => v.Length > 0);
        string? Role(XElement e) => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "role")?.Value;
        string? Meta(string name) => metadata.Elements().Where(e => e.Name.LocalName == "meta")
            .FirstOrDefault(e => e.Attribute("name")?.Value == name)?.Attribute("content")?.Value?.Trim();
        string? Identifier(params string[] schemes) => Dc("identifier")
            .FirstOrDefault(e => e.Attributes().Any(a => a.Name.LocalName == "scheme" && schemes.Contains(a.Value, StringComparer.OrdinalIgnoreCase)))?.Value.Trim();

        var creators = Dc("creator").ToList();
        List<string> People(params string[] roles) => [.. creators.Where(c => roles.Contains(Role(c) ?? "aut")).Select(c => c.Value.Trim()).Where(v => v.Length > 0)];
        var date = First("date");
        var series = Meta("calibre:series") is { Length: > 0 } name ? new List<SeriesPart> { new(name, Meta("calibre:series_index") is { } i ? TrimIndex(i) : null) } : [];

        return new BookInfo
        {
            Title = First("title"),
            Authors = People("aut"),
            Narrators = People("nrt"),
            Translators = People("trl"),
            Series = series,
            Description = First("description") is { } d ? PlainText(d) : null,
            Publisher = First("publisher"),
            Year = date is not null && date.Length >= 4 && int.TryParse(date[..4], NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null,
            ReleaseDate = date is not null && date.Length >= 10 && DateOnly.TryParseExact(date[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var r) ? r : null,
            Language = First("language"),
            Isbn = Identifier("ISBN"),
            Asin = Identifier("ASIN", "AUDIBLE_ASIN", "AMAZON")?.ToUpperInvariant(),
            Genres = [.. Dc("subject").Select(e => e.Value.Trim()).Where(v => v.Length > 0)],
        };
    }

    /// <summary>Calibre writes "3.0" for book 3.</summary>
    private static string TrimIndex(string index) =>
        double.TryParse(index, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n.ToString("0.##", CultureInfo.InvariantCulture) : index;

    private static string? ReadText(string path)
    {
        try
        {
            return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>A description without its HTML: paragraphs become blank lines, tags and entities go.</summary>
    public static string PlainText(string html)
    {
        var text = Paragraphs().Replace(html, "\n\n");
        text = Breaks().Replace(text, "\n");
        text = Tags().Replace(text, "");
        text = WebUtility.HtmlDecode(text);
        return BlankLines().Replace(text, "\n\n").Trim();
    }

    [GeneratedRegex(@"^(?<name>.+?)\s*#\s*(?<n>\d+(?:\.\d+)?(?:-\d+)?)\s*$")]
    private static partial Regex SeriesHash();

    [GeneratedRegex(@"<\s*(?:/p|/div)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Paragraphs();

    [GeneratedRegex(@"<\s*(?:br\s*/?|/li)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex Breaks();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\n\s*\n+")]
    private static partial Regex BlankLines();
}
