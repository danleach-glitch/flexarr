using System.Diagnostics;
using Flexarr.Core.Books;
using Flexarr.Core.Probing;

namespace Flexarr.Core.Tests;

/// <summary>A book's chapters on its whole timeline, from whichever source its files offer.</summary>
public sealed class BookChaptersTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-chapters-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ChapterSourceFile File(string name, double seconds, string? title = null, params ProbedChapter[] chapters) =>
        new($"/b/{name}", seconds, title is null ? null : new Dictionary<string, string> { ["title"] = title }, chapters);

    [Fact]
    public void EmbeddedChapters_OfASingleFile()
    {
        var (chapters, source) = BookChapters.Build(
            [File("book.m4b", 300, null, new(0, 100, "Opening Credits"), new(100, 200, "Chapter 1"), new(200, 300, "Chapter 2"))], null, "Book");

        Assert.Equal(ChapterSource.Embedded, source);
        Assert.Equal([("Opening Credits", 0.0, 100.0), ("Chapter 1", 100.0, 200.0), ("Chapter 2", 200.0, 300.0)],
            chapters.Select(c => (c.Title, c.StartSeconds, c.EndSeconds)));
    }

    [Fact]
    public void EmbeddedChapters_OfSeveralFiles_AreMovedOntoTheBooksTimeline()
    {
        var (chapters, _) = BookChapters.Build(
        [
            File("part1.m4b", 100, null, new(0, 60, "One"), new(60, 100, "Two")),
            File("part2.m4b", 50, "Three"),
        ], null, "Book");

        Assert.Equal([("One", 0.0, 60.0), ("Two", 60.0, 100.0), ("Three", 100.0, 150.0)], chapters.Select(c => (c.Title, c.StartSeconds, c.EndSeconds)));
    }

    [Fact]
    public void UntitledEmbeddedChapters_AreNumbered_AndRunWithoutGaps()
    {
        var (chapters, _) = BookChapters.Build([File("b.m4b", 300, null, new(5, 90, null), new(100, 290, null))], null, "Book");

        Assert.Equal([("Chapter 1", 0.0, 100.0), ("Chapter 2", 100.0, 300.0)], chapters.Select(c => (c.Title, c.StartSeconds, c.EndSeconds)));
    }

    [Fact]
    public void OneChapterPerFile_NamedByTitleTagOrFileName()
    {
        var (chapters, source) = BookChapters.Build(
            [File("1-01 Introduction.mp3", 60), File("1-02 Our Image Of The World.mp3", 90, "Our Image of the World"), File("Track_03.mp3", 30)], null, "Book");

        Assert.Equal(ChapterSource.Files, source);
        Assert.Equal([("Introduction", 0.0), ("Our Image of the World", 60.0), ("Track 03", 150.0)], chapters.Select(c => (c.Title, c.StartSeconds)));
    }

    [Fact]
    public void ACueSheet_ForOneFile()
    {
        const string cue = """
            TITLE "Dune"
            FILE "dune.mp3" MP3
              TRACK 01 AUDIO
                TITLE "Book One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Book Two"
                INDEX 01 61:30:37
            """;

        var (chapters, source) = BookChapters.Build([File("dune.mp3", 7200)], cue, "Dune");

        Assert.Equal(ChapterSource.Cue, source);
        Assert.Equal("Book One", chapters[0].Title);
        Assert.Equal(3690.49, chapters[1].StartSeconds, 2);
        Assert.Equal(7200, chapters[1].EndSeconds);
    }

    [Fact]
    public void ACueSheet_NamingSeveralFiles()
    {
        const string cue = """
            FILE "cd1.mp3" MP3
              TRACK 01 AUDIO
                TITLE "One"
                INDEX 01 00:00:00
              TRACK 02 AUDIO
                TITLE "Two"
                INDEX 01 00:50:00
            FILE "cd2.mp3" MP3
              TRACK 03 AUDIO
                TITLE "Three"
                INDEX 01 00:10:00
            """;

        var (chapters, _) = BookChapters.Build([File("cd1.mp3", 100), File("cd2.mp3", 100)], cue, "Book");

        Assert.Equal([("One", 0.0), ("Two", 50.0), ("Three", 110.0)], chapters.Select(c => (c.Title, c.StartSeconds)));
    }

    [Fact]
    public void ALongSingleFileWithNothingElse_IsSplitIntoParts()
    {
        var (chapters, source) = BookChapters.Build([File("book.mp3", 3 * 3600 + 60)], null, "Book");

        Assert.Equal(ChapterSource.Even, source);
        Assert.Equal(7, chapters.Count);
        Assert.Equal("Part 1", chapters[0].Title);
        Assert.Equal(3 * 3600 + 60, chapters[^1].EndSeconds);
    }

    [Fact]
    public void AShortSingleFile_IsOneChapter()
    {
        var (chapters, _) = BookChapters.Build([File("story.mp3", 1200)], null, "A Short Story");

        Assert.Equal([("A Short Story", 0.0, 1200.0)], chapters.Select(c => (c.Title, c.StartSeconds, c.EndSeconds)));
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_ChaptersAreReadFromAnM4b()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        var metadata = Path.Combine(_dir, "chapters.txt");
        await System.IO.File.WriteAllTextAsync(metadata, """
            ;FFMETADATA1
            album=Test Book
            artist=Test Author
            composer=Test Narrator

            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=2000
            title=Opening

            [CHAPTER]
            TIMEBASE=1/1000
            START=2000
            END=5000
            title=Chapter One
            """);
        var book = Path.Combine(_dir, "book.m4b");
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-i", metadata, "-map_metadata", "1",
            "-map_chapters", "1", "-t", "5", "-c:a", "aac", "-f", "mp4", book);

        var probe = await new FfprobeMediaProbe().ProbeBookFileAsync(book, default);

        Assert.Equal([("Opening", 0.0), ("Chapter One", 2.0)], probe.Chapters.Select(c => (c.Title, Math.Round(c.StartSeconds, 1))));
        var info = BookTags.Read(probe.Tags, singleFile: true);
        Assert.Equal(("Test Book", "Test Author", "Test Narrator"), (info.Title, info.Authors[0], info.Narrators[0]));
    }

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        var start = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"{program} failed: {await errors}");
    }
}
