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
