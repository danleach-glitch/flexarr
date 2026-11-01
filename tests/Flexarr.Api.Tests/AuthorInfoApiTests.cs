using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Core.Books;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

/// <summary>Authors' photos and biographies through the API: admins choose and upload them, and book pages show them.</summary>
public class AuthorInfoApiTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Fact]
    public async Task OnlyAnAdmin_SeesCandidates_OrChangesAPerson()
    {
        using var app = Serve();
        var (person, _) = await SeedAsync(app);
        var guest = await OtherProfileAsync(app);
        using var photo = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]);

        Assert.Equal(HttpStatusCode.Forbidden, (await guest.GetAsync(Api($"/people/{person}/candidates"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsJsonAsync(Api($"/people/{person}"), new { biography = "Mine now." })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await guest.PutAsync(Api($"/people/{person}/image"), photo)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guest.GetAsync(Api($"/people/{person}"))).StatusCode); // looking is fine
    }

    [Fact]
    public async Task TheCandidatePhotoProxy_ServesOnlyWhatTheServerFound()
    {
        using var app = Serve();
        var (person, _) = await SeedAsync(app, folderPhoto: true);
        var admin = Admin(app);
        var guest = await OtherProfileAsync(app);

        var before = await admin.GetAsync(Api($"/images/people/{person}/candidates/folder"));
        var candidates = await admin.GetFromJsonAsync<JsonElement>(Api($"/people/{person}/candidates"));
        var unknown = await admin.GetAsync(Api($"/images/people/{person}/candidates/audible"));
        var elsewhere = await admin.GetAsync(Api($"/images/people/{Guid.NewGuid()}/candidates/folder"));
        var anonymous = await app.CreateClient().GetAsync(Api($"/images/people/{person}/candidates/folder"));
        var notAdmin = await guest.GetAsync(Api($"/images/people/{person}/candidates/folder"));

        Assert.Equal(HttpStatusCode.NotFound, before.StatusCode); // nothing looked for yet
        var folder = candidates.GetProperty("photos").EnumerateArray().Single(p => p.GetProperty("source").GetString() == "folder");
        Assert.Equal($"/api/v1/images/people/{person}/candidates/folder", folder.GetProperty("image").GetString());
        Assert.Contains(candidates.GetProperty("biographies").EnumerateArray(), b => b.GetProperty("text").GetString() == "A writer.");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, elsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, notAdmin.StatusCode);
    }

    [Fact]
    public async Task ChoosingAPhoto_ChangesItsVersion_AndLocksThePerson()
    {
        using var app = Serve();
        var (person, _) = await SeedAsync(app, folderPhoto: true);
        var admin = Admin(app);
        var before = (await admin.GetFromJsonAsync<JsonElement>(Api($"/people/{person}"))).GetProperty("image").GetString();
        await admin.GetAsync(Api($"/people/{person}/candidates"));

        var chosen = await (await admin.PutAsJsonAsync(Api($"/people/{person}"), new { photo = "folder" })).Content.ReadFromJsonAsync<JsonElement>();
        var written = await (await admin.PutAsJsonAsync(Api($"/people/{person}"), new { biography = "Written here.", locked = false })).Content.ReadFromJsonAsync<JsonElement>();
        var gone = await admin.PutAsJsonAsync(Api($"/people/{person}"), new { photo = "nowhere" });

        Assert.NotEqual(before, chosen.GetProperty("image").GetString());
        Assert.Contains("?v=", chosen.GetProperty("image").GetString(), StringComparison.Ordinal);
        Assert.Equal(("folder", true), (chosen.GetProperty("photoSource").GetString(), chosen.GetProperty("locked").GetBoolean()));
        Assert.Equal(("Written here.", "manual", false), (written.GetProperty("biography").GetString(), written.GetProperty("biographySource").GetString(), written.GetProperty("locked").GetBoolean()));
        Assert.Equal(HttpStatusCode.Conflict, gone.StatusCode);
    }

    [Fact]
    public async Task UploadingAPhoto_ServesIt_AtANewVersion()
    {
        if (!await TestFfmpeg.AvailableAsync())
        {
            return; // CI without ffmpeg: nothing to convert it with
        }

        using var app = Serve();
        var (person, _) = await SeedAsync(app);
        var admin = Admin(app);
        var before = (await admin.GetFromJsonAsync<JsonElement>(Api($"/people/{person}"))).GetProperty("image").GetString();
        var png = Path.Combine(factory.DataDirectory, $"{Guid.NewGuid():N}.png");
        await TestFfmpeg.RunAsync("-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=64x48", "-frames:v", "1", png);
        using var body = new ByteArrayContent(await File.ReadAllBytesAsync(png));

        var uploaded = await (await admin.PutAsync(Api($"/people/{person}/image"), body)).Content.ReadFromJsonAsync<JsonElement>();
        var image = uploaded.GetProperty("image").GetString()!;
        var served = await app.CreateClient().GetAsync(new Uri(image, UriKind.Relative));

        Assert.NotEqual(before, image);
        Assert.Equal(("upload", true), (uploaded.GetProperty("photoSource").GetString(), uploaded.GetProperty("locked").GetBoolean()));
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    [Fact]
    public async Task AFolderPhotoOutsideTheirLibraries_IsntServed()
    {
        if (!await TestFfmpeg.AvailableAsync())
        {
            return; // CI without ffmpeg: no real photo to make, so nothing would be served either way
        }

        using var app = Serve();
        var outside = Directory.CreateTempSubdirectory("flexarr-outside-").FullName;
        try
        {
            var photo = Path.Combine(outside, "author.jpg");
            await TestFfmpeg.RunAsync("-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", photo);
            var (person, _) = await SeedAsync(app, imageUrl: $"file://{photo}");
            var (inside, _) = await SeedAsync(app, folderPhoto: true);
            await Admin(app).GetAsync(Api($"/people/{inside}/candidates"));
            var folder = await (await Admin(app).PutAsJsonAsync(Api($"/people/{inside}"), new { photo = "folder" })).Content.ReadFromJsonAsync<JsonElement>();

            var refused = await app.CreateClient().GetAsync(Api($"/images/people/{person}"));
            var served = await app.CreateClient().GetAsync(new Uri(folder.GetProperty("image").GetString()!, UriKind.Relative));

            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
            Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    [Fact]
    public async Task AnUploadWhoseFileIsGone_IsNoPhoto_AndUnlockingClearsIt()
    {
        using var app = Serve();
        var (person, _) = await SeedAsync(app, imageUrl: "file:///elsewhere/person-images/gone.jpg");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            await db.People.Where(p => p.Id == person).ExecuteUpdateAsync(u => u.SetProperty(p => p.PhotoSource, PersonSources.Upload).SetProperty(p => p.InfoLocked, true));
        }

        var admin = Admin(app);
        var shown = await admin.GetFromJsonAsync<JsonElement>(Api($"/people/{person}"));
        var unlocked = await (await admin.PutAsJsonAsync(Api($"/people/{person}"), new { locked = false })).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(JsonValueKind.Null, shown.GetProperty("image").ValueKind);
        Assert.Equal(JsonValueKind.Null, unlocked.GetProperty("photoSource").ValueKind);
        Assert.False(unlocked.GetProperty("locked").GetBoolean());
    }

    [Fact]
    public async Task Unlocking_RepliesAtOnce_AndLooksThemUpAfter()
    {
        using var app = Serve();
        var (person, _) = await SeedAsync(app, folderPhoto: true);
        using (var scope = app.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<FlexarrDbContext>().People.Where(p => p.Id == person)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.InfoLocked, true));
        }

        var admin = Admin(app);
        var unlocked = await admin.PutAsJsonAsync(Api($"/people/{person}"), new { locked = false });
        await app.Services.GetRequiredService<Flexarr.Api.Profiles.PersonLookups>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var after = await admin.GetFromJsonAsync<JsonElement>(Api($"/people/{person}"));

        Assert.Equal(HttpStatusCode.OK, unlocked.StatusCode);
        Assert.Equal(("folder", false), (after.GetProperty("photoSource").GetString(), after.GetProperty("locked").GetBoolean()));
    }

    [Fact]
    public async Task ABooksPage_ListsItsAuthors_WithPhotosAndBiographies()
    {
        using var app = Serve();
        var (person, book) = await SeedAsync(app, imageUrl: "https://images.test/writer.jpg", coAuthor: "Second Writer");

        var detail = await Admin(app).GetFromJsonAsync<JsonElement>(Api($"/items/{book}"));

        var authors = detail.GetProperty("book").GetProperty("authors").EnumerateArray().ToList();
        Assert.Equal(["A Writer", "Second Writer"], authors.Select(a => a.GetProperty("name").GetString()));
        Assert.Equal(person.ToString(), authors[0].GetProperty("id").GetString());
        Assert.StartsWith($"/api/v1/images/people/{person}?v=", authors[0].GetProperty("image").GetString(), StringComparison.Ordinal);
        Assert.Equal("A writer.", authors[0].GetProperty("biography").GetString());
        Assert.Equal(JsonValueKind.Null, authors[1].GetProperty("image").ValueKind);
    }

    /// <summary>An author with one book in a library of their own, under Author/Title, with a biography.</summary>
    private async Task<(Guid Person, Guid Book)> SeedAsync(WebApplicationFactory<Program> app, bool folderPhoto = false, string? imageUrl = null, string? coAuthor = null)
    {
        var root = Directory.CreateDirectory(Path.Combine(factory.MediaDirectory, $"books-{Guid.NewGuid():N}")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "A Writer", "A Book")).FullName;
        if (folderPhoto)
        {
            // A real photo where ffmpeg can make one, so it can be served.
            var photo = Path.Combine(root, "A Writer", "author.jpg");
            if (await TestFfmpeg.AvailableAsync())
            {
                await TestFfmpeg.RunAsync("-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=64x64", "-frames:v", "1", photo);
            }
            else
            {
                await File.WriteAllBytesAsync(photo, [0xFF, 0xD8, 0xFF]);
            }
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
        var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = root }] };
        var person = new Person
        {
            Name = "A Writer", Biography = "A writer.", BiographySource = PersonSources.Audible, ImageUrl = imageUrl,
            ImageChangedAt = imageUrl is null ? null : new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        var credits = new List<Credit> { new() { Person = person, Role = CreditRole.Author, Order = 0 } };
        if (coAuthor is not null)
        {
            credits.Add(new Credit { Person = new Person { Name = coAuthor }, Role = CreditRole.Author, Order = 1 });
        }

        var book = new MediaItem
        {
            LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "A Book", SortTitle = "a book", Path = folder,
            Book = new BookDetails { DurationSeconds = 600 }, Credits = credits,
        };
        db.AddRange(library, book);
        await db.SaveChangesAsync();
        return (person.Id, book.Id);
    }

    private WebApplicationFactory<Program> Serve() => factory.WithWebHostBuilder(b =>
        b.ConfigureTestServices(s => s.AddHttpClient("books").ConfigurePrimaryHttpMessageHandler(() => new Nowhere())));

    private static HttpClient Admin(WebApplicationFactory<Program> app)
    {
        var client = app.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A TV paired with the key, on a profile that isn't the main one: not an admin.</summary>
    private static async Task<HttpClient> OtherProfileAsync(WebApplicationFactory<Program> app)
    {
        var admin = Admin(app);
        var id = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = $"Guest {Guid.NewGuid():N}"[..20] })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Playroom TV" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var tv = app.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.Equal(HttpStatusCode.OK, (await tv.PostAsJsonAsync(Api($"/profiles/{id}/select"), new { })).StatusCode);
        Assert.False((await tv.GetFromJsonAsync<JsonElement>(Api("/me"))).GetProperty("isAdmin").GetBoolean());
        return tv;
    }

    /// <summary>Audible and Open Library, knowing nobody.</summary>
    private sealed class Nowhere : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>Running ffmpeg from a test, when it's installed.</summary>
internal static class TestFfmpeg
{
    public static async Task<bool> AvailableAsync()
    {
        try
        {
            await RunAsync("-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public static async Task RunAsync(params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = System.Diagnostics.Process.Start(start)!;
        var error = process.StandardError.ReadToEndAsync();
        await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await error);
    }
}
