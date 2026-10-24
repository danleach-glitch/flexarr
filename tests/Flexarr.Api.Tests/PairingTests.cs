using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Flexarr.Api.Tests;

public class PairingTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    /// <summary>A client on the home network (no key: a TV connecting for the first time).</summary>
    private HttpClient Tv(string address = "192.168.1.70")
    {
        var tv = factory.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Test-Peer", address);
        return tv;
    }

    private HttpClient Admin()
    {
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return admin;
    }

    [Fact]
    public async Task ATv_IsHandedATokenOfItsOwn_OnceTheMainProfileApprovesItsCode()
    {
        var tv = Tv();
        var admin = Admin();

        var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Living room" })).Content.ReadFromJsonAsync<JsonElement>();
        var id = started.GetProperty("id").GetString();
        var code = started.GetProperty("code").GetString()!;

        var waiting = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));
        var strangerApproves = await tv.PostAsJsonAsync(Api($"/pairing/code/{code}/approve"), new { });
        var details = await admin.GetFromJsonAsync<JsonElement>(Api($"/pairing/code/{code.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant()}"));
        var approved = await admin.PostAsJsonAsync(Api($"/pairing/code/{code}/approve"), new { });
        var handedOver = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));
        var again = await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{id}"));

        Assert.Matches("^[A-Z2-9]{3}-[A-Z2-9]{3}$", code);
        Assert.Equal("waiting", waiting.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, strangerApproves.StatusCode);
        Assert.Equal("Living room", details.GetProperty("deviceName").GetString()); // codes are read case- and dash-insensitively
        Assert.Equal(("192.168.1.70", "Local"), (details.GetProperty("requestedFrom").GetString(), details.GetProperty("zone").GetString()));
        Assert.Equal(HttpStatusCode.NoContent, approved.StatusCode);
        Assert.Equal("approved", handedOver.GetProperty("status").GetString());
        Assert.Equal("expired", again.GetProperty("status").GetString()); // handed over once only

        // The TV gets its own token, not the server's API key, and it works like one.
        var token = handedOver.GetProperty("apiKey").GetString()!;
        Assert.NotEqual(FlexarrApiFactory.ApiKey, token);
        var paired = factory.CreateClient();
        paired.DefaultRequestHeaders.Add("X-Api-Key", token);
        Assert.Equal(HttpStatusCode.OK, (await paired.GetAsync(Api("/system/status"))).StatusCode);
        var listed = await admin.GetFromJsonAsync<JsonElement>(Api("/sign-ins"));
        Assert.Contains(listed.EnumerateArray(), s => s.GetProperty("name").GetString() == "Living room" && s.GetProperty("kind").GetString() == "Device");
    }

    [Fact]
    public async Task Pairing_FromTheInternet_IsRefused()
    {
        var stranger = factory.CreateClient();
        stranger.DefaultRequestHeaders.Add("X-Test-Peer", "203.0.113.50");

        var response = await stranger.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Living room Apple TV" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Pairing_AddressedToSomeoneElsesName_IsRefused()
    {
        // A page on attacker.example that pointed its own name at this server, open in a browser on the home network.
        var started = await (await Tv("192.168.1.74").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Real TV" })).Content.ReadFromJsonAsync<JsonElement>();
        using var start = new HttpRequestMessage(HttpMethod.Post, Api("/pairing")) { Content = JsonContent.Create(new { deviceName = "Living room Apple TV" }) };
        start.Headers.Host = "attacker.example:8383";
        using var poll = new HttpRequestMessage(HttpMethod.Get, Api($"/pairing/{started.GetProperty("id").GetString()}"));
        poll.Headers.Host = "attacker.example:8383";

        Assert.Equal(HttpStatusCode.Forbidden, (await Tv("192.168.1.75").SendAsync(start)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Tv("192.168.1.75").SendAsync(poll)).StatusCode);
    }

    [Fact]
    public async Task OneAddress_CantTakeEveryPairingSlot()
    {
        var tv = Tv("192.168.1.71");
        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            results.Add((await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = $"TV {i}" })).StatusCode);
        }

        var someoneElse = await Tv("192.168.1.72").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Bedroom" });

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], results);
        Assert.Equal(HttpStatusCode.OK, someoneElse.StatusCode);
    }

    [Fact]
    public async Task ADeviceName_ThatCouldForgeALogLine_IsRefused()
    {
        var forged = await Tv("192.168.1.76").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Den\n[INF] Signed in a new Device" });
        var started = await (await Tv("192.168.1.76").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Den" })).Content.ReadFromJsonAsync<JsonElement>();
        var renamed = await Admin().PostAsJsonAsync(Api($"/pairing/code/{started.GetProperty("code").GetString()}/approve"), new { deviceName = "Den‮vt" });

        Assert.Equal(HttpStatusCode.BadRequest, forged.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
    }

    [Fact]
    public async Task AtMostTenTvs_AreApprovedInAnHour()
    {
        // Each approval is a new sign-in, so a script in the approver's page can't make them by the thousand.
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var results = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 11; i++)
        {
            var tv = server.CreateClient();
            tv.DefaultRequestHeaders.Add("X-Test-Peer", $"192.168.1.{100 + i}");
            var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = $"TV {i}" })).Content.ReadFromJsonAsync<JsonElement>();
            last = await admin.PostAsJsonAsync(Api($"/pairing/code/{started.GetProperty("code").GetString()}/approve"), new { });
            results.Add(last.StatusCode);
            await tv.GetAsync(Api($"/pairing/{started.GetProperty("id").GetString()}")); // collects its token, freeing the slot
        }

        Assert.Equal([.. Enumerable.Repeat(HttpStatusCode.NoContent, 10), HttpStatusCode.TooManyRequests], results);
        Assert.True(last!.Headers.RetryAfter?.Delta > TimeSpan.FromMinutes(50));
        Assert.True((await last.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("retryAfterSeconds").GetInt32() > 3000);
    }

    [Fact]
    public async Task Approval_NeedsAJsonBody()
    {
        var started = await (await Tv("192.168.1.73").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Den" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = started.GetProperty("code").GetString();

        // What a form on another website can send: no JSON.
        using var form = new FormUrlEncodedContent([]);
        var response = await Admin().PostAsync(Api($"/pairing/code/{code}/approve"), form);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
