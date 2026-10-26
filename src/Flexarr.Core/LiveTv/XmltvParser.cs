using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml;

namespace Flexarr.Core.LiveTv;

/// <summary>A programme as an XMLTV guide lists it, on the guide's channel id.</summary>
public sealed record XmltvProgramme(
    string ChannelId,
    DateTime Start,
    DateTime End,
    string Title,
    string? Subtitle,
    string? Description,
    string? Category,
    int? Season,
    int? Episode,
    string? ImageUrl,
    bool IsNew);

/// <summary>
/// Reads the programmes out of an XMLTV guide as it downloads. Guides run to tens of megabytes, so the document is
/// never held whole. Its DOCTYPE is ignored rather than fetched, and nothing external is resolved.
/// </summary>
public static partial class XmltvParser
{
    private static readonly XmlReaderSettings Settings = new()
    {
        Async = true,
        DtdProcessing = DtdProcessing.Ignore,
        XmlResolver = null,
        IgnoreComments = true,
        IgnoreWhitespace = true,
        IgnoreProcessingInstructions = true,
    };

    public static async IAsyncEnumerable<XmltvProgramme> ProgrammesAsync(Stream guide, [EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = XmlReader.Create(guide, Settings);
        while (await reader.ReadAsync())
        {
            ct.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Name != "programme")
            {
                continue;
            }

            var channel = reader.GetAttribute("channel");
            var start = ParseTime(reader.GetAttribute("start"));
            var end = ParseTime(reader.GetAttribute("stop"));
            if (reader.IsEmptyElement)
            {
                continue;
            }

            string? title = null, subtitle = null, description = null, category = null, image = null;
            int? season = null, episode = null;
            var isNew = false;
            using (var programme = reader.ReadSubtree())
            {
                // Each case moves past its element itself (reading its text does), so the loop never reads twice. The
                // read always happens, even for a field already set (a second category): skipping it would loop forever.
                await programme.ReadAsync();
                await programme.ReadAsync();
                while (!programme.EOF)
                {
                    if (programme.NodeType != XmlNodeType.Element || programme.Depth != 1)
                    {
                        await programme.ReadAsync();
                        continue;
                    }

                    switch (programme.Name)
                    {
                        case "title":
                            title = First(title, await programme.ReadElementContentAsStringAsync());
                            break;
                        case "sub-title":
                            subtitle = First(subtitle, await programme.ReadElementContentAsStringAsync());
                            break;
                        case "desc":
                            description = First(description, await programme.ReadElementContentAsStringAsync());
                            break;
                        case "category":
                            category = First(category, await programme.ReadElementContentAsStringAsync());
                            break;
                        case "episode-num":
                            var system = programme.GetAttribute("system");
                            var (s, e) = ParseEpisode(system, await programme.ReadElementContentAsStringAsync());
                            season ??= s;
                            episode ??= e;
                            break;
                        case "icon":
                            image ??= programme.GetAttribute("src");
                            await programme.SkipAsync();
                            break;
                        case "new":
                            isNew = true;
                            await programme.SkipAsync();
                            break;
                        default:
                            await programme.SkipAsync();
                            break;
                    }
                }
            }

            if (channel is { Length: > 0 } && start is { } from && end is { } to && to > from && title is { Length: > 0 })
            {
                yield return new XmltvProgramme(channel, from, to, title, subtitle, description, category, season, episode, image, isNew);
            }
        }
    }

    /// <summary>XMLTV's "20261026063000 +0000" (the offset is optional, and means UTC when it's missing), as UTC.</summary>
    internal static DateTime? ParseTime(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var parts = value.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Length < 12
            || !DateTime.TryParseExact(parts[0][..Math.Min(14, parts[0].Length)], parts[0].Length >= 14 ? "yyyyMMddHHmmss" : "yyyyMMddHHmm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var offset = TimeSpan.Zero;
        if (parts.Length == 2 && OffsetPattern().Match(parts[1]) is { Success: true } m)
        {
            offset = new TimeSpan(int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture), 0);
            if (m.Groups[1].Value == "-")
            {
                offset = -offset;
            }
        }

        return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
    }

    /// <summary>
    /// Season and episode from <c>xmltv_ns</c> ("0.4." is series 1, episode 5: it counts from zero, and either part
    /// may be blank or "3/10") or <c>onscreen</c> ("S1 E5", "S01E05").
    /// </summary>
    internal static (int? Season, int? Episode) ParseEpisode(string? system, string value)
    {
        if (system == "xmltv_ns")
        {
            var parts = value.Split('.');
            return (parts.Length > 0 ? ZeroBased(parts[0]) : null, parts.Length > 1 ? ZeroBased(parts[1]) : null);
        }

        if (OnScreenPattern().Match(value) is { Success: true } m)
        {
            return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        }

        return (null, null);

        static int? ZeroBased(string part)
        {
            var number = part.Split('/')[0].Trim();
            return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n + 1 : null;
        }
    }

    /// <summary>The first non-blank value of a field the guide may repeat.</summary>
    private static string? First(string? kept, string value) => kept ?? (string.IsNullOrWhiteSpace(value) ? null : value.Trim());

    [GeneratedRegex(@"^([+-])(\d{2}):?(\d{2})$")]
    private static partial Regex OffsetPattern();

    [GeneratedRegex(@"S(\d{1,4})\s*E(\d{1,4})", RegexOptions.IgnoreCase)]
    private static partial Regex OnScreenPattern();
}
