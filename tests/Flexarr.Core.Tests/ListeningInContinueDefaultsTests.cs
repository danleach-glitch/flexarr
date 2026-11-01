using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flexarr.Core.Tests;

/// <summary>Every profile from before the switch, not just the main one, gets books and podcasts in Continue watching.</summary>
public sealed class ListeningInContinueDefaultsTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-listening-defaults-").FullName;
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
    public async Task UpgradedProfiles_AllHaveListeningInContinueOn()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101060916_AuthorInfo");

        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Profiles (Id, Name, Color, CreatedAt, HiddenLibraryIds, AutoPlayNext, SkipIntros, SkipRecaps, SkipCredits,
                SubtitleSize, SubtitlePosition, SubtitlesInBlackBars, PodcastAutoDownload, PodcastKeepEpisodes, PodcastSpeed, PodcastsInSidebar,
                AudiobookSpeed, AudiobookSkipBackSeconds, AudiobookSkipForwardSeconds, AudiobookSmartRewind)
            SELECT 'kid', 'Kid', Color, CreatedAt, HiddenLibraryIds, AutoPlayNext, SkipIntros, SkipRecaps, SkipCredits,
                SubtitleSize, SubtitlePosition, SubtitlesInBlackBars, PodcastAutoDownload, PodcastKeepEpisodes, PodcastSpeed, PodcastsInSidebar,
                AudiobookSpeed, AudiobookSkipBackSeconds, AudiobookSkipForwardSeconds, AudiobookSmartRewind FROM Profiles WHERE Id = 'owner';
            """);

        await migrator.MigrateAsync();

        var profiles = await _db.Profiles.AsNoTracking().OrderBy(p => p.Id).Select(p => new { p.Id, p.ListeningInContinue }).ToListAsync();
        Assert.Equal(["kid", "owner"], profiles.Select(p => p.Id));
        Assert.All(profiles, p => Assert.True(p.ListeningInContinue));
    }
}
