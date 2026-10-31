using System.Diagnostics;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Probing;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Core.Tests;

/// <summary>
/// A whole scan with the real ffprobe, over books made with ffmpeg: tags, chapters and disc folders as they come out
/// of real files. Without ffmpeg installed (CI), there's nothing to check.
/// </summary>
public sealed class RealAudiobookScanTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-realbooks-").FullName;
    private FlexarrDbContext _db = null!;

    private string Books => Path.Combine(_dir, "books");

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task WhenFfmpegIsInstalled_RealFilesBecomeBooks()
    {
        if (!await HasFfmpegAsync())
        {
            return;
        }

        // An m4b with chapters and tags, in Author/Series/1 - Title.
        var m4b = Path.Combine(Books, "Richard Osman/Thursday Murder Club/1 - The Thursday Murder Club/TMC1.m4b");
        var metadata = Path.Combine(_dir, "meta.txt");
        await File.WriteAllTextAsync(metadata, """
            ;FFMETADATA1
            album=The Thursday Murder Club
            artist=Richard Osman
            composer=Lesley Manville
            date=2020

            [CHAPTER]
            TIMEBASE=1/1000
            START=0
            END=3000
            title=Opening Credits

            [CHAPTER]
            TIMEBASE=1/1000
            START=3000
            END=6000
            title=Chapter 1
            """);
        Directory.CreateDirectory(Path.GetDirectoryName(m4b)!);
        await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-i", metadata, "-map_metadata", "1",
            "-map_chapters", "1", "-t", "6", "-c:a", "aac", "-f", "mp4", m4b);

        // An mp3 book across two disc folders, one chapter per file from the title tags.
        foreach (var (disc, track, title) in new[] { (2, 1, "Part Three"), (1, 1, "Part One"), (1, 2, "Part Two") })
        {
            var mp3 = Path.Combine(Books, $"Alan Watts/Out of Your Mind/CD{disc}/{track:00}.mp3");
            Directory.CreateDirectory(Path.GetDirectoryName(mp3)!);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=220:sample_rate=22050", "-t", "2",
                "-metadata", $"title={title}", "-metadata", "album=Out of Your Mind", "-metadata", "artist=Alan Watts", "-metadata", $"track={track}",
                "-c:a", "libmp3lame", mp3);
        }

        var library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = Books }] };
        _db.Libraries.Add(library);
        await _db.SaveChangesAsync();
        await new LibraryScanner(_db, new FfprobeMediaProbe(), new ScanProgress(), NullLogger<LibraryScanner>.Instance).ScanAsync(library, null, default);

        var tmc = await _db.MediaItems.Include(i => i.Book).SingleAsync(i => i.Title == "The Thursday Murder Club");
        Assert.Equal(2020, tmc.Year);
        Assert.InRange(tmc.Book!.DurationSeconds, 5.5, 6.5);
        Assert.Equal(ChapterSource.Embedded, tmc.Book.ChapterSource);
        Assert.Equal(["Opening Credits", "Chapter 1"], await _db.MediaChapters.Where(c => c.MediaItemId == tmc.Id).OrderBy(c => c.Index).Select(c => c.Title).ToListAsync());
        Assert.Equal("1", (await _db.BookSeriesEntries.Include(e => e.Series).SingleAsync(e => e.BookId == tmc.Id)).Sequence);
        Assert.Contains(await _db.Credits.Where(c => c.MediaItemId == tmc.Id && c.Role == CreditRole.Narrator).Select(c => c.Person!.Name).ToListAsync(), n => n == "Lesley Manville");

        var watts = await _db.MediaItems.SingleAsync(i => i.Title == "Out of Your Mind");
        Assert.Equal(["Part One", "Part Two", "Part Three"], await _db.MediaChapters.Where(c => c.MediaItemId == watts.Id).OrderBy(c => c.Index).Select(c => c.Title).ToListAsync());
        Assert.Equal(["CD1/01.mp3", "CD1/02.mp3", "CD2/01.mp3"], (await _db.MediaFiles.Where(f => f.MediaItemId == watts.Id).OrderBy(f => f.BookOrder).Select(f => f.Path).ToListAsync())
            .Select(p => Path.GetRelativePath(Path.Combine(Books, "Alan Watts/Out of Your Mind"), p)));
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
