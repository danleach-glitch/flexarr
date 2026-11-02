using Flexarr.Metadata;

namespace Flexarr.Core.Tests;

public class MusicBrainzClientTests
{
    private static MusicBrainzClient ClientFor(string body, Action<Uri>? seen = null) => new(new HttpClient(new Canned(body, seen)));

    [Fact]
    public async Task ABarcode_FindsItsReleaseGroup_AndTheArtistCreditedOnIt()
    {
        Uri? asked = null;
        var client = ClientFor(File.ReadAllText(Path.Combine("Fixtures", "musicbrainz-release-barcode.json")), u => asked = u);

        var release = await client.ReleaseByBarcodeAsync("3618116321222", "Le Plus Beau");

        Assert.NotNull(release);
        Assert.Equal("04120832-3d22-46d7-9e76-10ab72784e11", release.ReleaseGroupId);
        var artist = Assert.Single(release.Artists);
        Assert.Equal(("801b4b13-24d8-4293-94bd-21c5b4bc420e", "TeddyBear"), (artist.Id, artist.Name));
        // A UPC and the same code as a 13-digit EAN are both asked for.
        Assert.Contains("barcode:(3618116321222 OR 03618116321222", Uri.UnescapeDataString(asked!.Query), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABarcodeMusicBrainzDoesntKnow_IsNull()
    {
        var release = await ClientFor("""{"count":0,"releases":[]}""").ReleaseByBarcodeAsync("0123456789012");

        Assert.Null(release);
    }

    [Fact]
    public async Task AnotherBarcodesRelease_IsntTaken()
    {
        var body = """{"releases":[{"title":"Else","barcode":"999999999999","release-group":{"id":"rg"},"artist-credit":[{"name":"X","artist":{"id":"a","name":"X"}}]}]}""";

        Assert.Null(await ClientFor(body).ReleaseByBarcodeAsync("123456789012"));
    }

    private sealed class Canned(string json, Action<Uri>? seen) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            seen?.Invoke(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") });
        }
    }
}
