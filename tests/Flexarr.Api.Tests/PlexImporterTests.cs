using Flexarr.Api.Import;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Flexarr.Api.Tests;

public sealed class PlexImporterTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-plex-test-").FullName;
    private FlexarrDbContext _db = null!;
    private string PlexPath => Path.Combine(_dir, "com.plexapp.plugins.library.db");

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "flexarr.db")}").Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task ImportsWatchedAndInProgress_ByFilePath()
    {
        var watched = await AddEpisodeAsync("/tv/Show/S01E01.mkv");
        var resuming = await AddEpisodeAsync("/tv/Show/S01E02.mkv");
        var newerHere = await AddEpisodeAsync("/tv/Show/S01E03.mkv");
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = newerHere, PositionSeconds = 300, LastPlayedAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        CreatePlexDatabase(
            ("/tv/Show/S01E01.mkv", Views: 2, OffsetMs: 0L, Account: 1),
            ("/tv/Show/S01E02.mkv", Views: 0, OffsetMs: 754_000L, Account: 1),
            ("/tv/Show/S01E03.mkv", Views: 1, OffsetMs: 0L, Account: 1),
            ("/tv/Show/S01E04-not-in-flexarr.mkv", Views: 1, OffsetMs: 0L, Account: 1),
            ("/tv/Show/S01E01.mkv", Views: 5, OffsetMs: 0L, Account: 2));

        var source = await PlexImporter.DescribeAsync(PlexPath, default);
        var result = await new PlexImporter(_db, [], NullLogger<PlexImporter>.Instance).ImportAsync(new PlexImportRequest(1, PlexPath), default);

        Assert.Equal([1L, 2L], source.Accounts.Select(a => a.Id).Order());
        Assert.Equal(new PlexImportResult(Matched: 3, Watched: 1, InProgress: 1, Unmatched: 1, KeptNewer: 1), result);

        var states = await _db.PlaybackStates.AsNoTracking().ToDictionaryAsync(p => p.MediaItemId);
        Assert.True(states[watched].Watched);
        Assert.Equal(2, states[watched].PlayCount);
        Assert.Equal((false, 754d), (states[resuming].Watched, states[resuming].PositionSeconds));
        Assert.Equal(300, states[newerHere].PositionSeconds);
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("relative/com.plexapp.plugins.library.db")]
    [InlineData("/tmp/not-com.plexapp.plugins.library.db")]
    public async Task OnlyReadsPlexsOwnDatabaseFile(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => PlexImporter.DescribeAsync(path, default));
        await Assert.ThrowsAsync<ArgumentException>(() => new PlexImporter(_db, [], NullLogger<PlexImporter>.Instance).ImportAsync(new PlexImportRequest(1, path), default));
    }

    private async Task<Guid> AddEpisodeAsync(string path)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync() ?? _db.Libraries.Add(new Library { Name = "TV", Type = LibraryType.TvShows }).Entity;
        var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Episode, Title = path, SortTitle = path, Files = [new MediaFile { Path = path, DurationSeconds = 1800 }] };
        _db.MediaItems.Add(item);
        await _db.SaveChangesAsync();
        return item.Id;
    }

    /// <summary>The handful of Plex tables the importer reads, with Plex's column names.</summary>
    private void CreatePlexDatabase(params (string File, int Views, long OffsetMs, int Account)[] rows)
    {
        using var connection = new SqliteConnection($"Data Source={PlexPath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE accounts (id INTEGER PRIMARY KEY, name TEXT);
            CREATE TABLE metadata_items (id INTEGER PRIMARY KEY, guid TEXT);
            CREATE TABLE media_items (id INTEGER PRIMARY KEY, metadata_item_id INTEGER);
            CREATE TABLE media_parts (id INTEGER PRIMARY KEY, media_item_id INTEGER, file TEXT);
            CREATE TABLE metadata_item_settings (id INTEGER PRIMARY KEY, account_id INTEGER, guid TEXT, view_count INTEGER, view_offset INTEGER, last_viewed_at INTEGER);
            INSERT INTO accounts VALUES (1, 'dan'), (2, '');
            """;
        command.ExecuteNonQuery();

        var files = rows.Select(r => r.File).Distinct().ToList();
        for (var i = 0; i < files.Count; i++)
        {
            Exec(connection, $"INSERT INTO metadata_items VALUES ({i + 1}, 'plex://episode/{i}'); INSERT INTO media_items VALUES ({i + 1}, {i + 1}); INSERT INTO media_parts VALUES ({i + 1}, {i + 1}, '{files[i]}');");
        }

        foreach (var row in rows)
        {
            Exec(connection, $"INSERT INTO metadata_item_settings (account_id, guid, view_count, view_offset, last_viewed_at) VALUES ({row.Account}, 'plex://episode/{files.IndexOf(row.File)}', {row.Views}, {row.OffsetMs}, 1700000000);");
        }
    }

    private static void Exec(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
