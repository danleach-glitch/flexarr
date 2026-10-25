using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Flexarr.Core.Podcasts;

/// <summary>What a podcast's RSS feed says about the show.</summary>
public sealed record ParsedFeed(
    string Title,
    string? Author,
    string? Description,
    string? Link,
    string? ImageUrl,
    string? Language,
    bool Explicit,
    IReadOnlyList<string> Categories,
    string? PodcastGuid,
    string? NewFeedUrl,
    IReadOnlyList<ParsedEpisode> Episodes);

/// <summary>An episode in a feed: only those with a sound file to play are kept.</summary>
public sealed record ParsedEpisode(
    string EpisodeGuid,
    string Title,
    string? Description,
    string AudioUrl,
    string? MimeType,
    long? LengthBytes,
    DateTime? PublishedAt,
    double? DurationSeconds,
    string? ImageUrl,
    int? Season,
    int? Episode,
    PodcastEpisodeType EpisodeType,
    bool Explicit,
    string? ChaptersUrl,
    string? TranscriptUrl,
    string? TranscriptType);

/// <summary>
/// Reads podcast RSS: RSS 2.0 with Apple's <c>itunes:</c> tags and Podcasting 2.0's <c>podcast:</c> ones. Feeds in the
/// wild are loose, so it reads what it can: an unknown date, a duration in odd units or HTML in a description are made
/// the best of rather than refused. Only a document that isn't RSS at all fails.
/// </summary>
public static partial class FeedParser
{
    private static readonly XNamespace ITunes = "http://www.itunes.com/dtds/podcast-1.0.dtd";
    private static readonly XNamespace Podcast = "https://podcastindex.org/namespace/1.0";
    private static readonly XNamespace Content = "http://purl.org/rss/1.0/modules/content/";
    private static readonly XNamespace Media = "http://search.yahoo.com/mrss/";

    /// <summary>Transcript formats a player can show alongside the sound, best first.</summary>
    private static readonly string[] TranscriptTypes = ["application/x-subrip", "application/srt", "text/vtt", "text/srt"];

    /// <exception cref="FormatException">The document isn't an RSS feed.</exception>
    public static ParsedFeed Parse(Stream xml)
    {
        ArgumentNullException.ThrowIfNull(xml);
        XDocument document;
        try
        {
            // No DTDs: a feed has no business declaring entities, and expanding them is how XML bombs work.
            using var reader = XmlReader.Create(xml, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null, IgnoreComments = true });
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new FormatException("This isn't a podcast feed: it isn't valid XML.", ex);
        }

        var channel = document.Root?.Name.LocalName == "rss" ? document.Root.Element("channel") : null;
        if (channel is null)
        {
            throw new FormatException("This isn't a podcast feed: there's no RSS channel in it.");
        }

        var title = Clean(Text(channel, "title")) ?? Clean(Text(channel, ITunes + "title"));
        if (string.IsNullOrEmpty(title))
        {
            throw new FormatException("This feed has no title.");
        }

