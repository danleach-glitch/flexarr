using Flexarr.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flexarr.Core.Tests;

/// <summary>
/// Dev builds connected to Readarr, Bookshelf or Chaptarr. Dropping that: book requests go, books matched there and
/// authors whose photo or biography came from there are looked up again, and locked authors are left alone.
/// </summary>
public sealed class RemoveReadarrMigrationTests : IAsyncLifetime
{
    private const string Before = "20261101112648_DropFormatGenres";

    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-remove-readarr-").FullName;
    private FlexarrDbContext _db = null!;

    public Task InitializeAsync()
    {
        _db = new(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task ReadarrData_IsCleared_AndLookedUpAgain()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync(Before);

        // The columns this migration drops aren't in today's model, so the old schema is seeded by hand.
        var library = Guid.NewGuid();
        var (matched, plain, byLocked, second) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var (fromArr, locked, audible) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var then = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);
        await InsertAsync("Libraries", new() { ["Id"] = library, ["Name"] = "Books", ["Type"] = "Audiobooks" });
        foreach (var (id, title, source) in new[] { (matched, "Dracula", "Bookshelf"), (plain, "Emma", "Audible"), (byLocked, "Persuasion", "Audible"), (second, "Lady Susan", "Audible") })
        {
            await InsertAsync("MediaItems", new()
            {
                ["Id"] = id, ["LibraryId"] = library, ["Kind"] = "Audiobook", ["Title"] = title, ["SortTitle"] = title.ToLowerInvariant(),
                ["MetadataSource"] = source, ["MetadataUpdatedAt"] = then,
            });
            await InsertAsync("BookDetails", new() { ["MediaItemId"] = id, ["ArrBookId"] = id == matched ? "12" : null, ["ArrInstance"] = id == matched ? "Bookshelf" : null });
        }

        // The author's book with the lower id is missing from disk: the refresh never sees it, so the other is picked.
        var (gone, there) = string.CompareOrdinal(plain.ToString().ToUpperInvariant(), second.ToString().ToUpperInvariant()) < 0 ? (plain, second) : (second, plain);
        await _db.Database.ExecuteSqlRawAsync("UPDATE MediaItems SET MissingSince = '2026-10-31 12:00:00' WHERE Id = {0}", gone.ToString().ToUpperInvariant());

        await InsertAsync("People", new() { ["Id"] = fromArr, ["Name"] = "Bram Stoker", ["ImageUrl"] = "https://images.gr-assets.com/a.jpg", ["PhotoSource"] = "readarr",
            ["Biography"] = "From Goodreads.", ["BiographySource"] = "readarr", ["InfoCheckedAt"] = then });
        await InsertAsync("People", new() { ["Id"] = locked, ["Name"] = "Jane Austen", ["ImageUrl"] = "https://images.gr-assets.com/b.jpg", ["PhotoSource"] = "readarr",
            ["InfoLocked"] = true, ["InfoCheckedAt"] = then });
        await InsertAsync("People", new() { ["Id"] = audible, ["Name"] = "Someone Else", ["ImageUrl"] = "https://m.media-amazon.com/images/I/c.jpg", ["PhotoSource"] = "audible",
            ["Biography"] = "From Audible.", ["BiographySource"] = "audible", ["InfoCheckedAt"] = then });
        await InsertAsync("Credits", new() { ["MediaItemId"] = plain, ["PersonId"] = fromArr, ["Role"] = "Author", ["Order"] = 0 });
        await InsertAsync("Credits", new() { ["MediaItemId"] = second, ["PersonId"] = fromArr, ["Role"] = "Author", ["Order"] = 0 });
        await InsertAsync("Credits", new() { ["MediaItemId"] = byLocked, ["PersonId"] = locked, ["Role"] = "Author", ["Order"] = 0 });

        var profile = await _db.Database.SqlQueryRaw<string>("SELECT Id AS Value FROM Profiles LIMIT 1").FirstOrDefaultAsync();
        if (profile is null)
        {
            profile = "p1";
            await InsertAsync("Profiles", new() { ["Id"] = profile, ["Name"] = "Main" });
        }

        await InsertAsync("MediaRequests", new() { ["Id"] = Guid.NewGuid(), ["ProfileId"] = profile, ["Kind"] = "Audiobook", ["TmdbId"] = 0, ["BookArrId"] = "12" });
        await InsertAsync("MediaRequests", new() { ["Id"] = Guid.NewGuid(), ["ProfileId"] = profile, ["Kind"] = "Movie", ["TmdbId"] = 603 });

        await migrator.MigrateAsync();

        Assert.Equal(["Movie"], (await _db.MediaRequests.AsNoTracking().ToListAsync()).Select(r => r.Kind.ToString()));

        var books = await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(i => i.Title);
        Assert.Null(books["Dracula"].MetadataSource); // its source was the app
        Assert.Null(books["Dracula"].MetadataUpdatedAt);
        // One of the author's books is due, so they're looked up again; the other isn't looked up online for nothing.
        var due = await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.MetadataUpdatedAt == null).Select(i => i.Id).ToListAsync();
        Assert.Contains(there, due);
        Assert.DoesNotContain(gone, due);
        Assert.Equal("Audible", books["Emma"].MetadataSource);
        Assert.NotNull(books["Persuasion"].MetadataUpdatedAt); // a locked author's book isn't

