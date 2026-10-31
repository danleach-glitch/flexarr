using System.Text.Json;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Flexarr.Api.Security;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Tests;

/// <summary>Stands in for the server's Tailscale: tests say what the tailnet looks like.</summary>
public sealed class FakeTailnet : ITailnet
{
    public TailnetSnapshot? Snapshot { get; set; } = new(true, "100.64.0.1", "server.tail.ts.net",
    [
        new TailnetPeer("phone", "100.64.0.2", [], true, null, TailnetPath.Direct),
        new TailnetPeer("gran-ipad", "100.64.0.9", ["tag:flexarr-guest"], true, null, TailnetPath.Derp),
    ]);

    public List<int?> RelayPorts { get; } = [];

    public Task<TailnetSnapshot?> SnapshotAsync(CancellationToken ct) => Task.FromResult(Snapshot);

    public Task<string?> SetRelayPortAsync(int? port, CancellationToken ct)
    {
        RelayPorts.Add(port);
        Snapshot = Snapshot! with { RelayPort = port };
        return Task.FromResult<string?>(null);
    }
}

public class GuestPolicyTests
{
    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class StaticOptions(FlexarrOptions value) : IOptionsMonitor<FlexarrOptions>
    {
        public FlexarrOptions CurrentValue => value;
        public FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<FlexarrOptions, string?> listener) => null;
    }

    private sealed class Server : IServer
    {
        public Server()
        {
            var addresses = new ServerAddressesFeature();
            addresses.Addresses.Add("http://0.0.0.0:8383");
            Features.Set<IServerAddressesFeature>(addresses);
        }

        public IFeatureCollection Features { get; } = new FeatureCollection();
        public void Dispose() { }
        public Task StartAsync<TContext>(IHttpApplication<TContext> application, CancellationToken cancellationToken) where TContext : notnull => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static (GuestPolicy Policy, FakeTailscale Fake, FakeTailnet Tailnet, TestClock Clock) Make(bool client = true)
    {
        var fake = new FakeTailscale();
        var options = new StaticOptions(new FlexarrOptions
        {
            RemoteGuests = { ClientId = client ? "client" : "", ClientSecret = client ? "secret" : "", ApiUrl = "https://tailscale.test" },
        });
        var clock = new TestClock(new DateTimeOffset(2026, 10, 31, 0, 0, 0, TimeSpan.Zero));
        var api = new TailscaleApi(new Clients(fake), options, clock, NullLogger<TailscaleApi>.Instance);
        var tailnet = new FakeTailnet();
        return (new GuestPolicy(api, tailnet, options, new Server(), clock, NullLogger<GuestPolicy>.Instance), fake, tailnet, clock);
    }

    [Fact]
    public void The_tests_accept_flexarr_and_refuse_everything_else()
    {
        var policy = new JsonObject { ["tests"] = new JsonArray(new JsonObject { ["src"] = "someone@example.com", ["accept"] = new JsonArray("x:1") }) };

        GuestPolicy.WithTests(policy, "tag:flexarr-guest", "100.64.0.1", 8383, ["100.64.0.2"]);

        var tests = policy["tests"]!.AsArray();
        Assert.Equal(2, tests.Count); // the owner's own test stays
        var ours = tests[1]!;
        Assert.Equal("tag:flexarr-guest", ours["src"]!.GetValue<string>());
        Assert.Equal(["100.64.0.1:8383"], ours["accept"]!.AsArray().Select(a => a!.GetValue<string>()));
        var deny = ours["deny"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains("100.64.0.1:22", deny);
        Assert.Contains("100.64.0.1:8443", deny);
        Assert.Contains("100.64.0.2:22", deny);
        Assert.Contains("100.64.0.2:443", deny);
        Assert.DoesNotContain("100.64.0.1:8383", deny);
    }

    [Fact]
    public void The_snippet_names_this_server_and_is_valid_once_wrapped()
    {
        var snippet = GuestPolicy.Snippet("tag:flexarr-guest", "100.64.0.1", 8383, relay: true);

        var parsed = JsonNode.Parse("{" + snippet + "}", documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!;
        Assert.Equal("100.64.0.1", parsed["hosts"]!["flexarr"]!.GetValue<string>());
        Assert.Equal(2, parsed["grants"]!.AsArray().Count);
        Assert.Equal("tcp:8383", parsed["grants"]![0]!["ip"]![0]!.GetValue<string>());
        Assert.NotNull(parsed["grants"]![1]!["app"]!["tailscale.com/cap/relay"]);
        Assert.Single(JsonNode.Parse("{" + GuestPolicy.Snippet("tag:flexarr-guest", "100.64.0.1", 8383, relay: false) + "}",
            documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })!["grants"]!.AsArray());
    }

    [Fact]
    public async Task A_policy_whose_tests_pass_is_safe_and_the_other_devices_are_tested()
    {
        var (policy, fake, _, _) = Make();
        using var _ = policy;

        var check = await policy.CheckAsync(CancellationToken.None);

        Assert.True(check.Safe);
        var sent = JsonNode.Parse(fake.Calls.Single(c => c.Path.EndsWith("/validate", StringComparison.Ordinal)).Body)!;
        var deny = sent["tests"]![0]!["deny"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Contains("100.64.0.2:22", deny);
        Assert.DoesNotContain(deny, d => d.StartsWith("100.64.0.9:", StringComparison.Ordinal)); // other guests aren't tested
    }

    [Fact]
    public async Task The_default_allow_all_policy_is_refused()
    {
        var (policy, fake, _, _) = Make();
        using var _ = policy;
        fake.Validate = _ => """{"message":"test(s) failed","data":[{"user":"tag:flexarr-guest","errors":["address \"100.64.0.2:22\": want: Drop, got: Accept"]}]}""";

        var check = await policy.CheckAsync(CancellationToken.None);

        Assert.False(check.Safe);
        Assert.Contains("autogroup:member", check.Problems[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task Answers_are_kept_briefly_then_asked_again()
    {
        var (policy, fake, _, clock) = Make();
        using var _ = policy;

        await policy.CheckAsync(CancellationToken.None);
        await policy.CheckAsync(CancellationToken.None);
        Assert.Single(fake.Calls, c => c.Path.EndsWith("/validate", StringComparison.Ordinal));

        clock.Advance(GuestPolicy.CheckLifetime);
        await policy.CheckAsync(CancellationToken.None);
        Assert.Equal(2, fake.Calls.Count(c => c.Path.EndsWith("/validate", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("""{"src":["*"],"dst":["192.168.1.0/24"],"ip":["*"]}""", "grants")]
    [InlineData("""{"src":["*"],"dst":["autogroup:internet"],"ip":["*"]}""", "grants")]
    [InlineData("""{"src":["tag:flexarr-guest"],"dst":["flexarr"],"ip":["tcp:8989"]}""", "grants")]
    [InlineData("""{"src":["tag:flexarr-guest"],"dst":["flexarr"],"ip":["*"]}""", "grants")]
    public async Task A_rule_letting_guests_reach_more_than_flexarr_is_caught_without_tailscale_testing_it(string grant, string where)
    {
        var (policy, fake, _, _) = Make();
        using var _ = policy;
        fake.Policy = Policy($$"""{"hosts":{"flexarr":"100.64.0.1"},"grants":[{"src":["tag:flexarr-guest"],"dst":["flexarr"],"ip":["tcp:8383"]},{{grant}}]}""");

        var check = await policy.CheckAsync(CancellationToken.None);

        Assert.False(check.Safe);
        Assert.Contains(check.Problems, p => p.StartsWith(where, StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Calls, c => c.Path.EndsWith("/validate", StringComparison.Ordinal));
    }

    [Fact]
    public void The_snippet_its_relay_grant_and_rules_for_members_pass_the_rule_check()
    {
        var policy = Policy("""
            {"hosts":{"flexarr":"100.64.0.1"},
             "acls":[{"action":"accept","src":["autogroup:member"],"dst":["*:*"]},{"action":"accept","src":["tag:flexarr-guest"],"dst":["flexarr:8383"]}],
             "grants":[{"src":["tag:flexarr-guest"],"dst":["flexarr"],"ip":["tcp:8383"]},{"src":["tag:flexarr-guest"],"dst":["flexarr"],"app":{"tailscale.com/cap/relay":[]}}],
             "ssh":[{"action":"check","src":["autogroup:member"],"dst":["autogroup:self"],"users":["autogroup:nonroot"]}]}
            """);

        Assert.Empty(GuestPolicy.Widening(policy, "tag:flexarr-guest", "100.64.0.1", 8383));
        policy["acls"]!.AsArray().Add(JsonNode.Parse("""{"action":"accept","src":["*"],"dst":["*:*"]}"""));
        Assert.Single(GuestPolicy.Widening(policy, "tag:flexarr-guest", "100.64.0.1", 8383));
    }

    private static JsonObject Policy(string json) => (JsonObject)JsonNode.Parse(json)!;

    [Theory]
    [InlineData("timeout")]
    [InlineData("html")]
    public async Task A_slow_or_strange_answer_from_Tailscale_is_not_safe_and_never_throws(string broken)
    {
        var (policy, fake, _, _) = Make();
        using var _ = policy;
        fake.Broken = broken;

        var check = await policy.CheckAsync(CancellationToken.None);

        Assert.False(check.Safe);
        Assert.False(check.Validated);
    }

    [Fact]
    public async Task Without_a_client_or_Tailscale_it_is_not_safe()
    {
        var (noClient, _, _, _) = Make(client: false);
        using (noClient)
        {
            Assert.False((await noClient.CheckAsync(CancellationToken.None)).Safe);
        }

        var (policy, fake, tailnet, _) = Make();
        using var _ = policy;
        tailnet.Snapshot = null;
        var check = await policy.CheckAsync(CancellationToken.None);
        Assert.False(check.Safe);
        Assert.Contains("isn't running", check.Problems[0], StringComparison.Ordinal);
        Assert.Empty(fake.Calls);
    }

    [Fact]
    public void Tailscale_status_is_read_with_each_devices_path()
    {
        const string status = """
            {"BackendState":"Running","Self":{"DNSName":"mini.tail.ts.net.","TailscaleIPs":["100.64.0.1","fd7a::1"]},
             "Peer":{
              "a":{"HostName":"phone","TailscaleIPs":["100.64.0.2"],"CurAddr":"1.2.3.4:41641","Relay":"lhr","Online":true,"Active":true},
              "b":{"HostName":"gran","TailscaleIPs":["100.64.0.9"],"Tags":["tag:flexarr-guest"],"CurAddr":"","PeerRelay":"100.64.0.1:40000","Online":true,"Active":true},
              "c":{"HostName":"aunt","TailscaleIPs":["100.64.0.10"],"Tags":["tag:flexarr-guest"],"CurAddr":"","Relay":"nyc","Online":true,"Active":true,"LastSeen":"2026-10-30T10:00:00Z"},
              "d":{"HostName":"idle","TailscaleIPs":["100.64.0.11"],"CurAddr":"","Relay":"nyc","Online":false,"Active":false}}}
            """;
        using var doc = JsonDocument.Parse(status);

        var snapshot = TailscaleService.Snapshot(doc.RootElement);

        Assert.True(snapshot.Running);
        Assert.Equal("100.64.0.1", snapshot.Address);
        Assert.Equal("mini.tail.ts.net", snapshot.DnsName);
        var byName = snapshot.Peers.ToDictionary(p => p.HostName);
        Assert.Equal(TailnetPath.Direct, byName["phone"].Path);
        Assert.Equal(TailnetPath.PeerRelay, byName["gran"].Path);
        Assert.Equal(TailnetPath.Derp, byName["aunt"].Path);
        Assert.Equal(TailnetPath.Unknown, byName["idle"].Path);
        Assert.Equal(["tag:flexarr-guest"], byName["gran"].Tags);
        Assert.Equal(new DateTime(2026, 10, 30, 10, 0, 0, DateTimeKind.Utc), byName["aunt"].LastSeen);
        Assert.Equal(40000, TailscaleService.RelayPortOf("""{"RelayServerPort":40000}"""));
        Assert.Null(TailscaleService.RelayPortOf("""{"WantRunning":true}"""));
    }
}

public class GuestAlertTests
{
    [Fact]
    public void Joining_says_who_and_as_whom_and_how_to_undo_it()
    {
        var message = Flexarr.Api.Notifications.GuestAlerts.JoinedMessage("Gran's iPad", "Gran");
        Assert.Equal("Gran's iPad joined", message.Title);
        Assert.Contains("Watching as Gran", message.Body, StringComparison.Ordinal);
        Assert.Contains("Settings › Remote access", message.Body, StringComparison.Ordinal);
        Assert.Equal("access", message.Open!["settings"]);
    }
}

public class GuestLinkTests
{
    private sealed class StaticOptions(Flexarr.Api.Configuration.FlexarrOptions value) : Microsoft.Extensions.Options.IOptionsMonitor<Flexarr.Api.Configuration.FlexarrOptions>
    {
        public Flexarr.Api.Configuration.FlexarrOptions CurrentValue => value;
        public Flexarr.Api.Configuration.FlexarrOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<Flexarr.Api.Configuration.FlexarrOptions, string?> listener) => null;
    }

    private static Task<long?> CapAsync(string peer, bool guest)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.Connection.RemoteIpAddress = System.Net.IPAddress.Parse(peer);
        var claims = new List<System.Security.Claims.Claim> { new(FlexarrClaims.Profile, "gran") };
        if (guest)
        {
            claims.Add(new(FlexarrClaims.Guest, "true"));
        }

        http.User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(claims, "test"));
        var accessor = new Microsoft.AspNetCore.Http.HttpContextAccessor { HttpContext = http };
        var current = new Flexarr.Api.Profiles.CurrentProfile(accessor, new StaticOptions(new()));
        return new GuestLinks(accessor, current, new FakeTailnet()).CapAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Only_a_guest_on_tailscales_shared_relays_is_capped()
    {
        Assert.Equal(GuestLinks.SlowLinkBitrate, await CapAsync("100.64.0.9", guest: true)); // the fake tailnet has it on DERP
        Assert.Null(await CapAsync("100.64.0.2", guest: true)); // direct
        Assert.Null(await CapAsync("100.64.0.9", guest: false)); // not a guest
    }
}

public class MacAppRemoteUrlTests
{
    [Fact]
    public void A_mac_app_server_tells_the_apps_its_tailnet_address()
    {
        Assert.Equal("http://100.64.0.5:8383", Flexarr.Api.Endpoints.SystemEndpoints.RemoteUrl(Flexarr.Api.Updates.InstallKind.MacApp, null, () => "100.64.0.5", 8383));
        Assert.Null(Flexarr.Api.Endpoints.SystemEndpoints.RemoteUrl(Flexarr.Api.Updates.InstallKind.MacApp, null, () => null, 8383));
        Assert.Null(Flexarr.Api.Endpoints.SystemEndpoints.RemoteUrl(Flexarr.Api.Updates.InstallKind.Docker, null, () => "100.64.0.5", 8383));
        Assert.Equal("https://mini.ts.net:8443", Flexarr.Api.Endpoints.SystemEndpoints.RemoteUrl(Flexarr.Api.Updates.InstallKind.Other, "https://mini.ts.net:8443", () => "100.64.0.5", 8383));
    }
}

public class TvInviteSealingTests
{
    [Fact]
    public void The_shared_vector_opens_as_the_apps_open_it()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "clients", "apple", "FlexarrKit", "Tests", "FlexarrKitTests", "Fixtures", "tv-invite.json");
        using var vector = JsonDocument.Parse(File.ReadAllText(path));
        var root = vector.RootElement;
        var tvPublic = Convert.FromBase64String(root.GetProperty("tvPublicKey").GetString()!);
        using var tv = System.Security.Cryptography.ECDiffieHellman.Create(new System.Security.Cryptography.ECParameters
        {
            Curve = System.Security.Cryptography.ECCurve.NamedCurves.nistP256,
            D = Convert.FromBase64String(root.GetProperty("tvPrivateKey").GetString()!),
            Q = new System.Security.Cryptography.ECPoint { X = tvPublic[1..33], Y = tvPublic[33..] },
        });

        var opened = GuestTvCodes.Open(tv, Convert.FromBase64String(root.GetProperty("serverKey").GetString()!), Convert.FromBase64String(root.GetProperty("sealed").GetString()!));

        Assert.Equal(root.GetProperty("plaintext").GetString(), System.Text.Encoding.UTF8.GetString(opened));
    }

    [Fact]
    public void What_the_server_seals_opens_with_the_tvs_key_only()
    {
        using var tv = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        using var someoneElse = System.Security.Cryptography.ECDiffieHellman.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        var (serverKey, box) = GuestTvCodes.Seal(GuestTvCodes.Uncompressed(tv.ExportParameters(false)), "invite"u8.ToArray());

        Assert.Equal("invite"u8.ToArray(), GuestTvCodes.Open(tv, serverKey, box));
        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() => GuestTvCodes.Open(someoneElse, serverKey, box));
    }
}
