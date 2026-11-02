using System.Net;
using System.Text;
using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

public class DeezerClientTests
{
    private static DeezerClient ClientFor(string fixture, Action<Uri>? seen = null) =>
        new(new HttpClient(new Canned(File.ReadAllText(Path.Combine("Fixtures", fixture)), seen)));

    [Fact]
    public async Task AnAlbum_ComesWithItsTracksArtistAndYear()
    {
        Uri? asked = null;
        var album = await ClientFor("deezer-album.json", u => asked = u).GetAlbumAsync(730000661);

        Assert.Equal("https://api.deezer.com/album/730000661", asked?.ToString());
        Assert.Equal("moisturizer", album.Title);
        Assert.Equal(2025, album.Year);
        Assert.Equal("Wet Leg", album.Artist?.Name);
        Assert.StartsWith("https://cdn-images.dzcdn.net/images/cover/", album.Cover, StringComparison.Ordinal);
        Assert.Equal(3, album.Tracks?.Data.Count);
        Assert.All(album.Tracks!.Data, t => Assert.True(t.Duration > 0));
    }

    [Fact]
    public async Task ArtistSearch_ReadsPhotosAndHowWellKnownTheyAre()
    {
        Uri? asked = null;
        var artists = await ClientFor("deezer-search-artist.json", u => asked = u).SearchArtistsAsync("wet leg", 3);

        Assert.Contains("q=wet%20leg", asked?.AbsoluteUri, StringComparison.Ordinal);
        Assert.Equal("Wet Leg", artists[0].Name);
        Assert.True(artists[0].NbFan > 0);
        Assert.StartsWith("https://cdn-images.dzcdn.net/images/artist/", artists[0].Picture, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Genres_LeaveOutAll()
    {
        var genres = await ClientFor("deezer-genres.json").GetGenresAsync();

        Assert.DoesNotContain(genres, g => g.Id == 0);
        Assert.Contains(genres, g => g.Name == "Pop");
    }

    [Fact]
    public async Task AnErrorAnswer_Throws_WithDeezersCode()
    {
        var error = await Assert.ThrowsAsync<DeezerException>(() => ClientFor("deezer-error.json").GetArtistAsync(1));

        Assert.Equal(4, error.Code);
        Assert.Contains("Quota", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void APlaceholderImage_IsNoImage()
    {
        var artist = new DeezerArtist(1, "Nobody", PictureXl: "https://cdn-images.dzcdn.net/images/artist//1000x1000-000000-80-0-0.jpg");

        Assert.Null(artist.Picture);
    }

    private sealed class Canned(string json, Action<Uri>? seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen?.Invoke(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
