using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;

namespace Flexarr.Core.Tests;

/// <summary>Titles' ages, handed down to their episodes, and the filter that keeps a profile within its age limit.</summary>
public sealed class AgeLimitTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("flexarr-ages-").FullName;
    private FlexarrDbContext _db = null!;
    private Library _films = null!;
    private Library _shows = null!;

    public async Task InitializeAsync()
    {
        _db = Context();
        await _db.Database.MigrateAsync();
        _films = new Library { Name = "Films", Type = LibraryType.Movies };
        _shows = new Library { Name = "TV", Type = LibraryType.TvShows };
        _db.Libraries.AddRange(_films, _shows);
        await _db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private FlexarrDbContext Context() =>
        new(new DbContextOptionsBuilder<FlexarrDbContext>().UseSqlite($"Data Source={Path.Combine(_dir, "test.db")}").Options);

    private static MediaItem Item(Library library, MediaKind kind, string title, string? rating = null, Guid? parentId = null) =>
        new() { LibraryId = library.Id, Kind = kind, Title = title, SortTitle = title, ContentRating = rating, ParentId = parentId };

    [Fact]
    public async Task EpisodesAddedLater_TakeTheirShowsAge_AndFollowItWhenItChanges()
    {
        var show = Item(_shows, MediaKind.Series, "Bluey", "TV-Y");
        _db.MediaItems.Add(show);
        await _db.SaveChangesAsync();

        // A later scan, in a context of its own: the show isn't loaded.
        await using (var scan = Context())
        {
            var season = Item(_shows, MediaKind.Season, "Season 1", parentId: show.Id);
            scan.MediaItems.Add(season);
            scan.MediaItems.Add(Item(_shows, MediaKind.Episode, "Magic Xylophone", parentId: season.Id));
            await scan.SaveChangesAsync();
        }

        Assert.All(await Ages(), a => Assert.Equal(0, a.RatingAge));

        show.ContentRating = "TV-PG";
        show.KidsOverride = KidsOverride.Block;
        await _db.SaveChangesAsync();

        Assert.All(await Ages(), a => Assert.Equal((8, KidsOverride.Block), (a.RatingAge, a.KidsOverride)));
    }

    [Fact]
    public async Task AnAgeLimit_ShowsWhatsRatedWithinIt_AndWhatAParentAllowed()
    {
        var family = new Library { Name = "Family videos", Type = LibraryType.HomeVideos, FineForKids = true };
        _db.Libraries.Add(family);
        _db.MediaItems.AddRange(
            Item(_films, MediaKind.Movie, "Paddington", "PG"),
            Item(_films, MediaKind.Movie, "Alien", "R"),
            Item(_films, MediaKind.Movie, "Unrated thing"),
            Item(_films, MediaKind.Movie, "Allowed though rated 15", "15"),
            Item(family, MediaKind.Series, "Holiday 2025"),
            Item(family, MediaKind.Series, "Blocked though fine library"));
        await _db.SaveChangesAsync();
        await _db.MediaItems.Where(i => i.Title == "Allowed though rated 15").ExecuteUpdateAsync(u => u.SetProperty(i => i.KidsOverride, KidsOverride.Allow));
        await _db.MediaItems.Where(i => i.Title == "Blocked though fine library").ExecuteUpdateAsync(u => u.SetProperty(i => i.KidsOverride, KidsOverride.Block));

        await using var child = Context();
        child.MaxRatingAge = RatingAges.ParentalGuidance;
        child.KidsLibraryIds = [family.Id];
        var seen = await child.MediaItems.Select(i => i.Title).OrderBy(t => t).ToListAsync();

        Assert.Equal(["Allowed though rated 15", "Holiday 2025", "Paddington"], seen);
        Assert.Equal(6, await _db.MediaItems.CountAsync()); // no limit, everything
    }

    [Fact]
    public async Task MusicAndHomeVideoLibraries_AreLeftOut_UnlessFineForKids()
    {
        _db.Libraries.AddRange(new Library { Name = "Music", Type = LibraryType.Music }, new Library { Name = "Home", Type = LibraryType.HomeVideos },
            new Library { Name = "Kids' music", Type = LibraryType.Music, FineForKids = true });
        await _db.SaveChangesAsync();

        await using var child = Context();
        child.MaxRatingAge = RatingAges.Twelve;

        Assert.Equal(["Films", "Kids' music", "TV"], await child.Libraries.Select(l => l.Name).OrderBy(n => n).ToListAsync());
    }

    [Fact]
    public async Task AnExplicitEpisode_KeepsItsOwnAge_UnderACleanPodcast()
    {
        var podcasts = new Library { Name = "Podcasts", Type = LibraryType.Podcasts };
        _db.Libraries.Add(podcasts);
        var feed = Item(podcasts, MediaKind.Podcast, "Wow in the World");
        _db.MediaItems.Add(feed);
        await _db.SaveChangesAsync();
        _db.MediaItems.AddRange(Item(podcasts, MediaKind.PodcastEpisode, "Clean", parentId: feed.Id),
            Item(podcasts, MediaKind.PodcastEpisode, "Sweary", "Explicit", feed.Id));
        await _db.SaveChangesAsync();

        var ages = await _db.MediaItems.Where(i => i.LibraryId == podcasts.Id).ToDictionaryAsync(i => i.Title, i => i.RatingAge);

        Assert.Equal(0, ages["Wow in the World"]);
        Assert.Equal(0, ages["Clean"]);
        Assert.Equal(18, ages["Sweary"]);
    }

    [Fact]
    public async Task Reconciling_WorksOutAgesForTitlesSavedWithout()
    {
        var show = Item(_shows, MediaKind.Series, "Doctor Who", "TV-PG");
        _db.MediaItems.Add(show);
        await _db.SaveChangesAsync();
        var season = Item(_shows, MediaKind.Season, "Series 1", parentId: show.Id);
        _db.MediaItems.Add(season);
        await _db.SaveChangesAsync();
        _db.MediaItems.Add(Item(_shows, MediaKind.Episode, "Rose", parentId: season.Id));
        _db.MediaItems.Add(Item(_films, MediaKind.Movie, "Not Rated", "NR"));
        await _db.SaveChangesAsync();
        // As a database from before ages were kept would have it.
        await _db.MediaItems.ExecuteUpdateAsync(u => u.SetProperty(i => i.RatingAge, 99));

        await _db.ReconcileRatingAgesAsync(default);

        var ages = await _db.MediaItems.AsNoTracking().ToDictionaryAsync(i => i.Title, i => i.RatingAge);
        Assert.Equal(8, ages["Doctor Who"]);
        Assert.Equal(8, ages["Series 1"]);
        Assert.Equal(8, ages["Rose"]);
        Assert.Null(ages["Not Rated"]);
    }

    [Fact]
    public async Task Reconciling_AHugeShow_FitsSqlitesLimitOnParameters()
    {
        var show = Item(_shows, MediaKind.Series, "Long runner", "TV-14");
        _db.MediaItems.Add(show);
        await _db.SaveChangesAsync();
        var season = Item(_shows, MediaKind.Season, "Season 1", parentId: show.Id);
        _db.MediaItems.Add(season);
        await _db.SaveChangesAsync();
        // More episodes at one age than SQLite takes parameters, as a big library's first start after the upgrade has.
        await _db.Database.ExecuteSqlRawAsync("""
            WITH RECURSIVE n(x) AS (SELECT 1 UNION ALL SELECT x + 1 FROM n WHERE x < 40000)
            INSERT INTO MediaItems (Id, LibraryId, ParentId, Kind, Title, SortTitle, AddedAt, KidsOverride)
            SELECT upper(hex(randomblob(4)) || '-' || hex(randomblob(2)) || '-4' || substr(hex(randomblob(2)), 2) || '-8'
                || substr(hex(randomblob(2)), 2) || '-' || hex(randomblob(6))), {0}, {1}, 'Episode', 'E' || x, 'e' || x, '2026-10-30 00:00:00', 'None'
            FROM n
            """, _shows.Id.ToString().ToUpperInvariant(), season.Id.ToString().ToUpperInvariant());

        await _db.ReconcileRatingAgesAsync(default);

        await using var fresh = Context();
        Assert.Equal(40_000, await fresh.MediaItems.CountAsync(i => i.Kind == MediaKind.Episode && i.RatingAge == 15));
    }

    private async Task<List<(int? RatingAge, KidsOverride KidsOverride)>> Ages()
    {
        await using var fresh = Context();
        return (await fresh.MediaItems.Select(i => new { i.RatingAge, i.KidsOverride }).ToListAsync()).Select(a => (a.RatingAge, a.KidsOverride)).ToList();
    }
}
