using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Core.Scanning;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

/// <summary>Copies of one title in different libraries, found by id, and the watch state they share.</summary>
public sealed class TitleLinkerTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-links-").FullName;
    private FlexarrDbContext _db = null!;
    private Library _hd = null!;
    private Library _uhd = null!;

    public async Task InitializeAsync()
    {
        _db = new FlexarrDbContext(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);
        await _db.Database.MigrateAsync();
        _hd = new Library { Name = "HD", Type = LibraryType.Movies };
        _uhd = new Library { Name = "4K", Type = LibraryType.Movies };
        _db.Libraries.AddRange(_hd, _uhd);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private MediaItem Add(Library library, MediaKind kind, string title, string? tmdb = null, string? tvdb = null, string? imdb = null, MediaItem? parent = null,
        int? season = null, int? episode = null, int? episodeEnd = null)
    {
        var item = new MediaItem { LibraryId = library.Id, Kind = kind, Title = title, SortTitle = title, TmdbId = tmdb, TvdbId = tvdb, ImdbId = imdb, ParentId = parent?.Id,
            SeasonNumber = season, EpisodeNumber = episode, EpisodeNumberEnd = episodeEnd };
        _db.MediaItems.Add(item);
        return item;
    }

    private async Task<Guid?> GroupOf(MediaItem item) =>
        await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.Id == item.Id).Select(i => i.LinkGroupId).FirstAsync();

    private async Task LinkAsync()
    {
        await _db.SaveChangesAsync();
        await TitleLinker.LinkAsync(_db, default);
        _db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task FilmsSharingAnId_Link()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        var other = Add(_uhd, MediaKind.Movie, "Other", tmdb: "2");
        await LinkAsync();

        Assert.NotNull(await GroupOf(hd));
        Assert.Equal(await GroupOf(hd), await GroupOf(uhd));
        Assert.Null(await GroupOf(other));
    }

    [Fact]
    public async Task ASecondRun_FindsNothingToDo()
    {
        Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        Add(_uhd, MediaKind.Movie, "Other", tmdb: "2");
        await LinkAsync();

        Assert.Equal(0, await TitleLinker.LinkAsync(_db, default));
    }

    [Fact]
    public async Task WithNoCopiesAtAll_ThereIsNothingToDo()
    {
        Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        Add(_uhd, MediaKind.Movie, "Other", tmdb: "2");
        await _db.SaveChangesAsync();

        Assert.Equal(0, await TitleLinker.LinkAsync(_db, default));
    }

    [Fact]
    public async Task ManyCopiesLinkedInBatches_AllLink_AndShareTheirWatchState()
    {
        var pairs = Enumerable.Range(0, 600).Select(n =>
            (Hd: Add(_hd, MediaKind.Movie, $"Film {n}", tmdb: $"{n}"), Uhd: Add(_uhd, MediaKind.Movie, $"Film {n}", tmdb: $"{n}"))).ToList();
        await _db.SaveChangesAsync();
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = pairs[^1].Hd.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow });
        var last = pairs[^1].Uhd.Id;
        await LinkAsync();

        var linked = await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().CountAsync(i => i.LinkGroupId != null);
        Assert.Equal(1200, linked);
        Assert.True(await _db.PlaybackStates.AsNoTracking().Where(p => p.MediaItemId == last).Select(p => p.Watched).FirstAsync());
        Assert.Empty(_db.ChangeTracker.Entries<PlaybackState>());
    }

    [Fact]
    public async Task SeasonsOfAShowNoLongerLinked_Unlink()
    {
        var hd = Add(_hd, MediaKind.Series, "Show", tvdb: "1");
        var uhd = Add(_uhd, MediaKind.Series, "Show", tvdb: "1");
        var one = Add(_hd, MediaKind.Season, "1", parent: hd, season: 1);
        var other = Add(_uhd, MediaKind.Season, "1", parent: uhd, season: 1);
        await LinkAsync();
        Assert.NotNull(await GroupOf(one));

        (await _db.MediaItems.IgnoreQueryFilters().FirstAsync(i => i.Id == uhd.Id)).TvdbId = "2";
        await LinkAsync();

        Assert.Null(await GroupOf(one));
        Assert.Null(await GroupOf(other));
    }

    [Fact]
    public async Task AnIdSharedThroughAThirdCopy_LinksAllThree()
    {
        var a = Add(_hd, MediaKind.Movie, "A", tmdb: "1");
        var b = Add(_hd, MediaKind.Movie, "B", tmdb: "1", imdb: "tt9");
        var c = Add(_uhd, MediaKind.Movie, "C", imdb: "tt9");
        await LinkAsync();

        Assert.NotNull(await GroupOf(a));
        Assert.Equal(await GroupOf(a), await GroupOf(b));
        Assert.Equal(await GroupOf(a), await GroupOf(c));
    }

    [Fact]
    public async Task AFilmAndAShowWithTheSameNumber_DoNotLink()
    {
        var film = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var show = Add(_hd, MediaKind.Series, "Show", tmdb: "1");
        await LinkAsync();

        Assert.Null(await GroupOf(film));
        Assert.Null(await GroupOf(show));
    }

    [Fact]
    public async Task AnIdOnTooManyTitles_LinksNothing()
    {
        var films = Enumerable.Range(0, 7).Select(i => Add(_hd, MediaKind.Movie, $"Film {i}", imdb: "tt1")).ToList();
        await LinkAsync();

        foreach (var film in films)
        {
            Assert.Null(await GroupOf(film));
        }
    }

    [Fact]
    public async Task ChangingAnId_Unlinks()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        await LinkAsync();
        Assert.NotNull(await GroupOf(hd));

        await _db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == uhd.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.TmdbId, "2"));
        await LinkAsync();

        Assert.Null(await GroupOf(hd));
        Assert.Null(await GroupOf(uhd));
    }

    [Fact]
    public async Task AGoneCopy_IsUnlinked()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        await LinkAsync();

        await _db.MediaItems.IgnoreQueryFilters().Where(i => i.Id == uhd.Id).ExecuteUpdateAsync(u => u.SetProperty(i => i.MissingSince, DateTime.UtcNow));
        await LinkAsync();

        Assert.Null(await GroupOf(hd));
        Assert.Null(await GroupOf(uhd));
    }

    [Fact]
    public async Task LinkedShows_LinkMatchingSeasonsAndEpisodes_AndLeaveTheRest()
    {
        var hd = Add(_hd, MediaKind.Series, "Show", tvdb: "5");
        var uhd = Add(_uhd, MediaKind.Series, "Show", tvdb: "5");
        await _db.SaveChangesAsync();
        var hd1 = Add(_hd, MediaKind.Season, "Season 1", parent: hd, season: 1);
        var hd2 = Add(_hd, MediaKind.Season, "Season 2", parent: hd, season: 2);
        var uhd1 = Add(_uhd, MediaKind.Season, "Season 1", parent: uhd, season: 1);
        await _db.SaveChangesAsync();
        var hdE1 = Add(_hd, MediaKind.Episode, "One", parent: hd1, season: 1, episode: 1);
        var hdE2 = Add(_hd, MediaKind.Episode, "Two and Three", parent: hd1, season: 1, episode: 2, episodeEnd: 3);
        var uhdE1 = Add(_uhd, MediaKind.Episode, "One", parent: uhd1, season: 1, episode: 1);
        var uhdE2 = Add(_uhd, MediaKind.Episode, "Two", parent: uhd1, season: 1, episode: 2);
        await LinkAsync();

        Assert.NotNull(await GroupOf(hd1));
        Assert.Equal(await GroupOf(hd1), await GroupOf(uhd1));
        Assert.Null(await GroupOf(hd2));
        Assert.NotNull(await GroupOf(hdE1));
        Assert.Equal(await GroupOf(hdE1), await GroupOf(uhdE1));
        Assert.Null(await GroupOf(hdE2)); // A two-part file isn't a copy of one episode.
        Assert.Null(await GroupOf(uhdE2));
    }

    [Fact]
    public async Task ADailyShowsEpisodeNamedByDate_LinksTheNumberedOneThatAiredThatDay()
    {
        var hd = Add(_hd, MediaKind.Series, "Daily", tvdb: "8");
        var uhd = Add(_uhd, MediaKind.Series, "Daily", tvdb: "8");
        await _db.SaveChangesAsync();
        var hdSeason = Add(_hd, MediaKind.Season, "2026", parent: hd, season: 2026);
        var uhdSeason = Add(_uhd, MediaKind.Season, "2026", parent: uhd, season: 2026);
        await _db.SaveChangesAsync();
        var numbered = Add(_hd, MediaKind.Episode, "41", parent: hdSeason, season: 2026, episode: 41);
        numbered.AirDate = new DateOnly(2026, 3, 4);
        var dated = Add(_uhd, MediaKind.Episode, "4 March", parent: uhdSeason, season: 2026);
        dated.AirDate = new DateOnly(2026, 3, 4);
        // Two that aired the same day: a file named by that date could be either.
        var first = Add(_hd, MediaKind.Episode, "42", parent: hdSeason, season: 2026, episode: 42);
        first.AirDate = new DateOnly(2026, 3, 5);
        var second = Add(_hd, MediaKind.Episode, "43", parent: hdSeason, season: 2026, episode: 43);
        second.AirDate = new DateOnly(2026, 3, 5);
        var either = Add(_uhd, MediaKind.Episode, "5 March", parent: uhdSeason, season: 2026);
        either.AirDate = new DateOnly(2026, 3, 5);
        await LinkAsync();

        Assert.NotNull(await GroupOf(numbered));
        Assert.Equal(await GroupOf(numbered), await GroupOf(dated));
        Assert.Null(await GroupOf(first));
        Assert.Null(await GroupOf(second));
        Assert.Null(await GroupOf(either));
    }

    [Fact]
    public async Task OnFirstLink_ACopyWatchedCounts_AndTheNewestPlaceWins()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        await _db.SaveChangesAsync();
        _db.PlaybackStates.AddRange(
            new PlaybackState { MediaItemId = hd.Id, Watched = true, PlayCount = 2, PositionSeconds = 0, LastPlayedAt = DateTime.UtcNow.AddDays(-9) },
            new PlaybackState { MediaItemId = uhd.Id, PositionSeconds = 500, PlayCount = 1, LastPlayedAt = DateTime.UtcNow });
        await LinkAsync();

        foreach (var copy in new[] { hd, uhd })
        {
            var state = await _db.PlaybackStates.AsNoTracking().SingleAsync(p => p.MediaItemId == copy.Id);
            Assert.True(state.Watched);
            Assert.Equal(500, state.PositionSeconds);
            Assert.Equal(2, state.PlayCount);
        }
    }

    [Fact]
    public async Task ACopyComingBack_DoesNotUndoMarkingUnwatched()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        await _db.SaveChangesAsync();
        _db.PlaybackStates.AddRange(
            new PlaybackState { MediaItemId = hd.Id, Watched = true, PlayCount = 1, LastPlayedAt = DateTime.UtcNow.AddDays(-9) },
            new PlaybackState { MediaItemId = uhd.Id, Watched = false, PlayCount = 1, LastPlayedAt = DateTime.UtcNow });
        await LinkAsync();

        Assert.All(await _db.PlaybackStates.AsNoTracking().ToListAsync(), state => Assert.False(state.Watched));
    }

    [Theory]
    [InlineData(KidsOverride.Block, KidsOverride.None, KidsOverride.Block)]
    [InlineData(KidsOverride.Block, KidsOverride.Allow, KidsOverride.Block)]
    [InlineData(KidsOverride.Allow, KidsOverride.None, KidsOverride.Allow)]
    [InlineData(KidsOverride.None, KidsOverride.None, KidsOverride.None)]
    public async Task OnLinking_CopiesAgreeAParentsSay_BlockWinning(KidsOverride first, KidsOverride second, KidsOverride agreed)
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        hd.KidsOverride = first;
        uhd.KidsOverride = second;
        await LinkAsync();

        var says = await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().Select(i => i.KidsOverride).ToListAsync();
        Assert.All(says, say => Assert.Equal(agreed, say));
    }

    [Fact]
    public async Task ACopyOfABlockedShow_IsBlocked_DownToItsEpisodes()
    {
        var hd = Add(_hd, MediaKind.Series, "Show", tvdb: "1");
        hd.KidsOverride = KidsOverride.Block;
        var uhd = Add(_uhd, MediaKind.Series, "Show", tvdb: "1");
        var season = Add(_uhd, MediaKind.Season, "1", parent: uhd, season: 1);
        var episode = Add(_uhd, MediaKind.Episode, "E1", parent: season, season: 1, episode: 1);
        await LinkAsync();

        var says = await _db.MediaItems.IgnoreQueryFilters().AsNoTracking().Where(i => i.LibraryId == _uhd.Id).Select(i => i.KidsOverride).ToListAsync();
        Assert.Equal(3, says.Count);
        Assert.All(says, say => Assert.Equal(KidsOverride.Block, say));
        Assert.NotNull(episode);
    }

    [Fact]
    public async Task Saving_WritesWatchStateToEveryCopy_EvenOneThisProfileCannotSee()
    {
        var hd = Add(_hd, MediaKind.Movie, "Film", tmdb: "1");
        var uhd = Add(_uhd, MediaKind.Movie, "Film", tmdb: "1");
        await LinkAsync();

        _db.HiddenLibraryIds = [_uhd.Id];
        _db.PlaybackStates.Add(new PlaybackState { MediaItemId = hd.Id, PositionSeconds = 321, SubtitleDelaySeconds = 2 });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var other = await _db.PlaybackStates.IgnoreQueryFilters().AsNoTracking().SingleAsync(p => p.MediaItemId == uhd.Id);
        Assert.Equal(321, other.PositionSeconds);
        Assert.Equal(0, other.SubtitleDelaySeconds); // That's about these files.

        var state = await _db.PlaybackStates.IgnoreQueryFilters().SingleAsync(p => p.MediaItemId == hd.Id);
        state.Watched = true;
        await _db.SaveChangesAsync();
        Assert.True(await _db.PlaybackStates.IgnoreQueryFilters().AsNoTracking().Where(p => p.MediaItemId == uhd.Id).Select(p => p.Watched).SingleAsync());
    }
}
