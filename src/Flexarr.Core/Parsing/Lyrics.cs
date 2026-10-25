using System.Globalization;
using System.Text.RegularExpressions;

namespace Flexarr.Core.Parsing;

/// <summary>One line of lyrics; <see cref="Time"/> is where in the track it is sung, in seconds, when known.</summary>
public sealed record LyricLine(double? Time, string Text);

/// <summary>A track's lyrics. Synced lyrics have a time on every line, in order; unsynced ones are plain text.</summary>
public sealed record Lyrics(bool Synced, IReadOnlyList<LyricLine> Lines);

/// <summary>
/// Reads lyrics from the LRC format (<c>[mm:ss.xx]line</c>) or plain text, and finds them in a file's tags. Timed lines
/// make the result synced; anything else is kept as plain lines.
/// </summary>
public static partial class LyricsParser
{
    /// <summary>
    /// Tags that hold lyrics, as ffprobe names them: Vorbis comments and MP4 use LYRICS (or UNSYNCEDLYRICS), ID3's USLT
    /// frame comes out as "lyrics-eng" (its language appended), and a few taggers write the frame names themselves.
    /// </summary>
    private static readonly string[] TagNames = ["lyrics", "unsyncedlyrics", "unsynced lyrics", "lyric", "uslt", "sylt"];

    /// <summary>The lyrics a file's tags carry, preferring a synced set when there's more than one.</summary>
    public static Lyrics? FromTags(IReadOnlyDictionary<string, string> tags)
    {
        ArgumentNullException.ThrowIfNull(tags);

        var found = tags
            .Where(t => TagNames.Contains(t.Key, StringComparer.OrdinalIgnoreCase) || t.Key.StartsWith("lyrics-", StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase)
            .Select(t => Parse(t.Value))
            .Where(l => l is not null)
            .ToList();
        return found.FirstOrDefault(l => l!.Synced) ?? found.FirstOrDefault();
    }

    /// <summary>The sidecar next to a track that shares its name: "01 - News.flac" → "01 - News.lrc", if it exists.</summary>
    public static string? Sidecar(string trackPath, string extension)
    {
        ArgumentNullException.ThrowIfNull(trackPath);
        var path = Path.ChangeExtension(trackPath, extension);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// Parses LRC or plain text. Null when there's nothing to show. An LRC file's tag lines (<c>[ar:…]</c>, <c>[ti:…]</c>)
    /// are dropped, and <c>[offset:ms]</c> moves every line earlier by that much, as players apply it. A line with
    /// several timestamps (a repeated chorus) becomes one line per time. Once any line is timed the untimed ones are
    /// left out, since there's nowhere in the song to put them.
    /// </summary>
    public static Lyrics? Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var timed = new List<LyricLine>();
        var plain = new List<string>();
        var offset = 0.0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim('\r', ' ', '\t', '﻿');
            var position = 0;
            var times = new List<double>();
            for (Match m; (m = Timestamp().Match(line, position)).Success && m.Index == position; position = m.Index + m.Length)
            {
                times.Add(Seconds(m));
            }

            if (times.Count > 0)
            {
                // Enhanced LRC times each word too (<mm:ss.xx>); the words are kept, the marks aren't.
                var content = WordTimestamp().Replace(line[position..], string.Empty).Trim();
                timed.AddRange(times.Select(t => new LyricLine(t, content)));
            }
            else if (TagLine().Match(line) is { Success: true } tag)
            {
                if (tag.Groups["key"].Value.Equals("offset", StringComparison.OrdinalIgnoreCase)
                    && double.TryParse(tag.Groups["value"].Value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms))
                {
                    offset = ms / 1000;
                }
            }
            else
            {
                plain.Add(line);
            }
        }

        if (timed.Count > 0)
        {
            var lines = timed
                .Select(l => l with { Time = Math.Round(Math.Max(0, l.Time!.Value - offset), 3) })
                .OrderBy(l => l.Time)
                .ToList();
            return lines.Any(l => l.Text.Length > 0) ? new Lyrics(true, lines) : null;
        }

        // Plain text keeps its verses apart with one blank line, and starts and ends on words.
        var kept = new List<LyricLine>();
        foreach (var line in plain)
        {
            if (line.Length > 0 || (kept.Count > 0 && kept[^1].Text.Length > 0))
            {
                kept.Add(new LyricLine(null, line));
            }
        }

        while (kept.Count > 0 && kept[^1].Text.Length == 0)
        {
            kept.RemoveAt(kept.Count - 1);
        }

        return kept.Count > 0 ? new Lyrics(false, kept) : null;
    }

    /// <summary>
    /// "[01:02.5]" → 62.5. The fraction may have two digits (hundredths) or three (milliseconds), and some writers
    /// put a colon before it ("[01:02:50]"); LRC has no hours, so a third number is always the fraction.
    /// </summary>
    private static double Seconds(Match m)
    {
        var minutes = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var seconds = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var fraction = m.Groups["f"].Success ? double.Parse("0." + m.Groups["f"].Value, CultureInfo.InvariantCulture) : 0;
        return (minutes * 60) + seconds + fraction;
    }

    [GeneratedRegex(@"\[(?<m>\d{1,3}):(?<s>\d{1,2})(?:[.:](?<f>\d{1,3}))?\]")]
    private static partial Regex Timestamp();

    [GeneratedRegex(@"<\d{1,3}:\d{1,2}(?:[.:]\d{1,3})?>\s*")]
    private static partial Regex WordTimestamp();

    [GeneratedRegex(@"^\[(?<key>[A-Za-z#_]+):(?<value>[^\]]*)\]$")]
    private static partial Regex TagLine();
}
