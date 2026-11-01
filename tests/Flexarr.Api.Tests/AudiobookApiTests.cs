using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Books;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>An audiobooks library's shelves, lists, series and book pages.</summary>
public class AudiobookApiTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task Shelves_ContinueListening_NextInSeries_Finished()
    {
        var lib = await SeedAsync();

        var shelves = await GetAsync<BookShelvesDto>($"/api/v1/libraries/{lib.Library}/book-shelves");

        Assert.Equal(["The Man Who Died Twice"], shelves.ContinueListening.Select(b => b.Title));
        // Thursday Murder Club's next is under way (so it's in Continue listening); Discworld's next hasn't been started.
        Assert.Equal(["Guards! Guards!"], shelves.NextInSeries.Select(b => b.Title));
        Assert.Equal(["The Thursday Murder Club", "The Colour of Magic"], shelves.Finished.Select(b => b.Title));
        Assert.Equal(["Discworld", "Thursday Murder Club"], shelves.Series.Select(s => s.Name));
        Assert.Equal(["Andy Weir", "Richard Osman", "Terry Pratchett"], shelves.Authors.Select(a => a.Name).Order());
        Assert.Contains(shelves.Narrators, n => n is { Name: "Lesley Manville", BookCount: 4 });
        Assert.Equal(["English"], shelves.Languages);
    }

    [Fact]
    public async Task ABookSummary_HasItsAuthorsNarratorSeriesAndLength()
    {
        var lib = await SeedAsync();

        var books = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?search=Murder%20Club");

        // The series' name finds all three of its books.
        Assert.Equal(3, books.Total);
        var book = books.Items.Single(b => b.Title == "The Thursday Murder Club");
        Assert.Equal(MediaKind.Audiobook, book.Kind);
        Assert.Equal(new BookSummaryDto("Richard Osman", 1, "Lesley Manville", "Thursday Murder Club", "1", 36000, book.Book!.FinishedAt), book.Book);
        Assert.NotNull(book.Book.FinishedAt);
        Assert.Equal(36000, book.DurationSeconds);
    }

    [Theory]
    [InlineData("progress=inprogress", new[] { "The Man Who Died Twice" })]
    [InlineData("progress=notstarted&sort=title", new[] { "The Bullet That Missed", "Guards! Guards!", "Project Hail Mary" })]
    [InlineData("progress=finished", new[] { "The Colour of Magic", "The Thursday Murder Club" })]
    [InlineData("sort=length&take=2", new[] { "Project Hail Mary", "The Bullet That Missed" })]
    [InlineData("minHours=11", new[] { "Project Hail Mary", "The Bullet That Missed" })]
    [InlineData("sort=series", new[] { "The Colour of Magic", "Guards! Guards!", "The Thursday Murder Club", "The Man Who Died Twice", "The Bullet That Missed", "Project Hail Mary" })]
    public async Task Books_FilterAndSort(string query, string[] titles)
    {
        var lib = await SeedAsync();

        var books = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?{query}");

        if (query.StartsWith("progress=finished", StringComparison.Ordinal) || query.StartsWith("minHours", StringComparison.Ordinal))
        {
            Assert.Equal(titles.Order(), books.Items.Select(b => b.Title).Order());
        }
        else
        {
            Assert.Equal(titles, books.Items.Select(b => b.Title));
        }
    }

    [Fact]
    public async Task Books_ByNarrator()
    {
        var lib = await SeedAsync();

        var books = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?narrator={lib.People["Ray Porter"]}");

        Assert.Equal(["Project Hail Mary"], books.Items.Select(b => b.Title));
    }

    [Fact]
    public async Task Books_ByLanguage_InAnySpellingOrCase()
    {
        var lib = await SeedAsync(); // every book tagged "English"

        var byCode = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?language=EN");
        var byName = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?language=english");
        var german = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?language=ger");

        Assert.Equal((6, 6, 0), (byCode.Total, byName.Total, german.Total));
    }

    [Theory]
    [InlineData("Русский")] // the tag as the shelves offer it
    [InlineData("russian")]
    [InlineData("ru")]
    [InlineData("русский")]
    public async Task Books_ByLanguage_WrittenBeyondAToZ(string asked)
    {
        var lib = await SeedAsync();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var book = await db.BookDetails.SingleAsync(d => d.MediaItemId == lib.Books["Project Hail Mary"]);
            book.Language = "Русский";
            await db.SaveChangesAsync();
        }

        var shelves = await GetAsync<BookShelvesDto>($"/api/v1/libraries/{lib.Library}/book-shelves");
        var books = await GetAsync<PagedDto<ItemSummaryDto>>($"/api/v1/libraries/{lib.Library}/books?language={Uri.EscapeDataString(asked)}");

        Assert.Contains("Русский", shelves.Languages!);
        Assert.Equal(["Project Hail Mary"], books.Items.Select(b => b.Title));
    }

    [Fact]
    public async Task ASeries_InReadingOrder_WithTheNextOnePickedOut()
    {
        var lib = await SeedAsync();

        var series = await GetAsync<BookSeriesDto>($"/api/v1/book-series/{lib.Series["Thursday Murder Club"]}");

        Assert.Equal([("The Thursday Murder Club", "1"), ("The Man Who Died Twice", "2"), ("The Bullet That Missed", "3")], series.Books.Select(b => (b.Book.Title, b.Sequence!)));
        Assert.Equal(lib.Books["The Man Who Died Twice"], series.NextId);
    }

    [Fact]
    public async Task ABooksPage_HasItsEditionSeriesAndMoreToListenTo()
    {
        var lib = await SeedAsync();

        var detail = await GetAsync<ItemDetailDto>($"/api/v1/items/{lib.Books["The Man Who Died Twice"]}");

        var book = detail.Book!;
        Assert.Equal(("B08KF1MJ7S", "English", 38000.0), (book.Asin, book.Language, book.DurationSeconds));
        Assert.Equal([("Thursday Murder Club", "2", 3)], book.Series.Select(s => (s.Name, s.Sequence!, s.BookCount)));
        Assert.Equal(["The Thursday Murder Club", "The Bullet That Missed"], book.MoreInSeries.Select(b => b.Title));
        Assert.Empty(book.ByAuthor); // Osman's others are all in the series.
        Assert.Equal(["The Colour of Magic"], book.ReadByNarrator.Select(b => b.Title));
        Assert.Equal(["Chapter 1", "Chapter 2"], book.Chapters.Select(c => c.Title));
        Assert.Contains(detail.Credits, c => c is { Role: CreditRole.Author, Name: "Richard Osman" });
    }

    [Fact]
    public async Task Home_HasContinueListening_NextInSeries_AndNewAudiobooks()
    {
        var lib = await SeedAsync();

        var home = await GetAsync<JsonElement>("/api/v1/home");
        var rows = home.GetProperty("rows").EnumerateArray().ToDictionary(r => r.GetProperty("id").GetString()!, r => r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList());

        Assert.Contains(lib.Books["The Man Who Died Twice"], rows[BookRows.ContinueRowId]);
        Assert.Contains(lib.Books["Guards! Guards!"], rows[BookRows.NextRowId]);
        Assert.Contains(lib.Books["Project Hail Mary"], rows[BookRows.NewRowId]);
        Assert.DoesNotContain(lib.Books["The Man Who Died Twice"], rows.GetValueOrDefault("continue") ?? []);

        var layout = await GetAsync<JsonElement>("/api/v1/settings/layout");
        Assert.Contains(layout.GetProperty("home").EnumerateArray(), r => r.GetProperty("id").GetString() == BookRows.NewRowId);
    }

    [Fact]
    public async Task Search_FindsBooksAndSeries_ApartFromFilmsAndShows()
    {
        var lib = await SeedAsync();

        var results = await GetAsync<SearchResultsDto>("/api/v1/search?q=Guards");
        var series = await GetAsync<SearchResultsDto>("/api/v1/search?q=Discworld");

        // Other tests' libraries have their own copies, so by title and kind rather than id.
        _ = lib;
        Assert.Contains(results.Books!, b => b.Title == "Guards! Guards!");
        Assert.DoesNotContain(results.Titles, t => t.Kind == MediaKind.Audiobook);
        Assert.Contains(series.BookSeries!, s => s.Name == "Discworld" && s.BookCount == 2);
    }

    [Fact]
    public async Task ASmartCollection_OfFinishedBooks()
    {
        var lib = await SeedAsync();
        var created = await Client().PostAsJsonAsync(new Uri("/api/v1/collections", UriKind.Relative),
            new { name = $"Finished {Guid.NewGuid():N}", rules = new { type = "book", watched = "watched", libraryIds = new[] { lib.Library } } });
        Assert.True(created.IsSuccessStatusCode, await created.Content.ReadAsStringAsync());
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var collection = await GetAsync<JsonElement>($"/api/v1/collections/{id}");

        Assert.Equal(new[] { lib.Books["The Colour of Magic"], lib.Books["The Thursday Murder Club"] }.Order(),
            collection.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
    }

    [Fact]
    public async Task OtherLibraries_AreNotBookLibraries()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var films = new Library { Name = $"Films {Guid.NewGuid():N}", Type = LibraryType.Movies };
        db.Libraries.Add(films);
        await db.SaveChangesAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await Client().GetAsync(new Uri($"/api/v1/libraries/{films.Id}/book-shelves", UriKind.Relative))).StatusCode);
    }

    private async Task<T> GetAsync<T>(string url)
    {
        var response = await Client().GetAsync(new Uri(url, UriKind.Relative));
        Assert.True(response.IsSuccessStatusCode, $"{url}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(Json))!;
    }

    private HttpClient Client()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    internal sealed record Seeded(Guid Library, Dictionary<string, Guid> Books, Dictionary<string, Guid> Series, Dictionary<string, Guid> People);

    /// <summary>
    /// Two series and a book on its own: Thursday Murder Club 1 finished and 2 under way, Discworld 1 finished and 2
    /// not started, and Project Hail Mary untouched.
    /// </summary>
    internal static async Task<Seeded> SeedAsync(IServiceProvider services, string profileId = PlaybackState.DefaultUser)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Audiobooks {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        db.Libraries.Add(library);
        var people = new Dictionary<string, Person>();
        Person Person(string name) => people.TryGetValue(name, out var p) ? p : people[name] = new Person { Name = name };
        var series = new Dictionary<string, BookSeries>();
        BookSeries Series(string name) => series.TryGetValue(name, out var s) ? s : series[name] = new BookSeries { LibraryId = library.Id, Name = name, SortName = SortTitle.For(name) };
        var books = new Dictionary<string, MediaItem>();
        var day = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

        void Book(string title, string author, string narrator, double hours, string? inSeries = null, string? sequence = null, string? asin = null)
        {
            var book = new MediaItem
            {
                LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = SortTitle.For(title), Path = $"/books/{library.Id}/{author}/{title}",
                AddedAt = day.AddDays(books.Count),
                Book = new BookDetails { DurationSeconds = hours * 3600, Asin = asin, Language = "English" },
                Files = [new MediaFile { Path = $"/books/{library.Id}/{author}/{title}/book.m4b", DurationSeconds = hours * 3600, Container = "mov,mp4,m4a,3gp,3g2,mj2", AudioCodec = "aac", BookOrder = 0, Size = 1000 }],
                Credits = [new Credit { Person = Person(author), Role = CreditRole.Author }, new Credit { Person = Person(narrator), Role = CreditRole.Narrator }],
            };
            books[title] = book;
            db.MediaItems.Add(book);
            if (inSeries is not null)
            {
                db.BookSeriesEntries.Add(new BookSeriesEntry { Series = Series(inSeries), Book = book, Sequence = sequence, SortOrder = BookAssembler.SequenceOrder(sequence) });
            }
        }

        Book("The Thursday Murder Club", "Richard Osman", "Lesley Manville", 10, "Thursday Murder Club", "1");
        Book("The Man Who Died Twice", "Richard Osman", "Lesley Manville", 38000 / 3600.0, "Thursday Murder Club", "2", "B08KF1MJ7S");
        Book("The Bullet That Missed", "Richard Osman", "Lesley Manville", 11, "Thursday Murder Club", "3");
        Book("The Colour of Magic", "Terry Pratchett", "Lesley Manville", 7, "Discworld", "1");
        Book("Guards! Guards!", "Terry Pratchett", "Nigel Planer", 9, "Discworld", "8");
        Book("Project Hail Mary", "Andy Weir", "Ray Porter", 16);
        db.MediaChapters.AddRange(
            new MediaChapter { MediaItemId = books["The Man Who Died Twice"].Id, Index = 0, Title = "Chapter 1", StartSeconds = 0, EndSeconds = 1000 },
            new MediaChapter { MediaItemId = books["The Man Who Died Twice"].Id, Index = 1, Title = "Chapter 2", StartSeconds = 1000, EndSeconds = 38000 });
        db.PlaybackStates.AddRange(
            new PlaybackState { UserId = profileId, MediaItemId = books["The Thursday Murder Club"].Id, Watched = true, FinishedAt = day.AddDays(20), LastPlayedAt = day.AddDays(20) },
            new PlaybackState { UserId = profileId, MediaItemId = books["The Man Who Died Twice"].Id, PositionSeconds = 1234, LastPlayedAt = day.AddDays(21) },
            new PlaybackState { UserId = profileId, MediaItemId = books["The Colour of Magic"].Id, Watched = true, FinishedAt = day.AddDays(10), LastPlayedAt = day.AddDays(10) });
        await db.SaveChangesAsync();
        return new Seeded(library.Id, books.ToDictionary(b => b.Key, b => b.Value.Id), series.ToDictionary(s => s.Key, s => s.Value.Id), people.ToDictionary(p => p.Key, p => p.Value.Id));
    }

    private Task<Seeded> SeedAsync() => SeedAsync(factory.Services);
}
