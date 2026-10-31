using System.Globalization;
using System.Text.Json.Nodes;
using Flexarr.Api.Configuration;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Security;

/// <summary>The safety check's last answer.</summary>
/// <param name="Safe">The policy confines guests to Flexarr's port: invites may be made and used.</param>
/// <param name="Problems">What's wrong, in words for the owner, when it isn't safe.</param>
/// <param name="Validated">Tailscale checked the policy (rather than the check failing before it could).</param>
public sealed record GuestPolicyCheck(bool Safe, IReadOnlyList<string> Problems, DateTime CheckedAt, bool Validated = false);

/// <summary>
/// Makes sure the tailnet's policy lets a guest device reach Flexarr and nothing else, before any invite is made or
/// used. Flexarr never edits the policy: it reads it, adds tests (a guest is accepted on Flexarr's port, and refused
/// this server's other ports and every other device), and asks Tailscale to validate the result. It also writes the
/// snippet the owner pastes in. See docs/remote-guests.md.
/// </summary>
public sealed partial class GuestPolicy(TailscaleApi api, ITailnet tailnet, IOptionsMonitor<FlexarrOptions> options, IServer server, TimeProvider clock,
    ILogger<GuestPolicy> logger) : IDisposable
{
    /// <summary>
    /// This server's other ports a guest must not reach: SSH, file sharing, screen sharing, Tailscale's HTTPS, and the
    /// usual homes of what runs beside Flexarr (Sonarr, Radarr, Lidarr, Prowlarr, Seerr, Bazarr, qBittorrent, SABnzbd,
    /// Plex, Home Assistant).
    /// </summary>
    private static readonly int[] OtherServerPorts = [22, 80, 443, 445, 5900, TailscaleService.Port, 8989, 7878, 8686, 9696, 5055, 6767, 8080, 8090, 32400, 8123];

    /// <summary>Sources that take in a guest device whatever the rest of the rule says.</summary>
    private static readonly string[] SourcesForEveryone = ["*", "autogroup:danger-all", "0.0.0.0/0", "::/0", "100.64.0.0/10", "fd7a:115c:a1e0::/48"];

    /// <summary>Ports checked on each other device.</summary>
    private static readonly int[] OtherDevicePorts = [22, 80, 443];

    /// <summary>Enough other devices to catch a rule that lets guests through, without a huge test.</summary>
    private const int MaxDevicesTested = 25;

    /// <summary>An answer is kept this long, so making several invites in a row checks once.</summary>
    public static readonly TimeSpan CheckLifetime = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public GuestPolicyCheck? Last { get; private set; }

    public void Dispose() => _lock.Dispose();

    /// <summary>Forgets the last answer (the client changed).</summary>
    public void Forget() => Last = null;

    /// <summary>The port Flexarr listens on, which guests reach directly on this server's tailnet address.</summary>
    public int FlexarrPort => ServerPort.Of(server) ?? 8383;

    /// <summary>The check's answer, from the last two minutes or asked now.</summary>
    public async Task<GuestPolicyCheck> CheckAsync(CancellationToken ct, bool fresh = false)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (!fresh && Last is { } last && clock.GetUtcNow().UtcDateTime - last.CheckedAt < CheckLifetime)
            {
                return last;
            }

            var check = await RunAsync(ct);
            if (Last is { Safe: true } && !check.Safe)
            {
                LogNoLongerSafe(string.Join("; ", check.Problems));
            }

            Last = check;
            return check;
        }
        finally
        {
            _lock.Release();
        }
    }

    private async Task<GuestPolicyCheck> RunAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        GuestPolicyCheck Unsafe(params string[] problems) => new(false, problems, now);

        var settings = options.CurrentValue.RemoteGuests;
        if (!settings.HasClient)
        {
            return Unsafe("Add the Tailscale OAuth client first.");
        }

        var snapshot = await tailnet.SnapshotAsync(ct);
        if (snapshot is not { Running: true, Address: { } self })
        {
            return Unsafe("Tailscale isn't running on the server.");
        }

        JsonObject policy;
        try
        {
            policy = await api.GetPolicyAsync(ct);
        }
        catch (TailscaleApiException ex)
        {
            return Unsafe(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Unsafe($"Couldn't reach Tailscale ({ex.Message}).");
        }

        // Tailscale's tests can only try a few addresses; reading the rules themselves catches the rest (a subnet
        // router, an exit node, another port on this server).
        if (Widening(policy, settings.Tag, self, FlexarrPort) is { Count: > 0 } widening)
        {
            return new GuestPolicyCheck(false, ["Guests could reach more than Flexarr. Narrow these rules so they leave guests out (use autogroup:member, not \"*\"):", .. widening.Take(5)], now,
                Validated: true);
        }

        var others = snapshot.Peers
            .Where(p => p.Address is not null && !p.Tags.Contains(settings.Tag, StringComparer.OrdinalIgnoreCase))
            .OrderByDescending(p => p.Online).ThenByDescending(p => p.LastSeen)
            .Take(MaxDevicesTested)
            .Select(p => p.Address!)
            .ToList();
        WithTests(policy, settings.Tag, self, FlexarrPort, others);

        try
        {
            var validation = await api.ValidateAsync(policy, ct);
            return validation.Passed
                ? new GuestPolicyCheck(true, [], now, Validated: true)
                : new GuestPolicyCheck(false, [Summary(validation.Problems, settings.Tag), .. validation.Problems.Take(5)], now, Validated: true);
        }
        catch (TailscaleApiException ex)
        {
            return Unsafe(ex.Message);
        }
        catch (HttpRequestException ex)
        {
            return Unsafe($"Couldn't reach Tailscale ({ex.Message}).");
        }
    }

    /// <summary>
    /// Adds the guest tests to <paramref name="policy"/>: accepted on Flexarr's port, refused on this server's other
    /// ports and on the usual ports of every other device. Tests already in the policy stay and run too.
    /// </summary>
    internal static void WithTests(JsonObject policy, string tag, string self, int flexarrPort, IReadOnlyList<string> otherDevices)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var deny = new JsonArray();
        foreach (var port in OtherServerPorts.Where(p => p != flexarrPort))
        {
            deny.Add($"{self}:{port.ToString(CultureInfo.InvariantCulture)}");
        }

        foreach (var device in otherDevices)
        {
            foreach (var port in OtherDevicePorts)
            {
                deny.Add($"{device}:{port.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        var tests = policy["tests"] as JsonArray ?? [];
        policy["tests"] = tests;
        tests.Add(new JsonObject
        {
            ["src"] = tag,
            ["accept"] = new JsonArray($"{self}:{flexarrPort.ToString(CultureInfo.InvariantCulture)}"),
            ["deny"] = deny,
        });
    }

    /// <summary>
    /// The rules in <paramref name="policy"/> that let a guest reach anything but Flexarr's port on this server,
    /// described for the owner. Only the snippet's own grants (Flexarr's port, and the faster relay) are allowed.
    /// </summary>
    internal static List<string> Widening(JsonObject policy, string tag, string self, int flexarrPort)
    {
        ArgumentNullException.ThrowIfNull(policy);
        var port = flexarrPort.ToString(CultureInfo.InvariantCulture);
        // Names this server goes by: its address, and any host alias for it ("flexarr" in the snippet).
        var server = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { self, $"{self}/32" };
        if (policy["hosts"] is JsonObject hosts)
        {
            foreach (var (name, value) in hosts)
            {
                if (value?.ToString() is { } address && server.Contains(address))
                {
                    server.Add(name);
                }
            }
        }

        bool TakesGuests(JsonNode? src) => Strings(src).Any(s => s.Equals(tag, StringComparison.OrdinalIgnoreCase)
            || SourcesForEveryone.Contains(s, StringComparer.OrdinalIgnoreCase));

        var found = new List<string>();
        foreach (var rule in (policy["acls"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (rule["action"]?.ToString() is { } action && !action.Equals("accept", StringComparison.OrdinalIgnoreCase) || !TakesGuests(rule["src"]))
            {
                continue;
            }

            // "flexarr:8383" is fine; anything else ("*:*", "192.168.1.0/24:*", "flexarr:*") isn't.
            var wider = Strings(rule["dst"]).Where(d => !(d.LastIndexOf(':') is var colon and > 0 && server.Contains(d[..colon]) && d[(colon + 1)..] == port)).ToList();
            if (wider.Count > 0)
            {
                found.Add($"acls: {Describe(rule["src"])} → {string.Join(", ", wider)}");
            }
        }

        foreach (var rule in (policy["grants"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (!TakesGuests(rule["src"]))
            {
                continue;
            }

            var dst = Strings(rule["dst"]).ToList();
            var toServerOnly = dst.Count > 0 && dst.All(server.Contains);
            var ip = Strings(rule["ip"]).ToList();
            // Flexarr's port alone, or (the faster relay) no network access at all, only the relay capability.
            var portOnly = ip.All(i => i == port || i.Equals($"tcp:{port}", StringComparison.OrdinalIgnoreCase));
            if (!toServerOnly || !portOnly)
            {
                found.Add($"grants: {Describe(rule["src"])} → {string.Join(", ", dst)}{(ip.Count > 0 ? $" ({string.Join(", ", ip)})" : "")}");
            }
        }

        foreach (var rule in (policy["ssh"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (TakesGuests(rule["src"]))
            {
                found.Add($"ssh: {Describe(rule["src"])} → {Describe(rule["dst"])}");
            }
        }

        return found;
    }

    private static IEnumerable<string> Strings(JsonNode? node) => node switch
    {
        JsonArray array => array.Select(n => n?.ToString()).OfType<string>(),
        JsonValue value when value.ToString() is { } one => [one],
        _ => [],
    };

    private static string Describe(JsonNode? node) => string.Join(", ", Strings(node));

    private static string Summary(IReadOnlyList<string> problems, string tag) =>
        problems.Any(p => p.Contains("want: Drop", StringComparison.OrdinalIgnoreCase) || p.Contains("got: Accept", StringComparison.OrdinalIgnoreCase))
            ? "Guests could reach more than Flexarr. A rule with \"*\" as its source lets tagged devices through: narrow it to autogroup:member."
        : problems.Any(p => p.Contains("want: Accept", StringComparison.OrdinalIgnoreCase))
            ? "Guests can't reach Flexarr yet. Paste the snippet into your tailnet policy."
        : problems.Any(p => p.Contains(tag, StringComparison.OrdinalIgnoreCase) && p.Contains("tag", StringComparison.OrdinalIgnoreCase))
            ? $"{tag} isn't in your tailnet policy yet. Paste the snippet into it."
        : "Tailscale didn't accept the policy with Flexarr's checks added.";

    /// <summary>The lines the owner pastes into the tailnet policy file, with this server's address filled in.</summary>
    public static string Snippet(string tag, string self, int flexarrPort, bool relay)
    {
        var port = flexarrPort.ToString(CultureInfo.InvariantCulture);
        var relayGrant = relay
            ? $",\n    // Faster relay: guests whose networks can't connect directly go through this server.\n    {{ \"src\": [\"{tag}\"], \"dst\": [\"flexarr\"], \"app\": {{ \"tailscale.com/cap/relay\": [] }} }}"
            : "";
        return $$"""
            // Flexarr family invites: guest devices reach Flexarr on this server and nothing else.
            // Merge these into your policy (Tailscale admin › Access controls). Keep any rule with "*" as its source
            // away from guests by narrowing it to "autogroup:member".
            "tagOwners": {
              "{{tag}}": ["autogroup:admin"]
            },
            "hosts": {
              "flexarr": "{{self}}"
            },
            "grants": [
              { "src": ["{{tag}}"], "dst": ["flexarr"], "ip": ["tcp:{{port}}"] }{{relayGrant}}
            ]
            """;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "The tailnet policy no longer confines family guests to Flexarr ({Problems}); invites are paused")]
    private partial void LogNoLongerSafe(string problems);
}
