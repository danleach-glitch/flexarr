using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Profiles;

namespace Flexarr.Api.Tests;

/// <summary>Wrong PINs: the guesser is locked out, but nobody can lock the owner out. A server of its own, so other tests' guesses don't count.</summary>
public class PinTests(FlexarrApiFactory factory) : IClassFixture<FlexarrApiFactory>
{
    private static Uri Api(string path) => new($"/api/v1{path}", UriKind.Relative);

    private HttpClient Admin()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        return client;
    }

    /// <summary>A TV watching as a profile of its own, so it can't manage the server.</summary>
    private async Task<HttpClient> KidsDeviceAsync(string name)
    {
        var kid = (await (await Admin().PostAsJsonAsync(Api("/profiles"), new { name = $"{name} kid" })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("id").GetString();
        var token = (await (await Admin().PostAsJsonAsync(Api("/session/device"), new { deviceName = name })).Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("token").GetString();
        var device = factory.CreateClient();
        device.DefaultRequestHeaders.Add("X-Api-Key", token);
        await device.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { pin = (string?)null });
        return device;
    }

    [Fact]
    public async Task WrongPins_LockOutTheGuesser_ButCantLockTheOwnerOut()
    {
        await Admin().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "8642" });
        var guesser = await KidsDeviceAsync("Guesser");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await guesser.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = $"000{i}" })).StatusCode);
        }

        var guesserWithRightPin = await guesser.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "8642" });
        // The owner's own browser, signed in as the main profile, isn't held up by someone else's guesses.
        var ownersBrowser = factory.CreateClient();
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session")))
        {
            signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            var cookie = Assert.Single((await ownersBrowser.SendAsync(signIn)).Headers.GetValues("Set-Cookie")).Split(';')[0];
            ownersBrowser.DefaultRequestHeaders.Add("Cookie", cookie);
            ownersBrowser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        var owner = await ownersBrowser.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "8642" });
        // Anyone else is slowed down for a few seconds at most, not locked out.
        var otherTv = await KidsDeviceAsync("Other TV");
        var throttled = await otherTv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "8642" });
        await Task.Delay(PinGuard.ProfileThrottle);
        var afterAWait = await otherTv.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "8642" });

        Assert.Equal([HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.Forbidden, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.TooManyRequests, guesserWithRightPin.StatusCode);
        Assert.True(guesserWithRightPin.Headers.Contains("Retry-After"));
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
        if (throttled.StatusCode == HttpStatusCode.TooManyRequests)
        {
            Assert.True(throttled.Headers.RetryAfter?.Delta <= PinGuard.ProfileThrottle);
        }

        Assert.Equal(HttpStatusCode.OK, afterAWait.StatusCode);
        await Admin().PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true });
    }

    [Fact]
    public async Task Strikes_AreHalved_AfterAQuietSpell()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync("kid", "guesser", () => false, CancellationToken.None);
        }

        var locked = await guard.CheckAsync("kid", "guesser", () => true, CancellationToken.None);
        clock.Now += TimeSpan.FromSeconds(31) + PinGuard.QuietPeriod;
        // A slip long after doesn't pick up where the old strikes left off.
        await guard.CheckAsync("kid", "guesser", () => false, CancellationToken.None);
        var later = await guard.CheckAsync("kid", "guesser", () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, locked.Result);
        Assert.Equal(PinCheck.Right, later.Result);
    }

    [Fact]
    public async Task APatientGuesser_IsLockedOutAgain_SoonAfterAQuietSpell()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        // Eight wrong PINs, each waiting out the lockout it earns (none for the first three, then 30 s doubling).
        for (var i = 1; i <= 8; i++)
        {
            await guard.CheckAsync("kid", "patient", () => false, CancellationToken.None);
            clock.Now += i < 4 ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(30 * Math.Pow(2, i - 4) + 1);
        }

        clock.Now += PinGuard.QuietPeriod;
        // Half the strikes are kept, so one more wrong PIN locks it out straight away.
        await guard.CheckAsync("kid", "patient", () => false, CancellationToken.None);
        var next = await guard.CheckAsync("kid", "patient", () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, next.Result);
    }

    [Fact]
    public async Task ABrowserThatKnowsThePin_IsntLockedOut_ByGuessesFromItsAddress()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        await guard.CheckAsync("owner", "address:10.0.0.2", () => true, CancellationToken.None, browser: "owners-laptop");
        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync("owner", "address:10.0.0.2", () => false, CancellationToken.None, browser: $"kids-tablet-{i}");
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);
        var owner = await guard.CheckAsync("owner", "address:10.0.0.2", () => true, CancellationToken.None, browser: "owners-laptop");
        // A new cookie doesn't buy the guesser fresh attempts.
        var freshCookie = await guard.CheckAsync("owner", "address:10.0.0.2", () => true, CancellationToken.None, browser: "kids-tablet-new");

        Assert.Equal(PinCheck.Right, owner.Result);
        Assert.Equal(PinCheck.LockedOut, freshCookie.Result);
    }

    [Fact]
    public async Task ManyDevicesSignedInAsTheMainProfile_ShareOneAllowance_ForGuessingItsPin()
    {
        // A script in the admin's page could pair TV after TV; each one's own allowance mustn't add up.
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        await admin.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "5319" });
        var statuses = new List<HttpStatusCode>();
        HttpResponseMessage? last = null;
        for (var i = 0; i < 20; i++)
        {
            var token = (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = $"TV {i}" })).Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("token").GetString();
            var device = server.CreateClient();
            device.DefaultRequestHeaders.Add("X-Api-Key", token);
            last = await device.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { pin = $"{1000 + i}" });
            statuses.Add(last.StatusCode);
        }

        Assert.Equal(8, statuses.Count(s => s == HttpStatusCode.Forbidden));
        Assert.Equal(12, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        // The web app can count down from the body as well as the header.
        var problem = await last!.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal((int)last.Headers.RetryAfter!.Delta!.Value.TotalSeconds, problem.GetProperty("retryAfterSeconds").GetInt32());
        // The API key itself still gets in.
        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { })).StatusCode);
    }

    [Fact]
    public async Task OverTailscale_ALockedOutLogin_IsHeldOffFromItsOtherAddresses_AndAnAddressFromOtherLogins()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync("owner", ["tailscale:teen@example.com", "address:100.70.1.40"], () => false, CancellationToken.None);
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);

        var otherAddress = await guard.CheckAsync("owner", ["tailscale:teen@example.com", "address:fd7a:115c:a1e0::42"], () => true, CancellationToken.None);
        var otherLogin = await guard.CheckAsync("owner", ["tailscale:teen-alt@example.com", "address:100.70.1.40"], () => true, CancellationToken.None);
        var someoneElse = await guard.CheckAsync("owner", ["tailscale:owner@example.com", "address:100.70.1.50"], () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, otherAddress.Result);
        Assert.Equal(PinCheck.LockedOut, otherLogin.Result);
        Assert.Equal(PinCheck.Right, someoneElse.Result);
    }

    [Fact]
    public async Task KnowingAnotherProfilesPin_DoesntCrowdTheOwnersBrowserOutOfTheKnownList()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        await guard.CheckAsync("owner", "address:10.0.0.3", () => true, CancellationToken.None, browser: "owners-laptop");
        // A kid throws away their cookie and picks their own profile, over and over.
        for (var i = 0; i < 10_001; i++)
        {
            await guard.CheckAsync("kid", "address:10.0.0.3", () => true, CancellationToken.None, browser: $"kid-{i}");
        }

        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync("owner", "address:10.0.0.3", () => false, CancellationToken.None, browser: $"kid-guess-{i}");
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);
        var owner = await guard.CheckAsync("owner", "address:10.0.0.3", () => true, CancellationToken.None, browser: "owners-laptop");

        Assert.Equal(PinCheck.Right, owner.Result);
    }

    [Fact]
    public async Task ARightPinForYourOwnProfile_DoesntWipeYourWrongOnesAtTheMainProfile()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        for (var round = 0; round < 2; round++)
        {
            for (var i = 0; i < 3; i++)
            {
                await guard.CheckAsync("owner", "address:10.0.0.4", () => false, CancellationToken.None);
            }

            await guard.CheckAsync("kid", "address:10.0.0.4", () => true, CancellationToken.None);
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);
        var next = await guard.CheckAsync("owner", "address:10.0.0.4", () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, next.Result);
    }

    [Fact]
    public async Task ChangingTheMainPin_TakesTheCurrentOne_OrTheApiKey()
    {
        var browser = factory.CreateClient();
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session")))
        {
            signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            browser.DefaultRequestHeaders.Add("Cookie", Assert.Single((await browser.SendAsync(signIn)).Headers.GetValues("Set-Cookie")).Split(';')[0]);
            browser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        var first = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "13579" });
        var removedWithout = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true });
        var changedWithout = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "0000" });
        var wrong = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "0000", currentPin = "1111" });
        var revealed = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { confirm = true });
        var renamed = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner" });
        var changed = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "24680", currentPin = "13579" });
        var wrongKey = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true, apiKey = "not-the-key" });
        var forgotten = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true, apiKey = FlexarrApiFactory.ApiKey });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // a first PIN needs nothing more
        Assert.Equal(HttpStatusCode.Forbidden, removedWithout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, changedWithout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revealed.StatusCode); // the PIN is still there, still guarding the key
        Assert.Equal(HttpStatusCode.OK, renamed.StatusCode); // other changes don't ask
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forgotten.StatusCode);
        Assert.False((await forgotten.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPin").GetBoolean());
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
