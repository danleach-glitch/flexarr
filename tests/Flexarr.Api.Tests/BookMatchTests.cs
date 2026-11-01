using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Books;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Fix match for books, and the chapter tools.</summary>
public class BookMatchTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    [Fact]
    public async Task FixMatch_SearchesThenAppliesAndLocksTheChoice()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "TMC (rip)", 3600);

        var results = await client.GetFromJsonAsync<List<BookMatchDto>>(new Uri($"/api/v1/items/{book}/book-match?q=thursday%20murder%20club&author=osman", UriKind.Relative), Json);
        var chosen = Assert.Single(results!);
        var applied = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{book}/book-match", UriKind.Relative),
            new { provider = chosen.Provider, id = chosen.Id, query = "thursday murder club", author = "osman" });

        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        var detail = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{book}", UriKind.Relative));
        Assert.Equal("The Thursday Murder Club", detail.GetProperty("item").GetProperty("title").GetString());
        Assert.True(detail.GetProperty("matchLocked").GetBoolean());
        Assert.Equal("B0TESTASIN", detail.GetProperty("book").GetProperty("asin").GetString());
    }

    [Fact]
    public async Task FixMatch_OntoABookAlreadyInThatSeries_RenumbersIt()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "TMC (rip)", 3600, series: ("Thursday Murder Club", "7"));

        var applied = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{book}/book-match", UriKind.Relative),
            new { provider = "audible", id = "B0TESTASIN", query = "thursday murder club", author = "osman" });

        Assert.Equal(HttpStatusCode.NoContent, applied.StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var entry = await db.BookSeriesEntries.Include(e => e.Series).SingleAsync(e => e.BookId == book);
        Assert.Equal(("Thursday Murder Club", "1", 1.0), (entry.Series!.Name, entry.Sequence!, entry.SortOrder!.Value));
    }

    [Fact]
    public async Task Edit_ReplacesTheDetails_DropsFormatGenres_AndLocks()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "TMC (rip)", 3600, series: ("Thursday Murder Club", "7"));

        var bad = await client.PutAsJsonAsync(new Uri($"/api/v1/items/{book}/book-details", UriKind.Relative), new { title = " " });
        var saved = await client.PutAsJsonAsync(new Uri($"/api/v1/items/{book}/book-details", UriKind.Relative), new
        {
            title = "The Thursday Murder Club", subtitle = "A Novel", authors = new[] { "Richard Osman" }, narrators = new[] { "Lesley Manville" },
            series = new[] { new { name = "Thursday Murder Club", sequence = "1" } }, genres = new[] { "Audiobook", "Mystery", "Audiobooks" },
            overview = "Four friends.", year = 2020, locked = true,
        });

        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, saved.StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var item = await db.MediaItems.Include(i => i.Book).Include(i => i.Genres).Include(i => i.Credits).ThenInclude(c => c.Person).SingleAsync(i => i.Id == book);
        Assert.Equal(("The Thursday Murder Club", "A Novel", 2020, true), (item.Title, item.Book!.Subtitle!, item.Year!.Value, item.MatchLocked));
        Assert.Equal(["Mystery"], item.Genres.Select(g => g.Name));
        Assert.Equal(["Richard Osman"], item.Credits.Where(c => c.Role == CreditRole.Author).Select(c => c.Person!.Name));
        var entry = await db.BookSeriesEntries.Include(e => e.Series).SingleAsync(e => e.BookId == book);
        Assert.Equal(("Thursday Murder Club", "1"), (entry.Series!.Name, entry.Sequence!));

        // Emptied lists clear: no series, no narrators.
        var cleared = await client.PutAsJsonAsync(new Uri($"/api/v1/items/{book}/book-details", UriKind.Relative),
            new { title = "The Thursday Murder Club", authors = new[] { "Richard Osman" }, locked = false });
        Assert.Equal(HttpStatusCode.NoContent, cleared.StatusCode);
        using var after = app.Services.CreateScope();
        var db2 = after.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.False(await db2.BookSeriesEntries.AnyAsync(e => e.BookId == book));
        Assert.False(await db2.Credits.AnyAsync(c => c.MediaItemId == book && c.Role == CreditRole.Narrator));
        Assert.False((await db2.MediaItems.SingleAsync(i => i.Id == book)).MatchLocked);
    }

    [Fact]
    public async Task Chapters_RenamedAndRetimed_ThenShifted_ThenAudibles()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "The Thursday Murder Club", 600, asin: "B0TESTASIN");

        var edited = await client.PutAsJsonAsync(new Uri($"/api/v1/items/{book}/chapters", UriKind.Relative),
            new { chapters = new[] { new { title = "Two", startSeconds = 300.0 }, new { title = "One", startSeconds = 0.0 } } });
        var shifted = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{book}/chapters/shift", UriKind.Relative), new { seconds = 10 });
        var audible = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{book}/chapters/audible", UriKind.Relative), new { shiftSeconds = 2 });

        Assert.Equal([("One", 0.0, 300.0), ("Two", 300.0, 600.0)], (await edited.Content.ReadFromJsonAsync<List<BookChapterDto>>(Json))!.Select(c => (c.Title, c.StartSeconds, c.EndSeconds)));
        Assert.Equal([0.0, 310.0], (await shifted.Content.ReadFromJsonAsync<List<BookChapterDto>>(Json))!.Select(c => c.StartSeconds));
        Assert.Equal([("Opening Credits", 0.0), ("Chapter 1", 32.0)], (await audible.Content.ReadFromJsonAsync<List<BookChapterDto>>(Json))!.Select(c => (c.Title, c.StartSeconds)));
        using var scope = app.Services.CreateScope();
        Assert.Equal(ChapterSource.Audible, (await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().BookDetails.SingleAsync(d => d.MediaItemId == book)).ChapterSource);
    }

    [Fact]
    public async Task AudibleChapters_Previewed_AreNotSaved()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "The Thursday Murder Club", 600, asin: "B0TESTASIN");

        var preview = await client.GetFromJsonAsync<List<BookChapterDto>>(new Uri($"/api/v1/items/{book}/chapters/audible", UriKind.Relative), Json);

        Assert.Equal("Opening Credits", preview![0].Title);
        Assert.Equal(0.0, preview[0].StartSeconds);
        Assert.Equal(600.0, preview[^1].EndSeconds);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.NotEqual(ChapterSource.Audible, (await db.BookDetails.SingleAsync(d => d.MediaItemId == book)).ChapterSource);
        Assert.DoesNotContain(await db.MediaChapters.Where(c => c.MediaItemId == book).Select(c => c.Title).ToListAsync(), t => t == "Opening Credits");
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("601")]
    [InlineData("-1e9")]
    public async Task AudibleChapters_ShiftedOutOfRange_AreRefused(string shift)
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "The Thursday Murder Club", 600, asin: "B0TESTASIN");

        var preview = await client.GetAsync(new Uri($"/api/v1/items/{book}/chapters/audible?shiftSeconds={shift}", UriKind.Relative));
        var saved = await client.PostAsync(new Uri($"/api/v1/items/{book}/chapters/audible", UriKind.Relative),
            new StringContent($$"""{"shiftSeconds": {{(double.TryParse(shift, out var n) && double.IsFinite(n) ? shift : "1e9")}}}""", System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal((HttpStatusCode.BadRequest, HttpStatusCode.BadRequest), (preview.StatusCode, saved.StatusCode));
    }

    [Fact]
    public async Task AudibleChapters_ShiftedPastTheEnd_LeaveTheBooksOwn()
    {
        using var app = Serve();
        var client = Client(app);
        var book = await SeedAsync(app, "The Thursday Murder Club", 600, asin: "B0TESTASIN");
        await client.PutAsJsonAsync(new Uri($"/api/v1/items/{book}/chapters", UriKind.Relative),
            new { chapters = new[] { new { title = "One", startSeconds = 0.0 }, new { title = "Two", startSeconds = 300.0 } } });

        var saved = await client.PostAsJsonAsync(new Uri($"/api/v1/items/{book}/chapters/audible", UriKind.Relative), new { shiftSeconds = 600 });

        Assert.Equal(HttpStatusCode.NotFound, saved.StatusCode);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        Assert.Equal(["One", "Two"], await db.MediaChapters.Where(c => c.MediaItemId == book).OrderBy(c => c.Index).Select(c => c.Title).ToListAsync());
    }

    [Theory]
    [InlineData("""{"chapters": []}""")]
    [InlineData("""{"chapters": [{"title": "", "startSeconds": 0}]}""")]
    [InlineData("""{"chapters": [{"title": "A", "startSeconds": 0}, {"title": "B", "startSeconds": 0}]}""")]
    public async Task BadChapterEdits_AreRefused(string body)
    {
        using var app = Serve();
        var book = await SeedAsync(app, "Book", 600);

        var response = await Client(app).PutAsync(new Uri($"/api/v1/items/{book}/chapters", UriKind.Relative), new StringContent(body, System.Text.Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Serve() => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(s => s.AddHttpClient("books").ConfigurePrimaryHttpMessageHandler(() => new Audible())));

    private static HttpClient Client(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private static async Task<Guid> SeedAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app, string title, double seconds, string? asin = null,
        (string Name, string Sequence)? series = null)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = title.ToLowerInvariant(), Path = $"/b/{Guid.NewGuid():N}",
            Book = new BookDetails { DurationSeconds = seconds, Asin = asin },
        };
        db.AddRange(library, book);
        if (series is var (name, sequence))
        {
            var stored = new BookSeries { LibraryId = library.Id, Name = name, SortName = name.ToLowerInvariant() };
            db.AddRange(stored, new BookSeriesEntry { SeriesId = stored.Id, BookId = book.Id, Sequence = sequence, SortOrder = BookAssembler.SequenceOrder(sequence) });
        }

        await db.SaveChangesAsync();
        return book.Id;
    }

    /// <summary>Audible's catalogue and Audnexus, with one book; everything else (Open Library…) has nothing.</summary>
    private sealed class Audible : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!;
            var body = (url.Host, url.AbsolutePath) switch
            {
                (_, "/1.0/catalog/products") => """
                    {"products": [{"asin": "B0TESTASIN", "title": "The Thursday Murder Club", "authors": [{"name": "Richard Osman"}],
                      "narrators": [{"name": "Lesley Manville"}], "series": [{"title": "Thursday Murder Club", "sequence": "1"}], "runtime_length_min": 745}]}
                    """,
                ("api.audnex.us", "/books/B0TESTASIN") => """
                    {"asin": "B0TESTASIN", "title": "The Thursday Murder Club", "authors": [{"name": "Richard Osman"}], "narrators": [{"name": "Lesley Manville"}],
                     "seriesPrimary": {"name": "Thursday Murder Club", "position": "1"}, "publisherName": "Penguin", "releaseDate": "2020-09-03T00:00:00.000Z"}
                    """,
                ("api.audnex.us", "/books/B0TESTASIN/chapters") => """
                    {"isAccurate": false, "runtimeLengthMs": 44718463, "chapters": [{"title": "Opening Credits", "startOffsetMs": 0, "lengthMs": 30000},
                     {"title": "Chapter 1", "startOffsetMs": 30000, "lengthMs": 500000}]}
                    """,
                _ => null,
            };
            return Task.FromResult(body is null ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
