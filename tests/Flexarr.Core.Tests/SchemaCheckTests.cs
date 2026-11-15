using Flexarr.Core.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

/// <summary>An older Flexarr won't start on a database a newer one has migrated.</summary>
public sealed class SchemaCheckTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-schema-check-").FullName;
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
    public void Unknown_ListsOnlyMigrationsLaterThanThisBuildsLast()
    {
        Assert.Empty(SchemaCheck.Unknown(["1_A", "2_B"], ["1_A", "2_B", "3_C"]));
        Assert.Equal(["4_D", "5_E"], SchemaCheck.Unknown(["1_A", "5_E", "4_D"], ["1_A", "2_B"]));
        // Renamed before release: the old name is still in a dev database's history, and is older than the build's last.
        Assert.Empty(SchemaCheck.Unknown(["1_A", "2_Renamed", "3_C"], ["1_A", "2_B", "3_C"]));
    }

    [Fact]
    public async Task NewAndCurrentDatabases_Start()
    {
        // A new database has no history yet.
        await SchemaCheck.EnsureNotNewerAsync(_db, CancellationToken.None);
        await _db.Database.MigrateAsync();
        await SchemaCheck.EnsureNotNewerAsync(_db, CancellationToken.None);
    }

    [Fact]
    public async Task ADatabaseANewerServerMigrated_StopsStartup()
    {
        await _db.Database.MigrateAsync();
        await _db.Database.ExecuteSqlRawAsync(
            "INSERT INTO __EFMigrationsHistory (MigrationId, ProductVersion) VALUES ('29991231000000_FromTheFuture', '10.0.0')");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => SchemaCheck.EnsureNotNewerAsync(_db, CancellationToken.None));
        Assert.Contains("29991231000000_FromTheFuture", error.Message, StringComparison.Ordinal);
    }
}
