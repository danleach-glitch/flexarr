using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
    public async Task DecliningACode_EndsIt_SoTheTvAsksForANewOne()
    {
        var tv = Tv("192.168.1.71");
        var admin = Admin();
        var started = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Not mine" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = started.GetProperty("code").GetString()!;

        var strangerDeclines = await tv.PostAsJsonAsync(Api($"/pairing/code/{code}/decline"), new { });
        var declined = await admin.PostAsJsonAsync(Api($"/pairing/code/{code}/decline"), new { });

        Assert.Equal(HttpStatusCode.Unauthorized, strangerDeclines.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, declined.StatusCode);
        Assert.Equal("expired", (await tv.GetFromJsonAsync<JsonElement>(Api($"/pairing/{started.GetProperty("id").GetString()}"))).GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync(Api($"/pairing/code/{code}/approve"), new { })).StatusCode);
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
    public async Task WhatsWaiting_IsListedForTheMainProfile_UntilItsApproved()
    {
        var started = await (await Tv("192.168.1.77").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Bedroom" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = started.GetProperty("code").GetString()!;

        var waiting = await Admin().GetFromJsonAsync<JsonElement>(Api("/pairing/waiting"));
        var stranger = await Tv("192.168.1.77").GetAsync(Api("/pairing/waiting"));
        await Admin().PostAsJsonAsync(Api($"/pairing/code/{code}/approve"), new { });
        var after = await Admin().GetFromJsonAsync<JsonElement>(Api("/pairing/waiting"));

        var listed = waiting.EnumerateArray().Single(p => p.GetProperty("code").GetString() == code);
        Assert.Equal(("Bedroom", "192.168.1.77", "Local"),
            (listed.GetProperty("deviceName").GetString(), listed.GetProperty("requestedFrom").GetString(), listed.GetProperty("zone").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, stranger.StatusCode);
        Assert.DoesNotContain(after.EnumerateArray(), p => p.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task TheMainProfilesPhone_HearsDevicesAskingToConnect_OnItsCommandStream()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        var tv = server.CreateClient();
        tv.DefaultRequestHeaders.Add("X-Test-Peer", "192.168.1.78");
        var before = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Kitchen" })).Content.ReadFromJsonAsync<JsonElement>();
        var session = await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "iPhone" })).Content.ReadFromJsonAsync<JsonElement>();
        var phone = server.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Api-Key", session.GetProperty("token").GetString()!);

        using var stream = await phone.GetAsync(Api("/devices/commands"), HttpCompletionOption.ResponseHeadersRead);
        using var reader = new StreamReader(await stream.Content.ReadAsStreamAsync());
        Assert.Equal(": connected", await reader.ReadLineAsync());

        // One already waiting is told straight away; a new one as it asks; and each as it's done with.
        var (name, first) = await NextEventAsync(reader);
        Assert.Equal(("pairing", "Kitchen"), (name, first.GetProperty("deviceName").GetString()));
        var after = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Den" })).Content.ReadFromJsonAsync<JsonElement>();
        var (_, second) = await NextEventAsync(reader);
        Assert.Equal(after.GetProperty("code").GetString(), second.GetProperty("code").GetString());
        await admin.PostAsJsonAsync(Api($"/pairing/code/{before.GetProperty("code").GetString()}/decline"), new { });
        var (ended, gone) = await NextEventAsync(reader);
        Assert.Equal(("pairing-ended", before.GetProperty("code").GetString()), (ended, gone.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task DontAllow_KeepsTheDevicesNextCodeFromBeingAskedAbout()
    {
        var tv = Tv("192.168.1.79");
        var first = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Spare room" })).Content.ReadFromJsonAsync<JsonElement>();
        await Admin().PostAsJsonAsync(Api($"/pairing/code/{first.GetProperty("code").GetString()}/decline"), new { });

        // The TV asks again at once, as it does: that code isn't asked about, though it can still be typed in.
        var again = await (await tv.PostAsJsonAsync(Api("/pairing"), new { deviceName = "Spare room" })).Content.ReadFromJsonAsync<JsonElement>();
        var code = again.GetProperty("code").GetString()!;
        var waiting = await Admin().GetFromJsonAsync<JsonElement>(Api("/pairing/waiting"));

        Assert.DoesNotContain(waiting.EnumerateArray(), p => p.GetProperty("code").GetString() == code);
        Assert.Equal(HttpStatusCode.OK, (await Admin().GetAsync(Api($"/pairing/code/{code}"))).StatusCode);
    }

    [Fact]
    public async Task ACodeStillOnScreen_KeepsGoing_AndOneNobodysShowingRunsOut()
    {
        var clock = new Clock();
        using var server = new FlexarrApiFactory();
        using var app = server.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock)));
        HttpClient Client(string address)
        {
            var client = app.CreateClient();
            client.DefaultRequestHeaders.Add("X-Test-Peer", address);
            return client;
        }

        var shown = await (await Client("192.168.1.90").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Shown", })).Content.ReadFromJsonAsync<JsonElement>();
        var left = await (await Client("192.168.1.91").PostAsJsonAsync(Api("/pairing"), new { deviceName = "Left", })).Content.ReadFromJsonAsync<JsonElement>();
        for (var minute = 1; minute <= 25; minute++)
        {
            clock.Now += TimeSpan.FromMinutes(1);
            await Client("192.168.1.90").GetAsync(Api($"/pairing/{shown.GetProperty("id").GetString()}"));
        }

        var still = await Client("192.168.1.90").GetFromJsonAsync<JsonElement>(Api($"/pairing/{shown.GetProperty("id").GetString()}"));
        var gone = await Client("192.168.1.91").GetFromJsonAsync<JsonElement>(Api($"/pairing/{left.GetProperty("id").GetString()}"));
        Assert.Equal("waiting", still.GetProperty("status").GetString());
        Assert.Equal("expired", gone.GetProperty("status").GetString());
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static async Task<(string Name, JsonElement Data)> NextEventAsync(StreamReader reader)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var line = await reader.ReadLineAsync(timeout.Token) ?? throw new InvalidOperationException("The stream ended.");
            if (line.StartsWith("event: ", StringComparison.Ordinal))
            {
                var data = await reader.ReadLineAsync(timeout.Token);
                return (line["event: ".Length..], JsonDocument.Parse(data!["data: ".Length..]).RootElement);
            }
        }
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
