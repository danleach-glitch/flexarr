using System.Net;
using Flexarr.Core.Data;
using Flexarr.Core.Libraries;
using Flexarr.Core.Media;
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
}
