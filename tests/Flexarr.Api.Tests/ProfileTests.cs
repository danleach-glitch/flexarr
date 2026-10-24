using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Api.Tests;

public class ProfileTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private HttpClient Client(string? profileToken = null)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        if (profileToken is not null)
        {
            client.DefaultRequestHeaders.Add("X-Flexarr-Profile", profileToken);
        }

        return client;
    }

    [Fact]
    public async Task MainProfileExists_AndCantBeRemoved()
    {
        var profiles = await Client().GetFromJsonAsync<JsonElement>(new Uri("/api/v1/profiles", UriKind.Relative));

        Assert.Contains(profiles.EnumerateArray(), p => p.GetProperty("isMain").GetBoolean() && p.GetProperty("isCurrent").GetBoolean());
        Assert.Equal(HttpStatusCode.Conflict, (await Client().DeleteAsync(new Uri("/api/v1/profiles/owner", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task PinProtectedProfile_NeedsThePin_AndItsTokenSelectsIt()
    {
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = "Sam", color = "#e5a0c0", pin = "2468" }))
            .Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();

        var wrong = await Client().PostAsJsonAsync(new Uri($"/api/v1/profiles/{id}/select", UriKind.Relative), new { pin = "1111" });
        var right = await Client().PostAsJsonAsync(new Uri($"/api/v1/profiles/{id}/select", UriKind.Relative), new { pin = "2468" });
        var token = (await right.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString();
        var asSam = await Client(token).GetFromJsonAsync<JsonElement>(new Uri("/api/v1/profiles", UriKind.Relative));
        var forged = await Client($"{id}.0000").GetFromJsonAsync<JsonElement>(new Uri("/api/v1/profiles", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(id, asSam.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetString());
        Assert.Equal("owner", forged.EnumerateArray().Single(p => p.GetProperty("isCurrent").GetBoolean()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task RejectsBadPins()
    {
        var response = await Client().PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = "Kid", pin = "12" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task ProfilePicture_StartsEmpty_AndUploadsAreChecked()
    {
        var created = await (await Client().PostAsJsonAsync(new Uri("/api/v1/profiles", UriKind.Relative), new { name = "Pic" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = created.GetProperty("id").GetString();
        using var empty = new ByteArrayContent([]);
        using var picture = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47]); // a PNG signature and nothing else

        var emptyUpload = await Client().PutAsync(new Uri($"/api/v1/profiles/{id}/image", UriKind.Relative), empty);
        var notAPicture = await Client().PutAsync(new Uri($"/api/v1/profiles/{id}/image", UriKind.Relative), picture);
        var missing = await Client().PutAsync(new Uri("/api/v1/profiles/nobody/image", UriKind.Relative), picture);
        var served = await factory.CreateClient().GetAsync(new Uri($"/api/v1/profiles/{id}/image", UriKind.Relative));

        Assert.Equal(JsonValueKind.Null, created.GetProperty("image").ValueKind);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, emptyUpload.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, notAPicture.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, served.StatusCode);
    }
}
