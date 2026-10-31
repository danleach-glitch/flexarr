using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

public class TailscaleApiTests
{
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (TailscaleApi Api, FakeTailscale Fake, TestClock Clock) Make(Action<RemoteGuestOptions>? configure = null)
    {
        var fake = new FakeTailscale();
        var options = new FlexarrOptions { RemoteGuests = { ClientId = "client", ClientSecret = "secret", ApiUrl = "https://tailscale.test" } };
        configure?.Invoke(options.RemoteGuests);
        var clock = new TestClock(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        var monitor = new StaticOptions(options);
        return (new TailscaleApi(new Clients(fake), monitor, clock, NullLogger<TailscaleApi>.Instance), fake, clock);
    }

    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    [Fact]
    public async Task Guest_keys_are_one_use_preapproved_and_carry_only_the_guest_tag()
    {
        var (api, fake, clock) = Make();
        using var _ = api;

        var key = await api.CreateGuestKeyAsync(clock.GetUtcNow().UtcDateTime.AddDays(3), CancellationToken.None);

        Assert.Equal("k1", key.Id);
        Assert.StartsWith("tskey-auth-", key.Key, StringComparison.Ordinal);
        var call = fake.Calls.Single(c => c.Path == "/api/v2/tailnet/-/keys");
        var body = JsonNode.Parse(call.Body)!;
        var create = body["capabilities"]!["devices"]!["create"]!;
        Assert.False(create["reusable"]!.GetValue<bool>());
        Assert.False(create["ephemeral"]!.GetValue<bool>());
        Assert.True(create["preauthorized"]!.GetValue<bool>());
        Assert.Equal(["tag:flexarr-guest"], create["tags"]!.AsArray().Select(t => t!.GetValue<string>()));
        Assert.Equal(3 * 24 * 3600, body["expirySeconds"]!.GetValue<long>());
        Assert.Equal("Bearer tskey-api-test", call.Authorization);
    }

    [Fact]
    public async Task The_access_token_is_reused_until_it_runs_out()
    {
        var (api, fake, clock) = Make();
        using var _ = api;

        await api.CreateGuestKeyAsync(clock.GetUtcNow().UtcDateTime.AddDays(3), CancellationToken.None);
        await api.CreateGuestKeyAsync(clock.GetUtcNow().UtcDateTime.AddDays(3), CancellationToken.None);
        Assert.Single(fake.Calls, c => c.Path == "/api/v2/oauth/token");

        clock.Advance(TimeSpan.FromHours(1));
        await api.GetPolicyAsync(CancellationToken.None);
        Assert.Equal(2, fake.Calls.Count(c => c.Path == "/api/v2/oauth/token"));
    }

    [Fact]
    public async Task A_refused_client_says_so()
    {
        var (api, fake, _) = Make();
        using var __ = api;
        fake.AcceptClient = false;

        Assert.Contains("didn't accept the OAuth client", await api.CheckClientAsync(CancellationToken.None), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_a_client_nothing_is_sent()
    {
        var (api, fake, clock) = Make(o => o.ClientSecret = "");
        using var _ = api;

        await Assert.ThrowsAsync<TailscaleApiException>(() => api.CreateGuestKeyAsync(clock.GetUtcNow().UtcDateTime.AddDays(1), CancellationToken.None));
        Assert.Empty(fake.Calls);
    }

    [Theory]
    [InlineData("{}", true, 0)]
    [InlineData("", true, 0)]
    [InlineData("""{"message":"test(s) failed","data":[{"user":"tag:flexarr-guest","errors":["address \"100.64.0.2:22\": want: Drop, got: Accept"]}]}""", true, 1)]
    [InlineData("""{"message":"tag:flexarr-guest is not a valid tag"}""", false, 1)]
    public void Validation_answers_are_read(string body, bool success, int problems)
    {
        Assert.Equal(problems, TailscaleApi.Problems(body, success).Count);
    }
}
