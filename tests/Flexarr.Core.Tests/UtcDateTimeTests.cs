using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

public sealed class UtcDateTimeTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-utc-").FullName;

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
        return Task.CompletedTask;
    }

    private FlexarrDbContext Open() => new(new DbContextOptionsBuilder<FlexarrDbContext>()
        .UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);

    [Fact]
    public async Task DatesComeBackAsUtc_SoTheyGoOutWithTheirZone()
    {
        var scanned = DateTime.UtcNow;
        await using (var db = Open())
        {
            await db.Database.MigrateAsync();
            db.Libraries.Add(new Library { Name = "Films", Type = LibraryType.Movies, LastScannedAt = scanned, Folders = [new LibraryFolder { Path = "/films" }] });
            await db.SaveChangesAsync();
        }

        await using (var db = Open())
        {
            var library = await db.Libraries.SingleAsync();
            Assert.Equal(DateTimeKind.Utc, library.CreatedAt.Kind);
            Assert.Equal(DateTimeKind.Utc, library.LastScannedAt!.Value.Kind);
            Assert.Equal(scanned, library.LastScannedAt.Value);
        }
    }
}
