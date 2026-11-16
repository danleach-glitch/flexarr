using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Flexarr.Api.Playback;

namespace Flexarr.Api.Tests;

/// <summary>The apps' speed test (docs/remote-streaming.md): bytes nothing can shrink, for signed-in devices only.</summary>
public class SpeedTestTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private HttpClient Client(bool signedIn = true)
    {
        var client = factory.CreateClient();
        if (signedIn)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        }

        // Away from home, where the apps run it.
        client.DefaultRequestHeaders.Add("X-Test-Peer", "81.2.69.160");
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
        return client;
    }

    [Fact]
    public async Task SendsTwoMillionBytesByDefault_UncachedAndUncompressed()
    {
        using var response = await Client().GetAsync(new Uri("/api/v1/speedtest", UriKind.Relative));

        response.EnsureSuccessStatusCode();
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(SpeedTestEndpoints.DefaultBytes, bytes.Length);

        // Random: compressing it saves next to nothing, so a link that compresses can't flatter the reading.
        using var squeezed = new MemoryStream();
        await using (var gzip = new GZipStream(squeezed, CompressionLevel.Optimal, leaveOpen: true))
        {
            await gzip.WriteAsync(bytes);
        }

        Assert.True(squeezed.Length > bytes.Length * 0.99);
    }

    [Theory]
    [InlineData(1_000_000, 1_000_000)]
    [InlineData(100_000_000, SpeedTestEndpoints.MaxBytes)]
    [InlineData(0, 1)]
    public async Task SendsWhatsAskedFor_UpToFourMebibytes(int asked, int sent)
    {
        using var response = await Client().GetAsync(new Uri($"/api/v1/speedtest?bytes={asked}", UriKind.Relative));

        response.EnsureSuccessStatusCode();
        Assert.Equal(sent, (await response.Content.ReadAsByteArrayAsync()).Length);
    }

    [Fact]
    public async Task IsOnlyForSignedInDevices()
    {
        using var response = await Client(signedIn: false).GetAsync(new Uri("/api/v1/speedtest", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
