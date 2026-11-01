using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flexarr.Core.Tests;

/// <summary>Profiles with a podcast speed and a book speed come out of the upgrade with one, the one their owner chose.</summary>
public sealed class ShareListeningSpeedTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-share-speed-").FullName;
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

    [Theory]
    [InlineData(1.5, 1.0, 1.5)] // podcasts chosen, books not
    [InlineData(1.0, 1.25, 1.25)] // books chosen, podcasts not
    [InlineData(1.3, 2.0, 1.3)] // both chosen: podcasts came first
    [InlineData(4.0, 1.0, 3.5)] // a podcast speed books can't play at
    [InlineData(1.0, 1.0, 1.0)]
    public async Task OneSpeed_TheChosenOne(double podcast, double book, double shared)
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101082228_ListeningInContinue");
        await _db.Database.ExecuteSqlAsync($"UPDATE Profiles SET PodcastSpeed = {podcast}, AudiobookSpeed = {book} WHERE Id = 'owner'");

        await migrator.MigrateAsync();

        var owner = await _db.Profiles.AsNoTracking().SingleAsync(p => p.Id == "owner");
        Assert.Equal(shared, owner.AudiobookSpeed, 3);
        Assert.Equal(podcast is not 1.0 ? podcast : shared, owner.PodcastSpeed, 3);
    }
}
