using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

public sealed class SqliteTuningTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"flexarr-tuning-{Guid.NewGuid():N}.db");
    private readonly FlexarrDbContext _db;

    public SqliteTuningTests()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={_path}").Options);
        _db.Database.Migrate();
        _db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task EachConnection_SyncsOnlyAtCheckpoints()
    {
        // 1 is NORMAL; SQLite's own default is 2, FULL.
        Assert.Equal(1, await _db.Database.SqlQueryRaw<int>("SELECT synchronous AS Value FROM pragma_synchronous").SingleAsync());
    }
}
