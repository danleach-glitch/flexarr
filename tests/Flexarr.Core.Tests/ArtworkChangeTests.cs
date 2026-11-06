using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

/// <summary>
/// <see cref="MediaItem.ArtworkChangedAt"/>, which artwork addresses carry, is kept by the save itself: it moves on
/// when a title's artwork does (and for the titles that borrow it), and not when the title is only looked up again.
/// </summary>
public sealed class ArtworkChangeTests : IAsyncLifetime
{
    private static readonly DateTime LastMonth = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime LastWeek = new(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-artwork-").FullName;
    private FlexarrDbContext _db = null!;
    private Library _library = null!;

    public async Task InitializeAsync()
    {
        _db = Open();
        await _db.Database.MigrateAsync();
        _library = new Library { Name = "Shows", Type = LibraryType.TvShows };
        _db.Libraries.Add(_library);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private FlexarrDbContext Open() =>
        new(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);

    private MediaItem Item(MediaKind kind, string title, Guid? parent = null) =>
        new() { LibraryId = _library.Id, Kind = kind, Title = title, SortTitle = title, ParentId = parent };

    /// <summary>A show, a season and an episode as they are in the database now, read afresh.</summary>
    private async Task<MediaItem> ReadAsync(Guid id)
    {
        await using var db = Open();
        return await db.MediaItems.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    [Fact]
    public async Task ATitleLookedUpAgain_KeepsWhenItsArtworkChanged()
    {
        var show = Item(MediaKind.Series, "Severance");
        show.PosterUrl = "https://image.test/severance.jpg";
        show.MetadataUpdatedAt = LastMonth;
        _db.Add(show);
        await _db.SaveChangesAsync();
        // Its first look-up is when its artwork is from.
        Assert.Equal(LastMonth, (await ReadAsync(show.Id)).ArtworkChangedAt);

        // Looked up again, with everything about its artwork set again to what it was.
        show.MetadataUpdatedAt = LastWeek;
        show.PosterUrl = null;
        show.PosterUrl = "https://image.test/severance.jpg";
        show.Overview = "Mark leads a team of office workers.";
        await _db.SaveChangesAsync();

        var saved = await ReadAsync(show.Id);
        Assert.Equal(LastWeek, saved.MetadataUpdatedAt);
        Assert.Equal(LastMonth, saved.ArtworkChangedAt);
    }

    [Fact]
    public async Task ATitleFromBeforeArtworkHadATimeOfItsOwn_IsGivenOneWhenItsNextLookedUp()
    {
        var show = Item(MediaKind.Series, "Severance");
        show.PosterUrl = "https://image.test/severance.jpg";
        _db.Add(show);
        await _db.SaveChangesAsync();
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, LastMonth)); // as an earlier version left it
        _db.ChangeTracker.Clear();
        Assert.Null((await ReadAsync(show.Id)).ArtworkChangedAt);

        var tracked = await _db.MediaItems.SingleAsync();
        tracked.MetadataUpdatedAt = LastWeek;
        await _db.SaveChangesAsync();
        Assert.Equal(LastWeek, (await ReadAsync(show.Id)).ArtworkChangedAt);

        // Sent back to be looked up afresh (a null), then looked up: its artwork is still from when it was.
        tracked.MetadataUpdatedAt = null;
        await _db.SaveChangesAsync();
        tracked.MetadataUpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        Assert.Equal(LastWeek, (await ReadAsync(show.Id)).ArtworkChangedAt);
    }

    [Theory]
    [InlineData("poster")]
    [InlineData("backdrop")]
    [InlineData("logo")]
    [InlineData("thumb")]
    public async Task ChangedArtwork_MovesTheTitleOn_AndTheSeasonsAndEpisodesThatBorrowIt(string kind)
    {
        var show = Item(MediaKind.Series, "Severance");
        (show.PosterUrl, show.MetadataUpdatedAt) = ("https://image.test/severance.jpg", LastMonth);
        var season = Item(MediaKind.Season, "Season 1", show.Id);
        season.MetadataUpdatedAt = LastMonth;
        var episode = Item(MediaKind.Episode, "Good News About Hell", season.Id);
        (episode.ThumbUrl, episode.MetadataUpdatedAt) = ("https://image.test/s01e01.jpg", LastMonth);
        var other = Item(MediaKind.Series, "Slow Horses");
        (other.PosterUrl, other.MetadataUpdatedAt) = ("https://image.test/slow-horses.jpg", LastMonth);
        var otherSeason = Item(MediaKind.Season, "Season 1", other.Id);
        otherSeason.MetadataUpdatedAt = LastMonth;
        _db.AddRange(show, season, episode, other, otherSeason);
        await _db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        _ = kind switch
        {
            "poster" => show.PosterUrl = "https://image.test/severance-season-2.jpg",
            "backdrop" => show.BackdropUrl = "https://image.test/severance-wide.jpg",
            "logo" => show.LogoUrl = "https://image.test/severance-logo.png",
            _ => show.ThumbUrl = "https://image.test/severance-thumb.jpg",
        };
        await _db.SaveChangesAsync();

        var changed = (await ReadAsync(show.Id)).ArtworkChangedAt;
        Assert.True(changed >= before);
        Assert.Equal(changed, (await ReadAsync(season.Id)).ArtworkChangedAt);
        Assert.Equal(changed, (await ReadAsync(episode.Id)).ArtworkChangedAt);
        // Nobody else's, and nothing else about the titles.
        Assert.Equal(LastMonth, (await ReadAsync(other.Id)).ArtworkChangedAt);
        Assert.Equal(LastMonth, (await ReadAsync(otherSeason.Id)).ArtworkChangedAt);
        Assert.Equal(LastMonth, (await ReadAsync(episode.Id)).MetadataUpdatedAt);
        Assert.Equal("https://image.test/s01e01.jpg", (await ReadAsync(episode.Id)).ThumbUrl);
    }

    [Fact]
    public async Task AReplacedLocalPicture_NotedByTheScan_ReachesTheEpisodesToo()
    {
        var show = Item(MediaKind.Series, "Home");
        (show.PosterUrl, show.MetadataUpdatedAt) = ("file:///shows/Home/poster.jpg", LastMonth);
        var season = Item(MediaKind.Season, "Season 1", show.Id);
        season.MetadataUpdatedAt = LastMonth;
        _db.AddRange(show, season);
        await _db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        show.ArtworkChangedAt = LastWeek; // what the scan does when the file changes (the address stays the same)
        await _db.SaveChangesAsync();

        Assert.Equal(LastWeek, (await ReadAsync(show.Id)).ArtworkChangedAt);
        Assert.True((await ReadAsync(season.Id)).ArtworkChangedAt >= before); // moved on, so its address is a new one
        Assert.Equal("file:///shows/Home/poster.jpg", (await ReadAsync(show.Id)).PosterUrl);
    }

    [Fact]
    public async Task ASeasonMovedToAnotherShow_MovesOn_WithItsEpisodes_AsTheArtworkTheyBorrowIsAnothers()
    {
        var show = Item(MediaKind.Series, "Severance");
        (show.PosterUrl, show.MetadataUpdatedAt) = ("https://image.test/severance.jpg", LastMonth);
        var other = Item(MediaKind.Series, "Slow Horses");
        (other.PosterUrl, other.MetadataUpdatedAt) = ("https://image.test/slow-horses.jpg", LastMonth);
        var season = Item(MediaKind.Season, "Season 1", show.Id);
        season.MetadataUpdatedAt = LastMonth;
        var episode = Item(MediaKind.Episode, "Failure's Contagious", season.Id);
        episode.MetadataUpdatedAt = LastMonth;
        _db.AddRange(show, other, season, episode);
        await _db.SaveChangesAsync();

        var before = DateTime.UtcNow;
        season.ParentId = other.Id;
        await _db.SaveChangesAsync();

        var moved = (await ReadAsync(season.Id)).ArtworkChangedAt;
        Assert.True(moved >= before);
        Assert.True((await ReadAsync(episode.Id)).ArtworkChangedAt >= before);
        // The shows' own pictures are what they were.
        Assert.Equal(LastMonth, (await ReadAsync(show.Id)).ArtworkChangedAt);
        Assert.Equal(LastMonth, (await ReadAsync(other.Id)).ArtworkChangedAt);
    }

    [Fact]
    public async Task AnEpisodeLookedUpAfterItsShowsArtworkChanged_StillHasANewAddress()
    {
        // As the weekly refresh goes: the show is saved with a new poster, then its episodes, from the same context,
        // whose copies of them are from before the change was handed down.
        var show = Item(MediaKind.Series, "Severance");
        show.PosterUrl = "https://image.test/severance.jpg";
        var season = Item(MediaKind.Season, "Season 1", show.Id);
        var episode = Item(MediaKind.Episode, "Good News About Hell", season.Id);
        _db.AddRange(show, season, episode);
        await _db.SaveChangesAsync();
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.MetadataUpdatedAt, LastMonth)); // as an earlier version left them
        _db.ChangeTracker.Clear();
        var tracked = await _db.MediaItems.ToDictionaryAsync(i => i.Kind);
        var address = ((await ReadAsync(episode.Id)).ArtworkChangedAt ?? LastMonth).Ticks;

        (tracked[MediaKind.Series].PosterUrl, tracked[MediaKind.Series].MetadataUpdatedAt) = ("https://image.test/severance-season-2.jpg", DateTime.UtcNow);
        await _db.SaveChangesAsync();
        tracked[MediaKind.Episode].MetadataUpdatedAt = DateTime.UtcNow;
        tracked[MediaKind.Episode].Overview = "Mark is promoted.";
        await _db.SaveChangesAsync();

        var saved = await ReadAsync(episode.Id);
        Assert.NotNull(saved.ArtworkChangedAt);
        Assert.NotEqual(address, saved.ArtworkChangedAt!.Value.Ticks);
        Assert.True(saved.ArtworkChangedAt > LastWeek);
    }
}