        var showExplicit = IsExplicit(Text(channel, ITunes + "explicit"));
        var episodes = new List<ParsedEpisode>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in channel.Elements("item"))
        {
            if (Episode(item, showExplicit) is { } episode && seen.Add(episode.EpisodeGuid))
            {
                episodes.Add(episode);
            }
        }

        return new ParsedFeed(
            title,
            Clean(Text(channel, ITunes + "author")) ?? Clean(Text(channel, ITunes + "owner", ITunes + "name")),
            PlainText(Text(channel, ITunes + "summary") is { Length: > 0 } summary && (Text(channel, "description")?.Length ?? 0) < summary.Length
                ? summary : Text(channel, "description")),
            WebUrl(Text(channel, "link")),
            WebUrl(channel.Element(ITunes + "image")?.Attribute("href")?.Value) ?? WebUrl(Text(channel, "image", "url")),
            Clean(Text(channel, "language"))?.ToLowerInvariant(),
            showExplicit,
            channel.Elements(ITunes + "category").SelectMany(Categories).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Clean(Text(channel, Podcast + "guid")),
            WebUrl(Text(channel, ITunes + "new-feed-url")),
            episodes);
    }

    private static ParsedEpisode? Episode(XElement item, bool showExplicit)
    {
        var enclosure = item.Element("enclosure");
        var url = WebUrl(enclosure?.Attribute("url")?.Value) ?? WebUrl(item.Element(Media + "content")?.Attribute("url")?.Value);
        var type = Clean(enclosure?.Attribute("type")?.Value)?.ToLowerInvariant();
        if (url is null || (type is not null && !type.StartsWith("audio/", StringComparison.Ordinal) && !IsAudioFile(url)))
        {
            return null; // a video podcast, or a post with no sound
        }

        var title = Clean(Text(item, "title")) ?? Clean(Text(item, ITunes + "title")) ?? "Untitled episode";
        var description = Text(item, Content + "encoded") is { Length: > 0 } encoded ? encoded
            : Text(item, "description") is { Length: > 0 } plain ? plain
            : Text(item, ITunes + "summary");
        var transcript = item.Elements(Podcast + "transcript")
            .Select(t => (Url: WebUrl(t.Attribute("url")?.Value), Type: t.Attribute("type")?.Value?.ToLowerInvariant()))
            .Where(t => t.Url is not null && t.Type is not null && TranscriptTypes.Contains(t.Type))
            .OrderBy(t => Array.IndexOf(TranscriptTypes, t.Type))
            .FirstOrDefault();

        return new ParsedEpisode(
            Clean(Text(item, "guid")) ?? url,
            title,
            PlainText(description),
            url,
            type,
            long.TryParse(enclosure?.Attribute("length")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) && length > 0 ? length : null,
            Date(Text(item, "pubDate")),
            Duration(Text(item, ITunes + "duration")),
            WebUrl(item.Element(ITunes + "image")?.Attribute("href")?.Value),
            PositiveInt(Text(item, ITunes + "season")) ?? PositiveInt(item.Element(Podcast + "season")?.Value),
            PositiveInt(Text(item, ITunes + "episode")) ?? PositiveInt(item.Element(Podcast + "episode")?.Value),
            Clean(Text(item, ITunes + "episodeType"))?.ToLowerInvariant() switch
            {
                "trailer" => PodcastEpisodeType.Trailer,
                "bonus" => PodcastEpisodeType.Bonus,
                _ => PodcastEpisodeType.Full,
            },
            Text(item, ITunes + "explicit") is { } flag ? IsExplicit(flag) : showExplicit,
            WebUrl(item.Element(Podcast + "chapters")?.Attribute("url")?.Value),
            transcript.Url,
            transcript.Type);
    }

    /// <summary>Apple's categories nest: "Society &amp; Culture" › "Documentary". Both count.</summary>
    private static IEnumerable<string> Categories(XElement category)
    {
        if (Clean(category.Attribute("text")?.Value) is { } name)
        {
            yield return WebUtility.HtmlDecode(name);
        }

        foreach (var sub in category.Elements(ITunes + "category"))
        {
            if (Clean(sub.Attribute("text")?.Value) is { } subName)
            {
                yield return WebUtility.HtmlDecode(subName);
            }
        }
    }

    private static string? Text(XElement parent, XName name) => parent.Element(name)?.Value;

    private static string? Text(XElement parent, XName name, XName child) => parent.Element(name)?.Element(child)?.Value;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsExplicit(string? value) => Clean(value)?.ToLowerInvariant() is "yes" or "true" or "explicit";

    private static int? PositiveInt(string? value) =>
        int.TryParse(Clean(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;

    /// <summary>An http or https address, else null (a feed's relative links, javascript: and the like).</summary>
    public static string? WebUrl(string? value) =>
        Uri.TryCreate(Clean(value), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri.AbsoluteUri : null;

    private static bool IsAudioFile(string url)
    {
        var path = new Uri(url).AbsolutePath;
        return Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".m4a" or ".aac" or ".ogg" or ".opus" or ".oga" or ".wav" or ".flac";
    }

    /// <summary>"3723", "62:03", "1:02:03", even "1:02:03.5"; null for anything else.</summary>
    public static double? Duration(string? value)
    {
        var text = Clean(value);
        if (text is null)
        {
            return null;
        }

        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
            {
                return null;
            }

            total = total * 60 + n;
        }

        return total > 0 ? total : null;
    }

    private static readonly string[] DateFormats =
    [
        "ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz", "ddd, d MMM yyyy HH:mm:ss",
        "ddd, d MMMM yyyy HH:mm:ss zzz", "yyyy-MM-ddTHH:mm:sszzz", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd",
    ];

    /// <summary>US time zones feeds name instead of giving an offset (RFC 822 allows them).</summary>
    private static readonly Dictionary<string, string> ZoneOffsets = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GMT"] = "+00:00", ["UT"] = "+00:00", ["UTC"] = "+00:00", ["Z"] = "+00:00",
        ["EST"] = "-05:00", ["EDT"] = "-04:00", ["CST"] = "-06:00", ["CDT"] = "-05:00",
        ["MST"] = "-07:00", ["MDT"] = "-06:00", ["PST"] = "-08:00", ["PDT"] = "-07:00",
        ["BST"] = "+01:00", ["CET"] = "+01:00", ["CEST"] = "+02:00",
    };

    /// <summary>An RFC 822 date as feeds write them (names for zones, "+0000" offsets, missing weekdays), in UTC.</summary>
    public static DateTime? Date(string? value)
    {
        var text = Clean(value);
        if (text is null)
        {
            return null;
        }

        text = WhiteSpace().Replace(text, " ");
        // "+0000" → "+00:00", and a named zone → its offset, so one set of formats reads them all.
        text = NumericZone().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}:{m.Groups[3].Value}");
        var lastSpace = text.LastIndexOf(' ');
        if (lastSpace > 0 && ZoneOffsets.TryGetValue(text[(lastSpace + 1)..], out var offset))
        {
            text = $"{text[..lastSpace]} {offset}";
        }

        if (DateTimeOffset.TryParseExact(text, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out var exact)
            || DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal, out exact))
        {
            return exact.UtcDateTime;
        }

        // A weekday that doesn't match the date ("Mon, 3 Jan 2024") makes the formats above refuse it: drop it.
        var comma = text.IndexOf(',', StringComparison.Ordinal);
        return comma > 0 && comma < 10 ? Date(text[(comma + 1)..]) : null;
    }

    /// <summary>
    /// A description as plain text: HTML tags dropped, paragraphs kept apart by a blank line, line breaks and list items
    /// on lines of their own, entities decoded.
    /// Clients show it as text, so nothing a feed writes can run in them.
    /// </summary>
    public static string? PlainText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return null;
        }

        var text = LineBreak().Replace(html, "\n");
        text = BlockEnd().Replace(text, "\n\n");
        text = ListItem().Replace(text, "\n• ");
        text = Tag().Replace(text, "");
        text = WebUtility.HtmlDecode(text).Replace(' ', ' ');
        var lines = text.Split('\n').Select(l => WhiteSpace().Replace(l, " ").Trim());
        var builder = new StringBuilder();
        var blank = 0;
        foreach (var line in lines)
        {
            if (line.Length == 0)
            {
                blank++;
                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(blank > 0 ? "\n\n" : "\n");
            }

            builder.Append(line);
            blank = 0;
        }

        var result = builder.ToString();
        return result.Length == 0 ? null : result.Length > 20_000 ? result[..20_000] : result;
    }

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex LineBreak();

    [GeneratedRegex(@"<\s*(/p|/div|/h[1-6]|/ul|/ol|/blockquote)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockEnd();

    [GeneratedRegex(@"<\s*li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItem();

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex WhiteSpace();

    [GeneratedRegex(@"([+-])(\d{2}):?(\d{2})$")]
    private static partial Regex NumericZone();
}
