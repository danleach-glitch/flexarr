using System.Globalization;
using System.Text.RegularExpressions;
using Flexarr.Core.Probing;

namespace Flexarr.Core.Books;

/// <summary>A chapter on a book's whole timeline.</summary>
public sealed record BookChapter(string Title, double StartSeconds, double EndSeconds);

/// <summary>One of a book's files, in playing order, with its length and what reading it found.</summary>
public sealed record ChapterSourceFile(string Path, double DurationSeconds, IReadOnlyDictionary<string, string>? Tags, IReadOnlyList<ProbedChapter> Chapters);

/// <summary>
/// Works out a book's chapters from its files, taking the first of: chapters inside the files, a <c>.cue</c> sheet,
/// one chapter per file, and evenly spaced parts of a long single file. Times are on the whole book's timeline.
/// </summary>
public static partial class BookChapters
{
    /// <summary>A single file longer than this, with nothing else to go on, is split into parts of <see cref="PartLength"/>.</summary>
    public static readonly double LongFile = TimeSpan.FromHours(1).TotalSeconds;

    public static readonly double PartLength = TimeSpan.FromMinutes(30).TotalSeconds;

    /// <param name="files">The book's files in playing order.</param>
    /// <param name="cueSheet">The text of a .cue sheet beside them, if there's one.</param>
    /// <param name="bookTitle">Names the only chapter of a short single-file book.</param>
    public static (IReadOnlyList<BookChapter> Chapters, ChapterSource Source) Build(IReadOnlyList<ChapterSourceFile> files, string? cueSheet, string bookTitle)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return ([], ChapterSource.None);
        }

        var offsets = new double[files.Count];
        for (var i = 1; i < files.Count; i++)
        {
            offsets[i] = offsets[i - 1] + files[i - 1].DurationSeconds;
        }

        var total = offsets[^1] + files[^1].DurationSeconds;

        // Inside the files: a single file's own list, or each file's (a file without any is a chapter of its own).
        var embedded = files.Sum(f => f.Chapters.Count);
        if (embedded > 1 || (embedded == 1 && files.Count > 1))
        {
            var chapters = new List<BookChapter>();
            for (var i = 0; i < files.Count; i++)
            {
                var file = files[i];
                if (file.Chapters.Count == 0)
                {
                    chapters.Add(new BookChapter(FileTitle(file, i), offsets[i], offsets[i] + file.DurationSeconds));
                    continue;
                }

                for (var c = 0; c < file.Chapters.Count; c++)
                {
                    var chapter = file.Chapters[c];
                    var end = Math.Min(chapter.EndSeconds, file.DurationSeconds > 0 ? file.DurationSeconds : chapter.EndSeconds);
                    chapters.Add(new BookChapter(chapter.Title ?? $"Chapter {chapters.Count + 1}", offsets[i] + chapter.StartSeconds, offsets[i] + end));
                }
            }

            return (Tidy(chapters, total), ChapterSource.Embedded);
        }

        if (cueSheet is not null && FromCue(cueSheet, files, offsets) is { Count: > 1 } cue)
        {
            return (Tidy(cue, total), ChapterSource.Cue);
        }

        if (files.Count > 1)
        {
            return ([.. files.Select((f, i) => new BookChapter(FileTitle(f, i), offsets[i], offsets[i] + f.DurationSeconds))], ChapterSource.Files);
        }

        if (total > LongFile)
        {
            var parts = (int)Math.Ceiling(total / PartLength);
            var length = total / parts;
            return ([.. Enumerable.Range(0, parts).Select(p => new BookChapter($"Part {p + 1}", p * length, p == parts - 1 ? total : (p + 1) * length))], ChapterSource.Even);
        }

        return ([new BookChapter(bookTitle, 0, total)], ChapterSource.Files);
    }

    /// <summary>
    /// Sorted, with each chapter running to the next one's start and the last to the end of the book, so the
    /// chapters cover the whole timeline without gaps or overlaps.
    /// </summary>
    private static List<BookChapter> Tidy(List<BookChapter> chapters, double total)
    {
        var sorted = chapters.Where(c => c.StartSeconds < total || total <= 0).OrderBy(c => c.StartSeconds).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var end = i + 1 < sorted.Count ? sorted[i + 1].StartSeconds : Math.Max(total, sorted[i].EndSeconds);
            sorted[i] = sorted[i] with { EndSeconds = end };
        }

        if (sorted.Count > 0 && sorted[0].StartSeconds > 0)
        {
            sorted[0] = sorted[0] with { StartSeconds = 0 };
        }

        return sorted;
    }

    /// <summary>A file's chapter title: its title tag, or its name without track numbers ("1-01 Introduction" → "Introduction").</summary>
    private static string FileTitle(ChapterSourceFile file, int index)
    {
        if (file.Tags is not null && file.Tags.TryGetValue("title", out var title) && !string.IsNullOrWhiteSpace(title))
        {
            return title.Trim();
        }

        var name = LeadingNumbers().Replace(Path.GetFileNameWithoutExtension(file.Path), "").Replace('_', ' ').Trim();
        return name.Length > 0 ? name : $"Chapter {index + 1}";
    }

    /// <summary>
    /// The tracks of a .cue sheet as chapters. A sheet naming several files places each file's tracks at that file's
    /// start; a sheet for one file (or whose names don't match) is read against the first.
    /// </summary>
    internal static List<BookChapter>? FromCue(string cue, IReadOnlyList<ChapterSourceFile> files, IReadOnlyList<double> offsets)
    {
        var chapters = new List<BookChapter>();
        var fileIndex = 0;
        string? title = null;
        var tracks = 0;
        foreach (var raw in cue.Split('\n'))
        {
            var line = raw.Trim();
            if (CueFile().Match(line) is { Success: true } f)
            {
                var name = Path.GetFileName(f.Groups["name"].Value.Replace('\\', '/'));
                var found = files.Select((x, i) => (x, i)).FirstOrDefault(p => string.Equals(Path.GetFileName(p.x.Path), name, StringComparison.OrdinalIgnoreCase));
                fileIndex = found.x is not null ? found.i : fileIndex;
            }
            else if (line.StartsWith("TRACK ", StringComparison.OrdinalIgnoreCase))
            {
                tracks++;
                title = null;
            }
            else if (tracks > 0 && CueTitle().Match(line) is { Success: true } t)
            {
                title = t.Groups["title"].Value.Trim();
            }
            else if (tracks > 0 && CueIndex().Match(line) is { Success: true } x)
            {
                var seconds = int.Parse(x.Groups["m"].Value, CultureInfo.InvariantCulture) * 60
                    + int.Parse(x.Groups["s"].Value, CultureInfo.InvariantCulture)
                    + (int.Parse(x.Groups["f"].Value, CultureInfo.InvariantCulture) / 75.0);
                chapters.Add(new BookChapter(title ?? $"Chapter {chapters.Count + 1}", offsets[fileIndex] + seconds, 0));
            }
        }

        return chapters.Count > 0 ? chapters : null;
    }

    [GeneratedRegex(@"^\s*(?:(?:cd|disc)\s*\d+\s*[-_. ]\s*)?\d{1,3}(?:[-_.]\d{1,3})?(?:\s*[-_.]\s*|\s+)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingNumbers();

    [GeneratedRegex("^FILE\\s+\"(?<name>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex CueFile();

    [GeneratedRegex("^TITLE\\s+\"?(?<title>[^\"]*)\"?", RegexOptions.IgnoreCase)]
    private static partial Regex CueTitle();

    [GeneratedRegex(@"^INDEX\s+01\s+(?<m>\d+):(?<s>\d{2}):(?<f>\d{2})", RegexOptions.IgnoreCase)]
    private static partial Regex CueIndex();
}
