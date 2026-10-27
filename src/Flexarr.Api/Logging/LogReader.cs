using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Serilog.Events;

namespace Flexarr.Api.Logging;

public sealed record LogQuery(LogEventLevel? Level, string? Area, string? Text, string? Before, int Limit);

public sealed record LogPage(IReadOnlyList<LogEntry> Entries, string? NextCursor);

/// <summary>
/// Pages back through the log files, newest first. A cursor is "file:offset": carry on from the line before that
/// byte. Reads backwards a chunk at a time, so the first page costs the same however large the log is.
/// </summary>
public sealed partial class LogReader(LogPaths paths)
{
    /// <summary>How much one request may read looking for matches before handing back a cursor to continue from.</summary>
    private const long ScanBudget = 32L * 1024 * 1024;

    public LogPage Read(LogQuery query)
    {
        var files = paths.LogFiles();
        var (startFile, startOffset) = ParseCursor(query.Before, files);
        var entries = new List<LogEntry>();
        long scanned = 0;
        for (var i = startFile; i < files.Count; i++)
        {
            var file = files[i];
            using var stream = Open(file.FullName);
            if (stream is null)
            {
                continue;
            }

            var end = i == startFile && startOffset is { } offset ? Math.Min(offset, stream.Length) : stream.Length;
            foreach (var (line, start) in LinesBackwards(stream, end))
            {
                scanned += line.Length + 1;
                if (Matches(line, query, $"{file.Name}:{start}") is { } entry)
                {
                    entries.Add(entry);
                    if (entries.Count >= query.Limit)
                    {
                        return new LogPage(entries, start > 0 || i + 1 < files.Count ? $"{file.Name}:{start}" : null);
                    }
                }

                if (scanned >= ScanBudget)
                {
                    return new LogPage(entries, start > 0 || i + 1 < files.Count ? $"{file.Name}:{start}" : null);
                }
            }
        }

        return new LogPage(entries, null);
    }

    private static LogEntry? Matches(string line, LogQuery query, string id)
    {
        if (query.Text is { Length: > 0 } text && !line.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (LogEntry.Parse(line, id) is not { } entry || (query.Level is { } level && entry.Level < level)
            || (query.Area is { Length: > 0 } area && !entry.Area.Equals(area, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        // The raw line also holds property names and JSON escapes; make sure the text is in something shown.
        return query.Text is not { Length: > 0 } t
            || entry.Message.Contains(t, StringComparison.OrdinalIgnoreCase)
            || entry.Source.Contains(t, StringComparison.OrdinalIgnoreCase)
            || entry.Exception?.Contains(t, StringComparison.OrdinalIgnoreCase) == true
            || entry.Properties.Values.Any(v => v.Contains(t, StringComparison.OrdinalIgnoreCase))
            ? entry
            : null;
    }

    private static (int File, long? Offset) ParseCursor(string? cursor, IReadOnlyList<FileInfo> files)
    {
        if (cursor is null || cursor.LastIndexOf(':') is var colon && colon <= 0
            || !long.TryParse(cursor.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var offset))
        {
            return (0, null);
        }

        var name = cursor[..colon];
        var index = files.ToList().FindIndex(f => f.Name == name);

        // The file has since been deleted: carry on from the next older one.
        return index >= 0 ? (index, offset) : (files.Count(f => string.CompareOrdinal(f.Name, name) > 0), null);
    }

    private static FileStream? Open(string path)
    {
        try
        {
            // Serilog keeps today's file open for writing.
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The lines ending before <paramref name="end"/>, last first, with the offset each starts at.</summary>
    internal static IEnumerable<(string Line, long Start)> LinesBackwards(Stream stream, long end)
    {
        const int ChunkSize = 64 * 1024;
        var buffer = new byte[ChunkSize];
        var carry = new List<byte>();
        var position = end;
        while (position > 0)
        {
            var size = (int)Math.Min(ChunkSize, position);
            position -= size;
            stream.Position = position;
            stream.ReadExactly(buffer, 0, size);
            for (var i = size - 1; i >= 0; i--)
            {
                if (buffer[i] != (byte)'\n')
                {
                    continue;
                }

                // buffer[i+1 .. size) followed by carry is one whole line.
                var line = Decode(buffer.AsSpan(i + 1, size - i - 1), carry);
                carry.Clear();
                size = i;
                if (line.Length > 0)
                {
                    yield return (line, position + i + 1);
                }
            }

            carry.InsertRange(0, buffer.AsSpan(0, size).ToArray());
        }

        if (carry.Count > 0)
        {
            yield return (Encoding.UTF8.GetString([.. carry]).TrimEnd('\r'), 0);
        }
    }

    private static string Decode(ReadOnlySpan<byte> head, List<byte> tail)
    {
        if (tail.Count == 0)
        {
            return Encoding.UTF8.GetString(head).TrimEnd('\r');
        }

        var bytes = new byte[head.Length + tail.Count];
        head.CopyTo(bytes);
        tail.CopyTo(bytes, head.Length);
        return Encoding.UTF8.GetString(bytes).TrimEnd('\r');
    }
}

/// <summary>Where the logs live, and which files are which.</summary>
public sealed partial class LogPaths(string directory)
{
    public const string FilePrefix = "flexarr-";

    public string Directory { get; } = directory;

    /// <summary>Serilog's rolling file name; it puts the date (and _001… once a day's file fills) before the dot.</summary>
    public string FilePattern => Path.Combine(Directory, FilePrefix + ".log");

    public string UploadsDirectory => Path.Combine(Directory, "uploads");

    /// <summary>The server's log files, newest first.</summary>
    public IReadOnlyList<FileInfo> LogFiles()
    {
        var folder = new DirectoryInfo(Directory);
        if (!folder.Exists)
        {
            return [];
        }

        return folder.EnumerateFiles(FilePrefix + "*.log")
            .Select(f => (File: f, Match: LogFileName().Match(f.Name)))
            .Where(f => f.Match.Success)
            .OrderByDescending(f => f.Match.Groups[1].Value, StringComparer.Ordinal)
            .ThenByDescending(f => f.Match.Groups[2].Success ? int.Parse(f.Match.Groups[2].Value, CultureInfo.InvariantCulture) : 0)
            .Select(f => f.File)
            .ToList();
    }

    [GeneratedRegex(@"^flexarr-(\d{8})(?:_(\d{3}))?\.log$")]
    private static partial Regex LogFileName();
}
