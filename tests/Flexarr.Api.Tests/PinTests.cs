using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flexarr.Api.Profiles;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

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
        await factory.SetMainPinAsync(null);
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
    public async Task APatientGuesser_GetsOnlyADaysAllowance_OfGuessesAtAProfile()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        var start = clock.Now;
        // One wrong PIN at a time, each after a quiet spell has halved the strikes, so no lockout is ever waited for.
        for (var i = 0; i < PinGuard.CallerDailyAllowance; i++)
        {
            Assert.Equal(PinCheck.Wrong, (await guard.CheckAsync("kid", "patient", () => false, CancellationToken.None)).Result);
            clock.Now += PinGuard.QuietPeriod + TimeSpan.FromMinutes(1);
        }

        var spent = await guard.CheckAsync("kid", "patient", () => true, CancellationToken.None);
        var someoneElse = await guard.CheckAsync("kid", "parent", () => true, CancellationToken.None);
        var otherProfile = await guard.CheckAsync("teen", "patient", () => true, CancellationToken.None);
        clock.Now = start + TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1);
        var nextDay = await guard.CheckAsync("kid", "patient", () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, spent.Result);
        Assert.Equal(PinCheck.Right, someoneElse.Result);
        Assert.Equal(PinCheck.Right, otherProfile.Result);
        Assert.Equal(PinCheck.Right, nextDay.Result);
    }

    [Fact]
    public async Task APinForgivenAsTheRightOneForAnotherCheck_DoesntSpendTheDaysAllowance()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        // The owner choosing the main profile with the admin PIN, many times in a day.
        for (var i = 0; i <= PinGuard.CallerDailyAllowance; i++)
        {
            await guard.CheckAsync("main", "sign-in:laptop", () => false, CancellationToken.None);
            guard.Forgive("main", ["sign-in:laptop"], mayManage: false);
            clock.Now += PinGuard.ProfileThrottle;
        }

        Assert.Equal(PinCheck.Right, (await guard.CheckAsync("main", "sign-in:laptop", () => true, CancellationToken.None)).Result);
    }

    [Fact]
    public async Task ABrowserThatKnowsThePin_IsntLockedOut_ByGuessesFromItsAddress()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.2", () => true, CancellationToken.None, browser: "owners-laptop");
        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.2", () => false, CancellationToken.None, browser: $"kids-tablet-{i}");
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);
        var owner = await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.2", () => true, CancellationToken.None, browser: "owners-laptop");
        // A new cookie doesn't buy the guesser fresh attempts.
        var freshCookie = await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.2", () => true, CancellationToken.None, browser: "kids-tablet-new");

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
        await admin.PostAsJsonAsync(Api("/admin/pin"), new { pin = "5319" });
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
    public async Task LettingDevicesAtHomeInWithoutTheKey_AsksForTheAdminPin()
    {
        using var server = new FlexarrApiFactory();
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        await admin.PostAsJsonAsync(Api("/admin/pin"), new { pin = "5319" });
        var browser = server.CreateClient();
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session")))
        {
            signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            browser.DefaultRequestHeaders.Add("Cookie", Assert.Single((await browser.SendAsync(signIn)).Headers.GetValues("Set-Cookie")).Split(';')[0]);
            browser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        var without = await browser.PutAsJsonAsync(Api("/settings"), new { authenticationRequired = "DisabledForLocalAddresses" });
        var wrong = await browser.PutAsJsonAsync(Api("/settings"), new { authenticationRequired = "DisabledForLocalAddresses", pin = "0000" });
        var right = await browser.PutAsJsonAsync(Api("/settings"), new { authenticationRequired = "DisabledForLocalAddresses", pin = "5319" });
        // Asking for the key again everywhere, and saving anything else, takes nothing more.
        var tightened = await browser.PutAsJsonAsync(Api("/settings"), new { authenticationRequired = "Enabled" });

        Assert.Equal(HttpStatusCode.Forbidden, without.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.Equal("DisabledForLocalAddresses", (await right.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("authenticationRequired").GetString());
        Assert.Equal(HttpStatusCode.OK, tightened.StatusCode);
        // The API key itself is enough.
        Assert.Equal(HttpStatusCode.OK, (await admin.PutAsJsonAsync(Api("/settings"), new { authenticationRequired = "DisabledForLocalAddresses" })).StatusCode);
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

    [Theory]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "range:2001:db8:1:2::/64")]
    [InlineData("fe80::1234:5678", "range:fe80::/64")]
    [InlineData("192.168.1.20", null)]
    [InlineData("::ffff:192.168.1.20", null)]
    [InlineData("fd7a:115c:a1e0::42", null)]
    public void AnIPv6AddressWithoutASignIn_AlsoCountsUnderItsSlash64(string address, string? range) =>
        Assert.Equal(range, ProfileEndpoints.PinRange(IPAddress.Parse(address)));

    [Fact]
    public async Task AGuesserTakingAFreshIPv6AddressInItsRange_IsStillLockedOut_ButAnotherRangeIsnt()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        string[] Caller(string address) => [$"address:{address}", ProfileEndpoints.PinRange(IPAddress.Parse(address))!];
        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync("owner", Caller($"2001:db8:1:2::{i + 1:x}"), () => false, CancellationToken.None);
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);

        var fresh = await guard.CheckAsync("owner", Caller("2001:db8:1:2::99"), () => true, CancellationToken.None);
        var otherRange = await guard.CheckAsync("owner", Caller("2001:db8:1:3::1"), () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, fresh.Result);
        Assert.Equal(PinCheck.Right, otherRange.Result);
    }

    [Fact]
    public async Task KnowingAnotherProfilesPin_DoesntCrowdTheOwnersBrowserOutOfTheKnownList()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.3", () => true, CancellationToken.None, browser: "owners-laptop");
        // A kid throws away their cookie and picks their own profile, over and over.
        for (var i = 0; i < 10_001; i++)
        {
            await guard.CheckAsync("kid", "address:10.0.0.3", () => true, CancellationToken.None, browser: $"kid-{i}");
        }

        for (var i = 0; i < 4; i++)
        {
            await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.3", () => false, CancellationToken.None, browser: $"kid-guess-{i}");
        }

        clock.Now += PinGuard.ProfileThrottle + TimeSpan.FromSeconds(1);
        var owner = await guard.CheckAsync(PinGuard.AdminKey, "address:10.0.0.3", () => true, CancellationToken.None, browser: "owners-laptop");

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
    public async Task ChangingTheAdminPin_TakesTheCurrentOne_OrTheApiKey_ButTheMainProfilesOwnPinDoesnt()
    {
        var browser = factory.CreateClient();
        using (var signIn = new HttpRequestMessage(HttpMethod.Post, Api("/session")))
        {
            signIn.Headers.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
            browser.DefaultRequestHeaders.Add("Cookie", Assert.Single((await browser.SendAsync(signIn)).Headers.GetValues("Set-Cookie")).Split(';')[0]);
            browser.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
        }

        var first = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "13579" });
        var changedWithout = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "0000" });
        var wrong = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "0000", currentPin = "1111" });
        var revealed = await browser.PostAsJsonAsync(Api("/settings/api-key/reveal"), new { confirm = true });
        var changed = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "24680", currentPin = "13579" });
        var wrongKey = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "1111", apiKey = "not-the-key" });
        var forgotten = await browser.PostAsJsonAsync(Api("/admin/pin"), new { pin = "1111", apiKey = FlexarrApiFactory.ApiKey });
        // The main profile's own PIN is apart: changing or taking it off asks for nothing more.
        var ownPin = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", pin = "2222" });
        var ownPinOff = await browser.PutAsJsonAsync(Api("/profiles/owner"), new { name = "Owner", removePin = true });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode); // a first PIN needs nothing more
        Assert.Equal(HttpStatusCode.Forbidden, changedWithout.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, revealed.StatusCode); // the PIN is still there, still guarding the key
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forgotten.StatusCode);
        Assert.True((await ownPin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPin").GetBoolean());
        Assert.False((await ownPinOff.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hasPin").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await browser.PostAsJsonAsync(Api("/admin/unlock"), new { pin = "1111" })).StatusCode); // the admin PIN stayed
        await factory.SetMainPinAsync(null);
    }

    [Fact]
    public async Task AGuesserChangingAddress_RunsOutOfAdminPinGuessesForTheDay_ButTheOwnerStillGetsIn()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        // A fresh address for every guess, each past the profile's throttle.
        for (var i = 0; i < PinGuard.AdminDailyAllowance; i++)
        {
            var (wrong, _) = await guard.CheckAsync(PinGuard.AdminKey, $"address:10.1.{i / 250}.{i % 250}", () => false, CancellationToken.None);
            Assert.Equal(PinCheck.Wrong, wrong);
            clock.Now += PinGuard.ProfileThrottle;
        }

        var (spent, retryAfter) = await guard.CheckAsync(PinGuard.AdminKey, "address:10.2.0.1", () => true, CancellationToken.None);
        var owner = await guard.CheckAsync(PinGuard.AdminKey, "sign-in:owners-laptop", () => true, CancellationToken.None, mayManage: true);
        // Other profiles' PINs aren't held up.
        var kid = await guard.CheckAsync("kid", "address:10.2.0.1", () => true, CancellationToken.None);
        clock.Now += retryAfter;
        var tomorrow = await guard.CheckAsync(PinGuard.AdminKey, "address:10.2.0.1", () => true, CancellationToken.None);

        Assert.Equal(PinCheck.LockedOut, spent);
        Assert.True(retryAfter > TimeSpan.FromHours(23));
        Assert.Equal(PinCheck.Right, owner.Result);
        Assert.Equal(PinCheck.Right, kid.Result);
        Assert.Equal(PinCheck.Right, tomorrow.Result);
    }

    [Fact]
    public async Task OnceTheDaysAdminPinGuessesAreSpent_TheOwnersSignedInDevices_AndABrowserThatGaveItBefore_StillGetIn()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        // The owner's browser at home (no sign-in) has given the admin PIN before.
        await guard.CheckAsync(PinGuard.AdminKey, "address:192.168.1.20", () => true, CancellationToken.None, browser: "owners-laptop");
        for (var i = 0; i < PinGuard.AdminDailyAllowance; i++)
        {
            await guard.CheckAsync(PinGuard.AdminKey, $"address:10.1.{i / 250}.{i % 250}", () => false, CancellationToken.None);
            clock.Now += PinGuard.ProfileThrottle;
        }

        // Neither may manage the server yet: the PIN is what would let them.
        var phone = await guard.CheckAsync(PinGuard.AdminKey, "sign-in:owners-phone", () => true, CancellationToken.None);
        var laptop = await guard.CheckAsync(PinGuard.AdminKey, "address:192.168.1.20", () => true, CancellationToken.None, browser: "owners-laptop");
        var stranger = await guard.CheckAsync(PinGuard.AdminKey, "address:192.168.1.20", () => true, CancellationToken.None, browser: "someone-else");

        Assert.Equal(PinCheck.Right, phone.Result);
        Assert.Equal(PinCheck.Right, laptop.Result);
        Assert.Equal(PinCheck.LockedOut, stranger.Result);
    }

    [Fact]
    public async Task ASignedInDeviceGuessingTheAdminPin_IsLockedOutItself_WithoutSpendingTheDaysGuessesForOthers()
    {
        var clock = new ManualClock();
        var guard = new PinGuard(clock);
        // Household tablets pace themselves: a few guesses, then a wait long enough for half their strikes to be
        // forgotten, each until its own day's allowance is spent; together, as many as the day's for callers by address.
        var started = clock.Now;
        var guesses = 0;
        var tablets = PinGuard.AdminDailyAllowance / PinGuard.CallerDailyAllowance;
        for (var n = 0; n < tablets; n++)
        {
            var mine = 0;
            while (mine < PinGuard.CallerDailyAllowance)
            {
                var (result, retryAfter) = await guard.CheckAsync(PinGuard.AdminKey, $"sign-in:tablet{n}", () => false, CancellationToken.None);
                if (result == PinCheck.Wrong)
                {
                    mine++;
                    guesses++;
                }

                clock.Now += retryAfter + (retryAfter >= TimeSpan.FromMinutes(8) ? PinGuard.QuietPeriod : TimeSpan.Zero) + PinGuard.ProfileThrottle;
            }
        }

        var tablet = await guard.CheckAsync(PinGuard.AdminKey, $"sign-in:tablet{tablets - 1}", () => true, CancellationToken.None);
        clock.Now += PinGuard.ProfileThrottle;
        var owner = await guard.CheckAsync(PinGuard.AdminKey, "address:192.168.1.20", () => true, CancellationToken.None);

        Assert.Equal(PinGuard.AdminDailyAllowance, guesses);
        Assert.True(clock.Now - started < TimeSpan.FromHours(23)); // all within the day
        Assert.Equal(PinCheck.LockedOut, tablet.Result); // its own day's allowance is spent
        Assert.Equal(PinCheck.Right, owner.Result);
    }

    [Fact]
    public async Task BrowsersMovedOverFromEarlierVersionsCookie_SpendTheDaysAdminPinGuesses_HoweverManySignInsTheyAskFor()
    {
        // Whoever holds the cookie earlier versions gave every browser is given a sign-in for the asking (one for each
        // browser name here), so such a sign-in is no count it can't shed: a guess from each must still run out.
        var clock = new ManualClock();
        using var factory = new FlexarrApiFactory();
        using var server = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton(new PinGuard(clock))));
        var admin = server.CreateClient();
        admin.DefaultRequestHeaders.Add("X-Api-Key", FlexarrApiFactory.ApiKey);
        await admin.PostAsJsonAsync(Api("/admin/pin"), new { pin = "5319" });
        // The owner's phone, signed in with the key, has been watching as someone else, so it can't manage the server.
        var kid = (await (await admin.PostAsJsonAsync(Api("/profiles"), new { name = "Kid" })).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var phone = server.CreateClient();
        phone.DefaultRequestHeaders.Add("X-Api-Key", (await (await admin.PostAsJsonAsync(Api("/session/device"), new { deviceName = "Owner's phone" }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("token").GetString());
        await phone.PostAsJsonAsync(Api($"/profiles/{kid}/select"), new { });
        HttpClient Moved(int browser)
        {
            var client = server.CreateClient();
            client.DefaultRequestHeaders.Add("Cookie",
                $"{Authentication.ApiKeyAuthenticationHandler.SessionCookieName}={Authentication.ApiKeyAuthenticationHandler.LegacySessionValue(FlexarrApiFactory.ApiKey)}");
            client.DefaultRequestHeaders.Add("User-Agent", $"Browser/{browser}");
            client.DefaultRequestHeaders.Add("X-Requested-With", "flexarr");
            return client;
        }

        for (var i = 0; i < PinGuard.AdminDailyAllowance; i++)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await Moved(i).PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "0000" })).StatusCode);
            clock.Now += PinGuard.ProfileThrottle;
        }

        var another = await Moved(PinGuard.AdminDailyAllowance).PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "5319" });
        var owner = await phone.PostAsJsonAsync(Api("/profiles/owner/select"), new { pin = "5319" });

        Assert.Equal(HttpStatusCode.TooManyRequests, another.StatusCode);
        Assert.Equal(HttpStatusCode.OK, owner.StatusCode);
    }

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;

        /// <summary>The guard's pause after a wrong PIN passes at once rather than taking a real second.</summary>
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => callback(state), null);
            return new Fired();
        }

        private sealed class Fired : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
