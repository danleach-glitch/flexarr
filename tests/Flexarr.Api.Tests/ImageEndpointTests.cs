using System.Net;
using Flexarr.Api.Endpoints;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
using Flexarr.Metadata;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Flexarr.Api.Tests;

public class ImageEndpointTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Theory]
    [InlineData("https://evil.example/t/p/w500/a.jpg")]
    [InlineData("http://image.tmdb.org/t/p/w500/a.jpg")]
    [InlineData("https://image.tmdb.org/3/search/movie")]
    [InlineData("file:///etc/hosts")]
    public async Task RemoteImages_OnlyComeFromKnownArtworkPaths(string url)
    {
        var response = await factory.CreateClient().GetAsync(new Uri($"/api/v1/images/remote?url={Uri.EscapeDataString(url)}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Theory]
    [InlineData("")] // unsigned
    [InlineData("&s=00000000000000000000000000000000")]
    public async Task RemoteImages_OnlyServeLinksTheServerSigned(string signature)
    {
        var url = "https://image.tmdb.org/t/p/original/listed-on-tmdb.jpg";

        var response = await factory.CreateClient().GetAsync(new Uri($"/api/v1/images/remote?url={Uri.EscapeDataString(url)}{signature}", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SignedRemoteImage_IsServed_AndEveryTmdbSizeIsOneImage()
    {
        var artwork = new FakeArtwork();
        using var server = factory.WithWebHostBuilder(b => b.UseSetting("Flexarr:FfmpegPath", "false").ConfigureTestServices(s =>
            s.AddHttpClient<ImageCache>().ConfigurePrimaryHttpMessageHandler(() => artwork)));
        var client = server.CreateClient();

        var link = ImageEndpoints.RemoteUrl("https://image.tmdb.org/t/p/w500/signed.jpg")!;
        var other = ImageEndpoints.RemoteUrl("https://image.tmdb.org/t/p/w1280/signed.jpg")!;
        var first = await client.GetAsync(new Uri($"{link}&w=342", UriKind.Relative));
        var again = await client.GetAsync(new Uri($"{other}&w=342", UriKind.Relative));

        Assert.Equal(link, other);
        Assert.Contains(Uri.EscapeDataString("https://image.tmdb.org/t/p/original/signed.jpg"), link, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(["https://image.tmdb.org/t/p/w342/signed.jpg"], artwork.Requested);
        Assert.Null(ImageEndpoints.RemoteUrl("https://evil.example/t/p/w500/a.jpg"));
    }

    [Theory]
    [InlineData(MediaKind.Movie, "file:///etc/hosts")]
    [InlineData(MediaKind.Movie, "embedded:///etc/hosts")]
    [InlineData(MediaKind.Series, "http://image.tmdb.org/t/p/original/a.jpg")]
    [InlineData(MediaKind.Series, "frame:///etc/hosts#1")]
    [InlineData(MediaKind.Movie, "file:///media/../etc/hosts")]
    public async Task ArtworkFromMetadata_IsNeverALocalFile(MediaKind kind, string poster)
    {
        Guid id;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
            var library = new Library { Name = $"Lib {Guid.NewGuid():N}", Type = kind == MediaKind.Movie ? LibraryType.Movies : LibraryType.TvShows };
            var item = new MediaItem { LibraryId = library.Id, Kind = kind, Title = "Sneaky", SortTitle = "sneaky", PosterUrl = poster };
            db.AddRange(library, item);
            await db.SaveChangesAsync();
            id = item.Id;
        }

        var response = await factory.CreateClient().GetAsync(new Uri($"/api/v1/images/items/{id}/poster", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("book.m4b")]
    [InlineData("01.mp3")]
    public async Task ABooksCoverInsideItsFile_IsServed(string name)
    {
        if (!await HasFfmpegAsync())
        {
            return; // CI without ffmpeg: nothing to make the book with
        }

        var root = Directory.CreateTempSubdirectory("flexarr-book-cover-").FullName;
        try
        {
            var book = Path.Combine(root, "Andy Weir", "Project Hail Mary", name);
            Directory.CreateDirectory(Path.GetDirectoryName(book)!);
            await RunAsync("ffmpeg", "-v", "error", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=22050", "-f", "lavfi", "-i", "color=c=red:s=64x64",
                "-map", "0:a", "-map", "1:v", "-t", "1", "-frames:v", "1", "-c:a", name.EndsWith(".mp3", StringComparison.Ordinal) ? "libmp3lame" : "aac",
                "-c:v", "mjpeg", "-disposition:v", "attached_pic", book);
            Guid id, outside;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = root }] };
                var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "Project Hail Mary", SortTitle = "project hail mary", PosterUrl = $"embedded://{book}" };
                // A book whose embedded:// points outside its library's folders is still refused.
                var stray = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "Stray", SortTitle = "stray", PosterUrl = "embedded:///etc/hosts" };
                db.AddRange(library, item, stray);
                await db.SaveChangesAsync();
                (id, outside) = (item.Id, stray.Id);
            }

            var client = factory.CreateClient();
            var response = await client.GetAsync(new Uri($"/api/v1/images/items/{id}/poster?w=64", UriKind.Relative));
            var refused = await client.GetAsync(new Uri($"/api/v1/images/items/{outside}/poster", UriKind.Relative));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType?.MediaType);
            Assert.True((await response.Content.ReadAsByteArrayAsync()).Length > 0);
            Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AReplacedCover_IsServedAnew()
    {
        if (!await HasFfmpegAsync())
        {
            return; // CI without ffmpeg: nothing to make the covers with
        }

        var root = Directory.CreateTempSubdirectory("flexarr-cover-").FullName;
        try
        {
            var cover = Path.Combine(root, "Book", "cover.jpg");
            Directory.CreateDirectory(Path.GetDirectoryName(cover)!);
            await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=red:s=64x64", "-frames:v", "1", cover);
            Guid id;
            using (var scope = factory.Services.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<FlexarrDbContext>();
                var library = new Library { Name = $"Books {Guid.NewGuid():N}", Type = LibraryType.Audiobooks, Folders = [new LibraryFolder { Path = root }] };
                var item = new MediaItem { LibraryId = library.Id, Kind = MediaKind.Audiobook, Title = "Book", SortTitle = "book", PosterUrl = $"file://{cover}" };
                db.AddRange(library, item);
                await db.SaveChangesAsync();
                id = item.Id;
            }

            var client = factory.CreateClient();
            var before = await client.GetByteArrayAsync(new Uri($"/api/v1/images/items/{id}/poster?w=64", UriKind.Relative));
            await RunAsync("ffmpeg", "-v", "error", "-y", "-f", "lavfi", "-i", "color=c=blue:s=128x128", "-frames:v", "1", cover);
            File.SetLastWriteTimeUtc(cover, DateTime.UtcNow.AddMinutes(1));
            var after = await client.GetByteArrayAsync(new Uri($"/api/v1/images/items/{id}/poster?w=64&v=2", UriKind.Relative));

            Assert.NotEqual(before, after);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<bool> HasFfmpegAsync()
    {
        try
        {
            await RunAsync("ffmpeg", "-version");
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task RunAsync(string program, params string[] args)
    {
        var start = new System.Diagnostics.ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true };
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

    /// <summary>TMDB, with every image the same tiny JPEG.</summary>
    private sealed class FakeArtwork : HttpMessageHandler
    {
        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requested)
            {
                Requested.Add(request.RequestUri!.ToString());
            }

            byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(jpeg) });
        }
    }
}
