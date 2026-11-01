using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Readarr, Bookshelf or Chaptarr: upcoming books, books on their way, and deleting a book through it.</summary>
public class AudiobookArrTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Fact]
    public async Task ComingSoonAndDownloads_IncludeBooks()
    {
        await LibraryAsync();
        var arr = new FakeArr();
        var release = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd'T'00:00:00'Z'", System.Globalization.CultureInfo.InvariantCulture);
        arr.Responses["GET //bookshelf.test" + CalendarPath()] = $$"""
            [{"id": 21, "title": "The Impossible Fortune", "releaseDate": "{{release}}", "author": {"authorName": "Richard Osman"},
              "images": [{"coverType": "cover", "remoteUrl": "https://covers.example/tif.jpg"}]}]
            """;
        arr.Responses["GET //bookshelf.test/api/v1/queue?page=1&pageSize=200&includeAuthor=true&includeBook=true"] = """
            {"totalRecords": 1, "records": [{"id": 7, "bookId": 21, "title": "TIF", "size": 100, "sizeleft": 25, "status": "downloading",
              "book": {"id": 21, "title": "The Impossible Fortune", "authorTitle": "osman, richard"}}]}
            """;
        using var app = arr.Serve(factory, bookshelf: true);
        var client = Client(app.CreateClient());
        UpcomingEndpoints.ForgetArrCalls();

        var upcoming = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/upcoming?days=14", UriKind.Relative));
        var downloads = await client.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/downloads", UriKind.Relative));

        var book = upcoming.EnumerateArray().Single(u => u.GetProperty("kind").GetString() == "Audiobook");
        Assert.Equal(("The Impossible Fortune", "Richard Osman"), (book.GetProperty("title").GetString(), book.GetProperty("seriesTitle").GetString()));
        Assert.Equal(0.75, book.GetProperty("download").GetProperty("progress").GetDouble());
        var downloading = downloads.EnumerateArray().Single(d => d.GetProperty("kind").GetString() == "Audiobook");
        Assert.Equal("The Impossible Fortune", downloading.GetProperty("title").GetString());
    }

    [Fact]
    public async Task DeletingABook_UnmonitorsItThenDeletesItsOwnFilesInBookshelf()
    {
        var library = await LibraryAsync();
        var bookId = await BookAsync(library, managedBy: "Bookshelf", arrId: "12");
        var arr = new FakeArr();
        arr.Responses["PUT //bookshelf.test/api/v1/book/monitor"] = "[]";
        arr.Responses["GET //bookshelf.test/api/v1/bookfile?bookId=12"] = """[{"id": 40, "bookId": 12, "path": "/data/audiobooks/book12/x.m4b"}]""";
        arr.Responses["DELETE //bookshelf.test/api/v1/bookfile/40"] = "{}";
        using var app = arr.Serve(factory, bookshelf: true);
        var client = Client(app.CreateClient());

        var preview = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{bookId}/deletion", UriKind.Relative));
        var deleted = await client.DeleteAsync(new Uri($"/api/v1/items/{bookId}", UriKind.Relative));

        Assert.Equal("Bookshelf", preview.GetProperty("managedBy").GetString());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(["GET /api/v1/bookfile?bookId=12", "GET /api/v1/bookfile?bookId=12", "PUT /api/v1/book/monitor", "DELETE /api/v1/bookfile/40"],
            arr.Requests.Where(r => r.Contains("book", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/items/{bookId}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task ABookMatchedByHand_IsStillDeletedThroughItsApp()
    {
        // Fix match makes the online provider its MetadataSource; the app its id came from still owns its files.
        var library = await LibraryAsync();
        var bookId = await BookAsync(library, managedBy: "Bookshelf", arrId: "14", source: "audible");
        var arr = new FakeArr();
        arr.Responses["PUT //bookshelf.test/api/v1/book/monitor"] = "[]";
        arr.Responses["GET //bookshelf.test/api/v1/bookfile?bookId=14"] = """[{"id": 41, "bookId": 14, "path": "/data/audiobooks/book14/y.m4b"}]""";
        arr.Responses["DELETE //bookshelf.test/api/v1/bookfile/41"] = "{}";
        using var app = arr.Serve(factory, bookshelf: true);
        var client = Client(app.CreateClient());

        var deleted = await client.DeleteAsync(new Uri($"/api/v1/items/{bookId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Equal(["GET /api/v1/bookfile?bookId=14", "PUT /api/v1/book/monitor", "DELETE /api/v1/bookfile/41"],
            arr.Requests.Where(r => r.Contains("book", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ABookTheAppHasNoFilesFor_IsDeletedFromDisk_AndA404OnAFileIsDone()
    {
        var root = Directory.CreateTempSubdirectory("flexarr-book-delete-").FullName;
        try
        {
            // Book 15: Bookshelf knows it but not its files (imported by hand). Book 16: its one file is already gone there.
            var (unfiled, unfiledFile) = await BookOnDiskAsync(root, "Unfiled", "15");
            var (gone, goneFile) = await BookOnDiskAsync(root, "Gone", "16");
            var arr = new FakeArr();
            arr.Responses["PUT //bookshelf.test/api/v1/book/monitor"] = "[]";
            arr.Responses["GET //bookshelf.test/api/v1/bookfile?bookId=15"] = "[]";
            arr.Responses["GET //bookshelf.test/api/v1/bookfile?bookId=16"] = $$"""[{"id": 42, "bookId": 16, "path": "{{goneFile}}"}]""";
            using var app = arr.Serve(factory, bookshelf: true); // DELETE bookfile/42 is a 404
            var client = Client(app.CreateClient());

            var first = await client.DeleteAsync(new Uri($"/api/v1/items/{unfiled}", UriKind.Relative));
            var second = await client.DeleteAsync(new Uri($"/api/v1/items/{gone}", UriKind.Relative));

            Assert.Equal((HttpStatusCode.NoContent, HttpStatusCode.NoContent), (first.StatusCode, second.StatusCode));
            Assert.False(File.Exists(unfiledFile)); // nobody else would have deleted it
            Assert.True(File.Exists(goneFile)); // the app's to delete, not Flexarr's
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/items/{unfiled}", UriKind.Relative))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri($"/api/v1/items/{gone}", UriKind.Relative))).StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ABookLinkedToAnotherEditionThere_IsDeletedFromDisk_AndTheAppIsLeftAlone()
    {
        // Only an ebook app: our audiobook "Dune" matched its ebook by title and author. Its files are the ebook's.
        var root = Directory.CreateTempSubdirectory("flexarr-book-delete-").FullName;
        try
        {
            var (book, file) = await BookOnDiskAsync(root, "Dune", "17");
            var arr = new FakeArr();
            arr.Responses["GET //bookshelf.test/api/v1/bookfile?bookId=17"] = """[{"id": 43, "bookId": 17, "path": "/data/ebooks/Frank Herbert/Dune.epub"}]""";
            using var app = arr.Serve(factory, bookshelf: true);
            var client = Client(app.CreateClient());

            var preview = await client.GetFromJsonAsync<JsonElement>(new Uri($"/api/v1/items/{book}/deletion", UriKind.Relative));
            var deleted = await client.DeleteAsync(new Uri($"/api/v1/items/{book}", UriKind.Relative));

            Assert.Equal(JsonValueKind.Null, preview.GetProperty("managedBy").ValueKind);
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            Assert.False(File.Exists(file));
            Assert.DoesNotContain(arr.Requests, r => r.StartsWith("PUT", StringComparison.Ordinal) || r.StartsWith("DELETE", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<(Guid Id, string File)> BookOnDiskAsync(string root, string title, string arrId)
    {
        var folder = Path.Combine(root, title, title); // a library of its own, at root/title
        Directory.CreateDirectory(folder);
        var file = Path.Combine(folder, "book.m4b");
        await File.WriteAllTextAsync(file, "x");
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = Path.Combine(root, title) }] };
        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = title, SortTitle = title.ToLowerInvariant(), MetadataSource = "Bookshelf", Path = folder,
            Book = new BookDetails { ArrBookId = arrId, ArrInstance = "Bookshelf" }, Files = [new MediaFile { Path = file, Size = 1, BookOrder = 0 }],
        };
        db.AddRange(library, book);
        await db.SaveChangesAsync();
        return (book.Id, file);
    }

    [Fact]
    public async Task ABookshelfThatRefuses_LeavesTheBookInFlexarr()
    {
        var library = await LibraryAsync();
        var bookId = await BookAsync(library, managedBy: "Bookshelf", arrId: "13");
        using var app = new FakeArr().Serve(factory, bookshelf: true); // every call a 404
        var client = Client(app.CreateClient());

        var deleted = await client.DeleteAsync(new Uri($"/api/v1/items/{bookId}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadGateway, deleted.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri($"/api/v1/items/{bookId}", UriKind.Relative))).StatusCode);
    }

    private const string Lookup = """
        [{"id": 0, "foreignBookId": "hc:1", "title": "The Impossible Fortune", "releaseDate": "2025-09-25T00:00:00Z", "monitored": false,
          "author": {"authorName": "Richard Osman", "foreignAuthorId": "hc:9"}, "editions": [{"monitored": true}],
          "images": [{"coverType": "cover", "remoteUrl": "https://covers.example/tif.jpg"}]},
         {"id": 13, "foreignBookId": "hc:2", "title": "The Man Who Died Twice", "monitored": false, "author": {"authorName": "Richard Osman"}},
         {"id": 12, "foreignBookId": "hc:3", "title": "The Thursday Murder Club", "monitored": true, "author": {"authorName": "Richard Osman"}}]
        """;

    private static FakeArr BookshelfForRequests()
    {
        var arr = new FakeArr();
        arr.Responses["GET //bookshelf.test/api/v1/book/lookup?term=osman"] = Lookup;
        arr.Responses["GET //bookshelf.test/api/v1/qualityprofile"] = """[{"id": 1, "name": "eBook"}, {"id": 2, "name": "Spoken"}]""";
        arr.Responses["GET //bookshelf.test/api/v1/metadataprofile"] = """[{"id": 1, "name": "Standard"}, {"id": 2, "name": "None"}]""";
        arr.Responses["GET //bookshelf.test/api/v1/rootfolder"] = """[{"path": "/data/audiobooks"}]""";
        arr.Responses["POST //bookshelf.test/api/v1/book"] = """{"id": 21, "foreignBookId": "hc:1"}""";
        arr.Responses["PUT //bookshelf.test/api/v1/book/monitor"] = "[]";
        arr.Responses["POST //bookshelf.test/api/v1/command"] = """{"id": 99}""";
        return arr;
    }

    [Fact]
    public async Task BookSearch_SaysWhatsInTheLibrary_Requested_OrAvailable()
    {
        var library = await LibraryAsync();
        var owned = await BookAsync(library, managedBy: "Bookshelf", arrId: "12");
        using var app = BookshelfForRequests().Serve(factory, bookshelf: true);

        var results = await Client(app.CreateClient()).GetFromJsonAsync<JsonElement>(new Uri("/api/v1/requests/books/search?q=osman", UriKind.Relative));

        var byTitle = results.EnumerateArray().ToDictionary(r => r.GetProperty("title").GetString()!);
        Assert.Equal("Available", byTitle["The Impossible Fortune"].GetProperty("availability").GetString());
        Assert.Equal("https://covers.example/tif.jpg", byTitle["The Impossible Fortune"].GetProperty("cover").GetString());
        Assert.Equal("Available", byTitle["The Man Who Died Twice"].GetProperty("availability").GetString());
        Assert.Equal("InLibrary", byTitle["The Thursday Murder Club"].GetProperty("availability").GetString());
        Assert.Equal(owned, byTitle["The Thursday Murder Club"].GetProperty("itemId").GetGuid());
    }

    [Fact]
    public async Task RequestingANewBook_AddsItWithItsAuthorFollowingNothingElse_ThenSearches()
    {
        await LibraryAsync();
        var arr = BookshelfForRequests();
        string? added = null;
        arr.OnRequest = r =>
        {
            if (r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath == "/api/v1/book")
            {
                added = r.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            }
        };
        using var app = arr.Serve(factory, bookshelf: true);

        var response = await Client(app.CreateClient()).PostAsJsonAsync(new Uri("/api/v1/requests/books", UriKind.Relative), new { foreignBookId = "hc:1", query = "osman" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var book = JsonDocument.Parse(added!).RootElement;
        Assert.True(book.GetProperty("monitored").GetBoolean());
        var author = book.GetProperty("author");
        Assert.Equal((2, 1, "/data/audiobooks", "none"), (author.GetProperty("qualityProfileId").GetInt32(), author.GetProperty("metadataProfileId").GetInt32(),
            author.GetProperty("rootFolderPath").GetString(), author.GetProperty("monitorNewItems").GetString()));
        Assert.Contains("POST /api/v1/command", arr.Requests);
        using var scope = app.Services.CreateScope();
        var request = await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().MediaRequests.SingleAsync(r => r.BookForeignId == "hc:1");
        Assert.Equal(("21", "The Impossible Fortune by Richard Osman"), (request.BookArrId, request.Title));

        // Asked again, it's Requested.
        var again = await Client(app.CreateClient()).GetFromJsonAsync<JsonElement>(new Uri("/api/v1/requests/books/search?q=osman", UriKind.Relative));
        Assert.Equal("Requested", again.EnumerateArray().Single(r => r.GetProperty("foreignBookId").GetString() == "hc:1").GetProperty("availability").GetString());
    }

    [Fact]
    public async Task RequestingABookTheAppKnows_MonitorsItInstead()
    {
        await LibraryAsync();
        var arr = BookshelfForRequests();
        using var app = arr.Serve(factory, bookshelf: true);

        var response = await Client(app.CreateClient()).PostAsJsonAsync(new Uri("/api/v1/requests/books", UriKind.Relative), new { foreignBookId = "hc:2", query = "osman" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Contains("PUT /api/v1/book/monitor", arr.Requests);
        Assert.DoesNotContain("POST /api/v1/book", arr.Requests);
    }

    [Fact]
    public async Task ComingSoon_DoesntPointIntoALibraryTheProfileCantSee()
    {
        await LibraryAsync();
        Guid hidden;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Private books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            hidden = library.Id;
        }

        var book = await BookAsync(hidden, managedBy: "Bookshelf", arrId: "31");
        var arr = new FakeArr();
        var release = DateTime.UtcNow.Date.AddDays(3).ToString("yyyy-MM-dd'T'00:00:00'Z'", System.Globalization.CultureInfo.InvariantCulture);
        arr.Responses["GET //bookshelf.test" + CalendarPath()] = $$"""[{"id": 31, "title": "Hidden Away", "releaseDate": "{{release}}"}]""";
        using var app = arr.Serve(factory, bookshelf: true);
        var admin = Client(app.CreateClient());
        var created = await (await admin.PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = $"Sam {Guid.NewGuid():N}"[..20], hiddenLibraryIds = new[] { hidden } }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var token = (await (await admin.PostAsJsonAsync(new Uri($"/api/v1/profiles/{created.GetProperty("id").GetString()}/select", UriKind.Relative), new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var sam = Client(app.CreateClient());
        sam.DefaultRequestHeaders.Add("X-Flexarr-Profile", token);

        UpcomingEndpoints.ForgetArrCalls();
        var forSam = await sam.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/upcoming?days=14", UriKind.Relative));
        var forMain = await admin.GetFromJsonAsync<JsonElement>(new Uri("/api/v1/upcoming?days=14", UriKind.Relative));

        static JsonElement Hidden(JsonElement list) => list.EnumerateArray().Single(u => u.GetProperty("title").GetString() == "Hidden Away");
        Assert.True(Hidden(forSam).GetProperty("libraryItemId").ValueKind == JsonValueKind.Null);
        Assert.Equal(book, Hidden(forMain).GetProperty("libraryItemId").GetGuid());
    }

    [Theory]
    [InlineData("{}")] // an add that says nothing of what it added
    [InlineData("<html>Sign in</html>")] // a proxy's page
    public async Task ARequestTheAppAnswersOddly_SaysSo(string answer)
    {
        await LibraryAsync();
        var arr = BookshelfForRequests();
        arr.Responses["POST //bookshelf.test/api/v1/book"] = answer;
        using var app = arr.Serve(factory, bookshelf: true);

        var response = await Client(app.CreateClient()).PostAsJsonAsync(new Uri("/api/v1/requests/books", UriKind.Relative), new { foreignBookId = "hc:1", query = "osman" });

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Contains("didn't answer the request", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    private static string CalendarPath()
    {
        var today = DateTime.UtcNow.Date;
        return $"/api/v1/calendar?start={today.AddDays(-2):yyyy-MM-dd}&end={today.AddDays(14):yyyy-MM-dd}&includeAuthor=true&unmonitored=false";
    }

    private static HttpClient Client(HttpClient client)
    {
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    private async Task<Guid> LibraryAsync()
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = await db.Libraries.IgnoreQueryFilters().FirstOrDefaultAsync(l => l.Type == LibraryType.Audiobooks);
        if (library is null)
        {
            library = new Library { Name = "Audiobooks", Type = LibraryType.Audiobooks };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        return library.Id;
    }

    private async Task<Guid> BookAsync(Guid library, string managedBy, string arrId, string? source = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var book = new MediaItem
        {
            LibraryId = library, Kind = MediaKind.Audiobook, Title = "Delete Me", SortTitle = "delete me", MetadataSource = source ?? managedBy,
            Path = $"/Volumes/Media/data/audiobooks/book{arrId}", Book = new BookDetails { ArrBookId = arrId, ArrInstance = managedBy },
        };
        db.MediaItems.Add(book);
        await db.SaveChangesAsync();
        return book.Id;
    }
}
