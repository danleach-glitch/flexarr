using Flexarr.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Flexarr.Core.Tests;

/// <summary>Profiles from before audiobooks get proper listening settings, not the zeros the Audiobooks migration gave them.</summary>
public sealed class AudiobookProfileDefaultsTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-book-defaults-").FullName;
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
    public async Task UpgradedProfiles_ListenAtNormalSpeed_WithThirtySecondSkipsAndSmartRewind()
    {
        var migrator = _db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261101025135_ArtworkStamp");

        // As the Audiobooks migration left them: an untouched profile, and one whose owner has since chosen settings.
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Profiles (Id, Name, Color, CreatedAt, HiddenLibraryIds, AutoPlayNext, SkipIntros, SkipRecaps, SkipCredits,
                SubtitleSize, SubtitlePosition, SubtitlesInBlackBars, PodcastAutoDownload, PodcastKeepEpisodes, PodcastSpeed, PodcastsInSidebar,
                AudiobookSpeed, AudiobookSkipBackSeconds, AudiobookSkipForwardSeconds, AudiobookSmartRewind)
            SELECT 'kid', 'Kid', Color, CreatedAt, HiddenLibraryIds, AutoPlayNext, SkipIntros, SkipRecaps, SkipCredits,
                SubtitleSize, SubtitlePosition, SubtitlesInBlackBars, PodcastAutoDownload, PodcastKeepEpisodes, PodcastSpeed, PodcastsInSidebar,
                0, 0, 0, 0 FROM Profiles WHERE Id = 'owner';
            UPDATE Profiles SET AudiobookSpeed = 1.5, AudiobookSkipBackSeconds = 10, AudiobookSkipForwardSeconds = 45, AudiobookSmartRewind = 0
                WHERE Id = 'owner';
            """);

        await migrator.MigrateAsync();

        var kid = await _db.Profiles.AsNoTracking().SingleAsync(p => p.Id == "kid");
        Assert.Equal((1.0, 30, 30, true), (kid.AudiobookSpeed, kid.AudiobookSkipBackSeconds, kid.AudiobookSkipForwardSeconds, kid.AudiobookSmartRewind));
        var owner = await _db.Profiles.AsNoTracking().SingleAsync(p => p.Id == "owner");
        Assert.Equal((1.5, 10, 45, false), (owner.AudiobookSpeed, owner.AudiobookSkipBackSeconds, owner.AudiobookSkipForwardSeconds, owner.AudiobookSmartRewind));
    }
}
