using System.Net.Http.Headers;

namespace Flexarr.Api.Tests;

public class ResponseCompressionTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    [Theory]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task ApiResponses_AreCompressed_WhenTheClientAccepts(string encoding)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        client.DefaultRequestHeaders.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        using var response = await client.GetAsync(new Uri("/api/v1/libraries", UriKind.Relative));

        response.EnsureSuccessStatusCode();
        Assert.Equal([encoding], response.Content.Headers.ContentEncoding);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
    }
}
