using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Api.Tests;

public class PairingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    [Fact]
    public async Task ATv_IsHandedTheKey_OnceTheMainProfileApprovesItsCode()
    {
        var tv = factory.CreateClient(); // no API key: a TV connecting for the first time
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);

        var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Living room" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("id").GetString();
        var code = started.GetProperty("code").GetString()!;

        var waiting = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));
        var strangerApproves = await tv.PostAsync(Api($"/pairing/code/{code}/approve"), null);
        var details = await admin.GetFromJsonAsync<JsonElement>(Api($"/pairing/code/{code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}"));
        var approved = await admin.PostAsync(Api($"/pairing/code/{code}/approve"), null);
        var handedOver = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));
        var again = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));

        Assert.Matches("^[A-Z2-9]{3}-[A-Z2-9]{3}$", code);
        Assert.Equal("waiting", waiting.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, strangerApproves.StatusCode);
        Assert.Equal("Living room", details.GetProperty("deviceName").GetString()); // codes are read case- and dash-insensitively
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal(("approved", FlexarrApiFactory.ApiKey), (handedOver.GetProperty("status").GetString(), handedOver.GetProperty("apiKey").GetString()));
        Assert.Equal("expired", again.GetProperty("status").GetString()); // handed over once only
    }
}
