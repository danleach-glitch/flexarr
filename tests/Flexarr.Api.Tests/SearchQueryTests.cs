using System.Collections.Concurrent;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
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
    private static readonly Regex ListOfEveryTitle = new("""IN \(\s*SELECT "m\d*"\."Id"\s+FROM "MediaItems" AS "m\d*"\s+WHERE (?!.*"Id" (=|IN))""", RegexOptions.Singleline);

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
}
