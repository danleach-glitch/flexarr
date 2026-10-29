using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Podcasts;

/// <summary>A named part of an episode, starting <see cref="StartSeconds"/> in.</summary>
public sealed record PodcastChapter(double StartSeconds, string Title);

/// <summary>
/// An episode's chapters: from the feed's Podcasting 2.0 chapters file, or else from a list of times in the episode's
/// notes ("00:00 Intro", "03:50 A gift for Jessica"), which is how most shows write them.
/// </summary>
public static partial class PodcastChapters
{
    /// <summary>Chapters files are a few kilobytes; anything past this isn't one.</summary>
    public const long MaxFileBytes = 1024 * 1024;

    private const int MaxTitle = 200;

    /// <summary>
    /// The chapters in a Podcasting 2.0 chapters file (<c>{"chapters": [{"startTime": 0, "title": "Intro"}]}</c>), in
    /// order. Ones marked <c>"toc": false</c> are meant to change the artwork, not to be listed, so they're left out, as
    /// are ones without a title. Empty when the file isn't one.
    /// </summary>
    public static IReadOnlyList<PodcastChapter> FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("chapters", out var list) || list.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var chapters = new List<PodcastChapter>();
            foreach (var chapter in list.EnumerateArray())
            {
                if (chapter.ValueKind != JsonValueKind.Object
                    || (chapter.TryGetProperty("toc", out var toc) && toc.ValueKind == JsonValueKind.False)
                    || !chapter.TryGetProperty("startTime", out var start) || !TrySeconds(start, out var seconds)
                    || !chapter.TryGetProperty("title", out var title) || title.ValueKind != JsonValueKind.String
                    || Title(title.GetString()) is not { } name)
                {
                    continue;
                }

                chapters.Add(new PodcastChapter(seconds, name));
            }

            return [.. chapters.OrderBy(c => c.StartSeconds)];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The chapters an episode's notes list: lines that start with a time, then the chapter's name. At least two, in
    /// order, are needed to count as a list, so a single "at 12:30 we talk about…" isn't one. Times past
    /// <paramref name="durationSeconds"/>, when it's known, are left out.
    /// </summary>
    public static IReadOnlyList<PodcastChapter> FromNotes(string? notes, double? durationSeconds = null)
    {
        if (string.IsNullOrWhiteSpace(notes))
        {
            return [];
        }

        var chapters = new List<PodcastChapter>();
        foreach (var line in notes.Split('\n'))
        {
            var match = TimedLine().Match(line);
            if (!match.Success || Timestamp(match.Groups["time"].Value) is not { } start || Title(match.Groups["title"].Value) is not { } title)
            {
                continue;
            }

            if (durationSeconds > 0 && start >= durationSeconds)
            {
                continue;
            }

            // A time that goes backwards is a slip ("02:00 Tours, 00:00 Criticism, 04:58 A gift") or the start of something
            // else, such as last week's list: it's skipped, and the list carries on from the times after it.
            if (chapters.Count > 0 && start <= chapters[^1].StartSeconds)
            {
                continue;
            }

            chapters.Add(new PodcastChapter(start, title));
        }

        return chapters.Count >= 2 ? chapters : [];
    }

    /// <summary>"1:02:55", "52:50" or "3:50" in seconds.</summary>
    public static double? Timestamp(string text)
    {
        var parts = text.Split(':');
        if (parts.Length is < 2 or > 3 || parts.Skip(1).Any(p => p.Length != 2))
        {
            return null;
        }

        double seconds = 0;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            seconds = seconds * 60 + value;
        }

        return parts.Skip(1).All(p => int.Parse(p, CultureInfo.InvariantCulture) < 60) ? seconds : null;
    }

    private static bool TrySeconds(JsonElement value, out double seconds)
    {
        seconds = 0;
        if (value.ValueKind == JsonValueKind.Number)
        {
            seconds = value.GetDouble();
        }
        else if (value.ValueKind != JsonValueKind.String
                 || !double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds))
        {
            return false;
        }

        return seconds >= 0 && double.IsFinite(seconds);
    }

    private static string? Title(string? text)
    {
        var title = text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            return null;
        }

        return title.Length > MaxTitle ? title[..MaxTitle].TrimEnd() + "…" : title;
    }

    // A time at the start of a line, perhaps after a bullet or in brackets, then a separator or just a space, then the name:
    // "00:00 Intro", "• (03:50) A gift", "[1:02:55] - Outro", "14:26 | Learning to spin a car".
    [GeneratedRegex(@"^\s*(?:[•\-–—*]\s*)?[\(\[]?(?<time>(?:\d{1,2}:)?\d{1,2}:\d{2})[\)\]]?\s*(?:[-–—:|•.]\s*)?(?<title>\S.*)$")]
    private static partial Regex TimedLine();
}
