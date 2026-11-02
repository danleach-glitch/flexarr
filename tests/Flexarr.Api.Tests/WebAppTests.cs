using System.Net;
using Microsoft.AspNetCore.Hosting;

namespace Flexarr.Api.Tests;

public sealed class WebAppTests : IClassFixture<FlexarrApiFactory>, IDisposable
{
    private readonly string _webRoot = Path.Combine(Path.GetTempPath(), "flexarr-webroot-" + Guid.NewGuid().ToString("N"));
    private readonly HttpClient _client;

    public WebAppTests(FlexarrApiFactory factory)
    {
        Directory.CreateDirectory(Path.Combine(_webRoot, "assets"));
        File.WriteAllText(Path.Combine(_webRoot, "index.html"), "<!doctype html><title>Flexarr</title>");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc.css"), "body{}");
        File.WriteAllText(Path.Combine(_webRoot, "assets", "index-abc.js"), "export {};");
        _client = factory.WithWebHostBuilder(builder => builder.UseWebRoot(_webRoot)).CreateClient();
    }

    [Theory]
    [InlineData("/assets/index-abc.css", "text/css")]
    [InlineData("/assets/index-abc.js", "text/javascript")]
    public async Task Assets_AreServedAsThemselves_NotTheWebPage(string path, string type)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/library/5")]
    public async Task AppRoutes_GetTheWebPage(string path)
    {
        using var response = await _client.GetAsync(new Uri(path, UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task AMissingAsset_IsNotFound()
    {
        using var response = await _client.GetAsync(new Uri("/assets/index-gone.js", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    public void Dispose()
    {
        _client.Dispose();
        Directory.Delete(_webRoot, recursive: true);
    }
}
