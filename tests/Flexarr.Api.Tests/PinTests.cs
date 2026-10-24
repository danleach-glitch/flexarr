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
    public async Task Strikes_AreForgotten_AfterAQuietSpell()
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

    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => Now;
    }
}
