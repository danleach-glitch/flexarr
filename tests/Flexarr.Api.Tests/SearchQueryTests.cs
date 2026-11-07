using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Flexarr.Api.Search;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>
/// What a search returns, and in what order, group by group, with the SQL it runs to get there: the server keeps a
/// note of every statement, so a test can say what a search may not ask the database to do.
/// </summary>
public sealed class SearchQueryTests : IAsyncLifetime
{
    private readonly FlexarrApiFactory _factory = new();
    private readonly Recorder _sql = new();
    private WebApplicationFactory<Program> _server = null!;

    public Task InitializeAsync()
    {
        _server = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.ConfigureDbContext<FlexarrDbContext>(o => o.AddInterceptors(_sql))));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _server.DisposeAsync();
        await _factory.DisposeAsync();
    }

    /// <summary>Every statement the server ran, in order.</summary>
    private sealed class Recorder : DbCommandInterceptor
    {
        public ConcurrentQueue<string> Statements { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Statements.Enqueue(command.CommandText);
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Statements.Enqueue(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Client(string? profileToken = null)
    {
        var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    private static string Unique() => $"zq{Guid.NewGuid().ToString("N")[..8]}";

    /// <summary>Searches, and gives back the answer with the statements that search ran.</summary>
    private async Task<(JsonElement Results, List<string> Sql)> SearchAsync(string query, string? token = null)
    {
        var client = Client(token);
        _sql.Statements.Clear();
        var results = await client.GetFromJsonAsync<JsonElement>(Api($"/search?q={Uri.EscapeDataString(query)}"));
        return (results, [.. _sql.Statements]);
    }

    private async Task SeedAsync(Action<FlexarrDbContext> seed)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<string> ProfileAsync(string name, params Guid[] hidden)
    {
        var created = await (await Client().PostAsJsonAsync(Api("/profiles"), new { name = $"{name} {Unique()}", hiddenLibraryIds = hidden }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var selected = await (await Client().PostAsJsonAsync(Api($"/profiles/{created.GetProperty("id").GetString()}/select"), new { }))
            .Content.ReadFromJsonAsync<JsonElement>();
        return selected.GetProperty("token").GetString()!;
    }

    private static MediaItem Film(Library library, string title) =>
        new() { LibraryId = library.Id, Kind = MediaKind.Movie, Title = title, SortTitle = title.ToLowerInvariant() };

    private static List<(Guid Id, int Credits)> People(JsonElement results) =>
        [.. results.GetProperty("people").EnumerateArray().Select(p => (p.GetProperty("id").GetGuid(), p.GetProperty("credits").GetInt32()))];

    /// <summary>A list of every title's id that a row's id is then looked for in: SQLite builds the whole list first.</summary>
    private static readonly Regex ListOfEveryTitle = new("""IN \(\s*SELECT "m\d*"\."Id"\s+FROM "MediaItems" """);

    [Fact]
    public async Task People_AreThoseInTitlesTheProfileCanSee_NamesThatStartWithTheWordFirst_ThenTheMostCredits()
    {
        var word = Unique();
        var seen = new Library { Name = $"Films {word}", Type = LibraryType.Movies };
        var hidden = new Library { Name = $"Hidden {word}", Type = LibraryType.Movies };
        var seenFilms = Enumerable.Range(0, 5).Select(n => Film(seen, $"Seen {n}")).ToList();
        var hiddenFilms = Enumerable.Range(0, 4).Select(n => Film(hidden, $"Hidden {n}")).ToList();
        var gone = Film(seen, "Gone");
        gone.MissingSince = DateTime.UtcNow;
        var three = new Person { Name = $"{word} Three" };
        var both = new Person { Name = $"{word} Both" };
        var onlyHidden = new Person { Name = $"{word} Hidden" };
        var later = new Person { Name = $"Later {word}" };
        var nowhere = new Person { Name = $"{word} Nowhere" };
        var onlyGone = new Person { Name = $"{word} Gone" };
        await SeedAsync(db =>
        {
            db.AddRange(seen, hidden, gone, three, both, onlyHidden, later, nowhere, onlyGone);
            db.AddRange(seenFilms);
            db.AddRange(hiddenFilms);
            Credit In(Person person, MediaItem film) => new() { PersonId = person.Id, MediaItemId = film.Id, Role = CreditRole.Actor };
            db.AddRange(seenFilms.Take(3).Select(f => In(three, f)));
            db.Add(In(three, gone)); // a title that's gone missing doesn't count
            db.Add(In(both, seenFilms[0]));
            db.AddRange(hiddenFilms.Select(f => In(both, f)));
            db.AddRange(hiddenFilms.Take(2).Select(f => In(onlyHidden, f)));
            db.AddRange(seenFilms.Select(f => In(later, f)));
            db.Add(In(onlyGone, gone));
        });
        var hides = await ProfileAsync("Hides", hidden.Id);

        var (everything, sql) = await SearchAsync(word);
        var (some, _) = await SearchAsync(word, hides);

        Assert.Equal([(both.Id, 5), (three.Id, 3), (onlyHidden.Id, 2), (later.Id, 5)], People(everything));
        Assert.Equal([(three.Id, 3), (both.Id, 1), (later.Id, 5)], People(some));
        Assert.Contains(sql, s => s.Contains("\"People\"", StringComparison.Ordinal));
        Assert.DoesNotContain(sql, s => ListOfEveryTitle.IsMatch(s));
    }

    [Fact]
    public async Task People_FoundDespiteATypo_AreStillOnlyThoseInTitlesTheProfileCanSee()
    {
        var word = Unique();
        var seen = new Library { Name = $"Films {word}", Type = LibraryType.Movies };
        var hidden = new Library { Name = $"Hidden {word}", Type = LibraryType.Movies };
        var films = new[] { Film(seen, "One"), Film(seen, "Two"), Film(hidden, "Three") };
        var actor = new Person { Name = $"Breaking{word} Jones" };
        var unseen = new Person { Name = $"Breaking{word} Smith" };
        await SeedAsync(db =>
        {
            db.AddRange(seen, hidden, actor, unseen);
            db.AddRange(films);
            db.AddRange(films.Select(f => new Credit { PersonId = actor.Id, MediaItemId = f.Id, Role = CreditRole.Actor }));
            db.Add(new Credit { PersonId = unseen.Id, MediaItemId = films[2].Id, Role = CreditRole.Actor });
        });
        var hides = await ProfileAsync("Hides", hidden.Id);

        var (everything, sql) = await SearchAsync($"braeking{word}");
        var (some, _) = await SearchAsync($"braeking{word}", hides);

        Assert.Equal([(actor.Id, 3), (unseen.Id, 1)], People(everything).OrderByDescending(p => p.Credits));
        Assert.Equal([(actor.Id, 2)], People(some));
        Assert.DoesNotContain(sql, s => ListOfEveryTitle.IsMatch(s));
    }

    [Fact]
    public async Task BookSeries_CountTheBooksTheProfileCanSee()
    {
        var word = Unique();
        var books = new Library { Name = $"Books {word}", Type = LibraryType.Audiobooks };
        var series = new BookSeries { LibraryId = books.Id, Name = $"{word} Saga", SortName = $"{word} saga" };
        var empty = new BookSeries { LibraryId = books.Id, Name = $"{word} Lost", SortName = $"{word} lost" };
        var volumes = Enumerable.Range(1, 3).Select(n => new MediaItem { LibraryId = books.Id, Kind = MediaKind.Audiobook, Title = $"Volume {n}", SortTitle = $"volume {n}" }).ToList();
        volumes[2].MissingSince = DateTime.UtcNow;
        await SeedAsync(db =>
        {
            db.AddRange(books, series, empty);
            db.AddRange(volumes);
            db.AddRange(volumes.Select((v, n) => new BookSeriesEntry { SeriesId = series.Id, BookId = v.Id, Sequence = $"{n + 1}", SortOrder = n + 1 }));
            db.Add(new BookSeriesEntry { SeriesId = empty.Id, BookId = volumes[2].Id });
        });
        var hides = await ProfileAsync("Hides", books.Id);

        var (everything, sql) = await SearchAsync(word);
        var (some, _) = await SearchAsync(word, hides);

        var found = everything.GetProperty("bookSeries").EnumerateArray().Select(s => (s.GetProperty("id").GetGuid(), s.GetProperty("bookCount").GetInt32())).ToList();
        Assert.Equal([(series.Id, 2)], found);
        Assert.Empty(some.GetProperty("bookSeries").EnumerateArray());
        Assert.DoesNotContain(sql, s => ListOfEveryTitle.IsMatch(s));
    }

    /// <summary>
    /// Episodes, albums, tracks and books as the query each kind has finds them: what a search gave before one pass
    /// found all four, and still gives when too many names match.
    /// </summary>
    private async Task<(List<Guid> Episodes, List<Guid> Albums, List<Guid> Tracks, List<Guid> Books)> EachKindsOwnQueryAsync(string term, params Guid[] hidden)
    {
        using var scope = _server.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        db.HiddenLibraryIds = hidden;
        var contains = $"%{term}%";
        var prefix = $"{term}%";
        var episodes = await db.MediaItems.Where(i => i.Kind == MediaKind.Episode && EF.Functions.Like(i.Title, contains))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).Take(8).Select(i => i.Id).ToListAsync();
        var albums = await db.MediaItems.Where(i => i.Kind == MediaKind.Album && EF.Functions.Like(i.Title, contains))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).ThenByDescending(i => i.Year).Take(8).Select(i => i.Id).ToListAsync();
        var tracks = await db.MediaItems.Where(i => i.Kind == MediaKind.Track && EF.Functions.Like(i.Title, contains))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix)).ThenBy(i => i.SortTitle).Take(8).Select(i => i.Id).ToListAsync();
        var books = await db.MediaItems.Where(i => i.Kind == MediaKind.Audiobook && i.MissingSince == null
                && (EF.Functions.Like(i.Title, contains) || (i.Book!.Subtitle != null && EF.Functions.Like(i.Book.Subtitle, contains))))
            .OrderByDescending(i => EF.Functions.Like(i.Title, prefix) || EF.Functions.Like(i.SortTitle, prefix)).ThenBy(i => i.SortTitle)
            .Take(8).Select(i => i.Id).ToListAsync();
        return (episodes, albums, tracks, books);
    }

    private static List<Guid> Ids(JsonElement results, string group) =>
        [.. results.GetProperty(group).EnumerateArray().Select(i => (group == "tracks" ? i.GetProperty("item") : i).GetProperty("id").GetGuid())];

    /// <summary>The statements that looked through the items for a name with the words in it.</summary>
    private static int PassesOverTitles(List<string> sql) => sql.Count(s => s.Contains("\"Title\" LIKE @contains", StringComparison.Ordinal));

    /// <summary>A show with episodes, an artist with albums and tracks, and books, with names that tie every way the orders can.</summary>
    private async Task<(Library Tv, Library Music, Library Books)> SeedNamedAsync(string word, int extraTracks = 0)
    {
        var tv = new Library { Name = $"TV {word}", Type = LibraryType.TvShows };
        var music = new Library { Name = $"Music {word}", Type = LibraryType.Music };
        var books = new Library { Name = $"Books {word}", Type = LibraryType.Audiobooks };
        var show = new MediaItem { LibraryId = tv.Id, Kind = MediaKind.Series, Title = "A Show", SortTitle = "show" };
        var season = new MediaItem { LibraryId = tv.Id, Kind = MediaKind.Season, Title = "Season 1", SortTitle = "1", SeasonNumber = 1, ParentId = show.Id };
        var artist = new MediaItem { LibraryId = music.Id, Kind = MediaKind.Artist, Title = "An Artist", SortTitle = "artist" };
        var album = new MediaItem { LibraryId = music.Id, Kind = MediaKind.Album, Title = "Their Album", SortTitle = "their album", ParentId = artist.Id };
        await SeedAsync(db => db.AddRange(tv, music, books, show, season, artist, album));
        // In a few saves, so the order the database holds them in isn't just the order of their ids.
        for (var batch = 0; batch < 3; batch++)
        {
            await SeedAsync(db =>
            {
                for (var n = 0; n < 5; n++)
                {
                    var starts = (n + batch) % 2 == 0;
                    db.Add(new MediaItem
                    {
                        LibraryId = tv.Id, Kind = MediaKind.Episode, ParentId = season.Id, SeasonNumber = 1, EpisodeNumber = batch * 5 + n,
                        Title = starts ? $"{word} Part {batch}{n}" : $"The {word} {batch}{n}", SortTitle = $"{batch}{n}",
                    });
                    db.Add(new MediaItem
                    {
                        LibraryId = music.Id, Kind = MediaKind.Album, ParentId = artist.Id, Title = starts ? $"{word} Sessions {batch}{n}" : $"Live {word} {batch}{n}",
                        SortTitle = $"album {batch}{n}", Year = n == 4 ? null : 2000 + ((n + batch) % 3),
                    });
                    // Sort titles that repeat, and ones only the database's order (by code point) puts right: a
                    // fullwidth letter comes before an emoji there, after it in .NET's ordinal order.
                    db.Add(new MediaItem
                    {
                        LibraryId = music.Id, Kind = MediaKind.Track, ParentId = album.Id, Title = starts ? $"{word} Song {batch}{n}" : $"My {word} {batch}{n}",
                        SortTitle = n switch { 0 => "\uFF21 song", 1 => "\U0001F600 song", 2 => "same", _ => $"song {(batch * 7 + n * 3) % 5}" },
                    });
                    var book = new MediaItem
                    {
                        LibraryId = books.Id, Kind = MediaKind.Audiobook,
                        Title = n switch { 0 => $"{word} Rising {batch}", 1 => $"The {word} Road {batch}", 2 => $"Nothing To See {batch}", _ => $"A {word} Tale {batch}{n}" },
                        SortTitle = n switch { 1 => $"{word} road {batch}", _ => $"book {(batch * 3 + n) % 4}" },
                        MissingSince = n == 4 && batch == 2 ? DateTime.UtcNow : null,
                    };
                    db.Add(book);
                    db.Add(new BookDetails { MediaItemId = book.Id, Subtitle = n == 2 ? $"A {word} Story" : n == 3 ? null : "Unabridged" });
                }
            });
        }

        if (extraTracks > 0)
        {
            await SeedAsync(db => db.AddRange(Enumerable.Range(0, extraTracks).Select(n => new MediaItem
            {
                LibraryId = music.Id, Kind = MediaKind.Track, ParentId = album.Id, Title = $"Filler {word} {n}", SortTitle = $"filler {n % 50:D2}",
            })));
        }

        return (tv, music, books);
    }

    [Fact]
    public async Task EpisodesAlbumsTracksAndBooks_AreWhatTheirOwnQueriesFind_InOnePassOverTheItems()
    {
        var word = Unique();
        var (tv, _, books) = await SeedNamedAsync(word);
        var hides = await ProfileAsync("Hides", tv.Id, books.Id);

        var (everything, sql) = await SearchAsync(word);
        var (some, _) = await SearchAsync(word, hides);

        var expected = await EachKindsOwnQueryAsync(word);
        Assert.Equal((8, 8, 8, 8), (expected.Episodes.Count, expected.Albums.Count, expected.Tracks.Count, expected.Books.Count));
        Assert.Equal(expected.Episodes, Ids(everything, "episodes"));
        Assert.Equal(expected.Albums, Ids(everything, "albums"));
        Assert.Equal(expected.Tracks, Ids(everything, "tracks"));
        Assert.Equal(expected.Books, Ids(everything, "books"));
        Assert.All(everything.GetProperty("tracks").EnumerateArray(), t => Assert.Equal("Their Album", t.GetProperty("album").GetString()));
        // One pass for the top-level titles, one for these four kinds.
        Assert.Equal(2, PassesOverTitles(sql));

        var seen = await EachKindsOwnQueryAsync(word, tv.Id, books.Id);
        Assert.Empty(Ids(some, "episodes"));
        Assert.Empty(Ids(some, "books"));
        Assert.Equal(seen.Albums, Ids(some, "albums"));
        Assert.Equal(seen.Tracks, Ids(some, "tracks"));
    }

    [Fact]
    public async Task WordsInTooManyNames_AreStillFoundKindByKind()
    {
        var word = Unique();
        await SeedNamedAsync(word, extraTracks: NamedItems.AtOnce);

        var (everything, sql) = await SearchAsync(word);

        var expected = await EachKindsOwnQueryAsync(word);
        Assert.Equal(expected.Episodes, Ids(everything, "episodes"));
        Assert.Equal(expected.Albums, Ids(everything, "albums"));
        Assert.Equal(expected.Tracks, Ids(everything, "tracks"));
        Assert.Equal(expected.Books, Ids(everything, "books"));
        // The top-level titles, the pass that gave up, and a query for each of the four kinds.
        Assert.Equal(6, PassesOverTitles(sql));
    }

    [Fact]
    public void Picks_KeepTiesInTheOrderTheyCame_AndSortTextAsTheDatabaseDoes()
    {
        static NamedItems.Named Item(MediaKind kind, bool starts, string sortTitle = "", int? year = null, bool sortStarts = false) =>
            new(Guid.NewGuid(), kind, starts, sortStarts, year, sortTitle);
        var episodes = new[] { Item(MediaKind.Episode, false), Item(MediaKind.Episode, true), Item(MediaKind.Episode, false), Item(MediaKind.Episode, true) };
        var albums = new[] { Item(MediaKind.Album, true, year: null), Item(MediaKind.Album, true, year: 1999), Item(MediaKind.Album, false, year: 2024), Item(MediaKind.Album, true, year: 2005), Item(MediaKind.Album, true, year: 1999) };
        var tracks = new[] { Item(MediaKind.Track, true, "\U0001F600"), Item(MediaKind.Track, true, "\uFF21"), Item(MediaKind.Track, true, "b"), Item(MediaKind.Track, false, "a"), Item(MediaKind.Track, true, "B"), Item(MediaKind.Track, true, "b") };
        var books = new[] { Item(MediaKind.Audiobook, false, "c"), Item(MediaKind.Audiobook, false, "b", sortStarts: true), Item(MediaKind.Audiobook, true, "d"), Item(MediaKind.Audiobook, false, "a") };

        var picks = NamedItems.Pick([.. episodes, .. albums, .. tracks, .. books]);

        Assert.Equal(new[] { 1, 3, 0, 2 }.Select(n => episodes[n].Id), picks.Episodes);
        Assert.Equal(new[] { 3, 1, 4, 0, 2 }.Select(n => albums[n].Id), picks.Albums);
        Assert.Equal(new[] { 4, 2, 5, 1, 0, 3 }.Select(n => tracks[n].Id), picks.Tracks);
        Assert.Equal(new[] { 1, 2, 3, 0 }.Select(n => books[n].Id), picks.Books);
        Assert.True(NamedItems.DatabaseOrder.Instance.Compare("\uFF21", "\U0001F600") < 0);
        Assert.True(string.CompareOrdinal("\uFF21", "\U0001F600") > 0);
        Assert.True(NamedItems.DatabaseOrder.Instance.Compare("ab", "abc") < 0);
        Assert.Equal(0, NamedItems.DatabaseOrder.Instance.Compare("\U0001F600x", "\U0001F600x"));
        Assert.True(NamedItems.DatabaseOrder.Instance.Compare("\U0001F600", "\U0001F601") < 0);
    }
}
