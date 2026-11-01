using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flexarr.Core.Tests;

/// <summary>Books lose the genres that only name the format, in any spelling; films keep theirs.</summary>
public sealed class DropFormatGenresTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-format-genres-").FullName;
    private FlexarrDbContext _db = null!;

    public Task InitializeAsync()
    {
        _db = new(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task FormatGenres_LeaveBooks_InAnySpelling_AndUnusedOnesGo()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101082228_ListeningInContinue");

        // The schema is the same before and after (the migration only changes data), so today's model can seed it.
        Genre G(string name) => new() { Name = name };
        var (audiobook, unabridged, spoken, speech, mystery) = (G("Audiobook"), G("Audiobooks - Unabridged"), G("Spoken-Word"), G("Speech"), G("Mystery"));
        var books = new Library { Name = "Books", Type = LibraryType.Audiobooks };
        var films = new Library { Name = "Films", Type = LibraryType.Movies };
        var book = new MediaItem
        {
            LibraryId = books.Id, Kind = MediaKind.Audiobook, Title = "Dracula", SortTitle = "dracula", Book = new BookDetails { DurationSeconds = 60 },
            Genres = [audiobook, unabridged, spoken, mystery],
        };
        var film = new MediaItem { LibraryId = films.Id, Kind = MediaKind.Movie, Title = "The King's Speech", SortTitle = "kings speech", Genres = [speech] };
        _db.AddRange(books, films, book, film);
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        await migrator.MigrateAsync();

        var left = await _db.MediaItems.AsNoTracking().Include(i => i.Genres).ToDictionaryAsync(i => i.Title, i => i.Genres.Select(g => g.Name).ToList());
        Assert.Equal(["Mystery"], left["Dracula"]);
        Assert.Equal(["Speech"], left["The King's Speech"]); // a film's genre, kept
        Assert.Equal(["Mystery", "Speech"], await _db.Genres.Select(g => g.Name).OrderBy(n => n).ToListAsync());
        Assert.True(BookGenres.IsFormat("Audiobooks - Unabridged") && BookGenres.IsFormat("Spoken-Word")); // the same words as the scan
    }
}