        var people = await _db.People.AsNoTracking().ToDictionaryAsync(p => p.Name);
        var stoker = people["Bram Stoker"];
        Assert.True(stoker is { ImageUrl: null, PhotoSource: null, Biography: null, BiographySource: null, InfoCheckedAt: null });
        Assert.NotNull(stoker.ImageChangedAt);
        Assert.Equal("readarr", people["Jane Austen"].PhotoSource); // locked: left alone
        Assert.Equal("https://images.gr-assets.com/b.jpg", people["Jane Austen"].ImageUrl);
        Assert.Equal("audible", people["Someone Else"].PhotoSource);
        Assert.Equal("From Audible.", people["Someone Else"].Biography);
    }

    /// <summary>A row with the values given; other required columns get an empty value of their type.</summary>
    private async Task InsertAsync(string table, Dictionary<string, object?> values)
    {
        var connection = (SqliteConnection)_db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        var columns = new List<(string Name, string Type)>();
        await using (var info = connection.CreateCommand())
        {
            info.CommandText = $"SELECT name, type, \"notnull\", dflt_value, pk FROM pragma_table_info('{table}')";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var required = reader.GetInt64(2) == 1 && reader.IsDBNull(3) && reader.GetInt64(4) == 0;
                if (required && !values.ContainsKey(reader.GetString(0)))
                {
                    columns.Add((reader.GetString(0), reader.GetString(1)));
                }
            }
        }

        foreach (var (name, type) in columns)
        {
            values[name] = type.Contains("INT", StringComparison.OrdinalIgnoreCase) ? 0L
                : type.Contains("REAL", StringComparison.OrdinalIgnoreCase) ? 0.0
                : name.EndsWith("At", StringComparison.Ordinal) ? DateTime.UnixEpoch // a time (AddedAt, RequestedAt…)
                : string.Empty;
        }

        await using var command = connection.CreateCommand();
        command.CommandText = $"INSERT INTO \"{table}\" ({string.Join(", ", values.Keys.Select(k => $"\"{k}\""))}) VALUES ({string.Join(", ", values.Keys.Select((_, i) => $"$p{i}"))})";
        var n = 0;
        foreach (var value in values.Values)
        {
            command.Parameters.AddWithValue($"$p{n++}", value switch
            {
                null => DBNull.Value,
                Guid g => g.ToString().ToUpperInvariant(),
                DateTime d => d.ToString("yyyy-MM-dd HH:mm:ss.FFFFFFF", System.Globalization.CultureInfo.InvariantCulture),
                bool b => b ? 1 : 0,
                _ => value,
            });
        }

        await command.ExecuteNonQueryAsync();
    }
}
