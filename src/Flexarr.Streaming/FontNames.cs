using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Flexarr.Core.Media;

namespace Flexarr.Streaming;

/// <summary>
/// The names a font answers to, and the fonts a styled subtitle script asks for, so a player can fetch only the
/// attached fonts a track uses (audit perf-66). libass picks a font by its family, full or PostScript name, ignoring case.
/// </summary>
public static partial class FontNames
{
    /// <summary>How many attached fonts are written out at once to read their names.</summary>
    private const int ReadAtOnce = 4;

    /// <summary>
    /// Of a file's <paramref name="fonts"/>, those <paramref name="script"/> asks for by name. Kept whenever it can't
    /// tell: all of them when the script names none, and any font whose own names can't be read.
    /// </summary>
    public static async Task<IReadOnlyList<AttachedFont>> UsedByAsync(MediaFile file, IReadOnlyList<AttachedFont> fonts, string script,
        TranscodeSettings settings, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(fonts);
        var wanted = InScript(script);
        if (wanted.Count == 0 || fonts.Count == 0)
        {
            return fonts;
        }

        var keep = new bool[fonts.Count];
        await Parallel.ForAsync(0, fonts.Count, new ParallelOptions { MaxDegreeOfParallelism = ReadAtOnce, CancellationToken = ct }, async (i, token) =>
        {
            var path = await SubtitleFonts.ExtractAsync(file, fonts[i], settings, token).ConfigureAwait(false);
            var names = path is null ? null : Read(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
            keep[i] = names is null || names.Overlaps(wanted);
        }).ConfigureAwait(false);
        return [.. fonts.Where((_, i) => keep[i])];
    }

    /// <summary>Family (1), full (4), PostScript (6) and typographic family (16) names, from the OpenType name table.</summary>
    private static readonly HashSet<ushort> NameIds = [1, 4, 6, 16];

    /// <summary>
    /// The names in a TrueType or OpenType font, or every font of a collection; null when they can't be read (a WOFF,
    /// a damaged file), so the caller can keep that font rather than guess.
    /// </summary>
    public static HashSet<string>? Read(ReadOnlySpan<byte> font)
    {
        try
        {
            if (font.Length < 12)
            {
                return null;
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (font[..4].SequenceEqual("ttcf"u8))
            {
                var count = BinaryPrimitives.ReadUInt32BigEndian(font[8..]);
                if (count is 0 or > 256)
                {
                    return null;
                }

                for (var i = 0; i < count; i++)
                {
                    if (!ReadOne(font, (int)BinaryPrimitives.ReadUInt32BigEndian(font[(12 + (4 * i))..]), names))
                    {
                        return null;
                    }
                }
            }
            else if (!ReadOne(font, 0, names))
            {
                return null;
            }

            return names.Count == 0 ? null : names;
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    /// <summary>One font's names, its table directory at <paramref name="start"/>; false when it isn't a font this reads.</summary>
    private static bool ReadOne(ReadOnlySpan<byte> font, int start, HashSet<string> names)
    {
        var version = BinaryPrimitives.ReadUInt32BigEndian(font[start..]);
        // TrueType (1.0 or 'true'), or CFF ('OTTO').
        if (version is not (0x00010000 or 0x74727565 or 0x4F54544F))
        {
            return false;
        }

        var tables = BinaryPrimitives.ReadUInt16BigEndian(font[(start + 4)..]);
        for (var t = 0; t < tables; t++)
        {
            var record = font[(start + 12 + (16 * t))..];
            if (!record[..4].SequenceEqual("name"u8))
            {
                continue;
            }

            var table = (int)BinaryPrimitives.ReadUInt32BigEndian(record[8..]);
            var count = BinaryPrimitives.ReadUInt16BigEndian(font[(table + 2)..]);
            var strings = table + BinaryPrimitives.ReadUInt16BigEndian(font[(table + 4)..]);
            for (var r = 0; r < count; r++)
            {
                var entry = font[(table + 6 + (12 * r))..];
                var platform = BinaryPrimitives.ReadUInt16BigEndian(entry);
                var encoding = BinaryPrimitives.ReadUInt16BigEndian(entry[2..]);
                var nameId = BinaryPrimitives.ReadUInt16BigEndian(entry[6..]);
                var length = BinaryPrimitives.ReadUInt16BigEndian(entry[8..]);
                var offset = BinaryPrimitives.ReadUInt16BigEndian(entry[10..]);
                if (!NameIds.Contains(nameId))
                {
                    continue;
                }

                var bytes = font.Slice(strings + offset, length);
                var text = platform switch
                {
                    0 or 3 => Encoding.BigEndianUnicode.GetString(bytes),
                    1 when encoding == 0 => Encoding.Latin1.GetString(bytes),
                    _ => null,
                };
                if (text?.Trim() is { Length: > 0 } name)
                {
                    names.Add(name);
                }
            }

            return true;
        }

        return false;
    }

    /// <summary>
    /// The font names an ASS/SSA script asks for: each style's Fontname and every \fn override in its lines, without
    /// the "@" that asks for a font set vertically.
    /// </summary>
    public static HashSet<string> InScript(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fontColumn = 1;
        var inStyles = false;
        foreach (var raw in script.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inStyles = line.Contains("Styles", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (inStyles && line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var columns = line["Format:".Length..].Split(',').Select(c => c.Trim()).ToList();
                var found = columns.FindIndex(c => c.Equals("Fontname", StringComparison.OrdinalIgnoreCase));
                fontColumn = found < 0 ? 1 : found;
            }
            else if (inStyles && line.StartsWith("Style:", StringComparison.OrdinalIgnoreCase))
            {
                var fields = line["Style:".Length..].Split(',');
                if (fontColumn < fields.Length)
                {
                    Add(names, fields[fontColumn]);
                }
            }
            else if (line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
            {
                foreach (Match match in FontOverride().Matches(line))
                {
                    Add(names, match.Groups[1].Value);
                }
            }
        }

        return names;
    }

    private static void Add(HashSet<string> names, string name)
    {
        var trimmed = name.Trim().TrimStart('@').Trim();
        if (trimmed.Length > 0)
        {
            names.Add(trimmed);
        }
    }

    /// <summary>\fn and the name after it, up to the next tag or the end of the block.</summary>
    [GeneratedRegex(@"\\fn([^\\}]*)")]
    private static partial Regex FontOverride();
}
