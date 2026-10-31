using System.Diagnostics;
using System.Net;
using System.Text.Json;

namespace Flexarr.Api.Security;

public sealed record TailnetPerson(string Login, string Name);

public sealed record TailscaleState(
    bool Installed,
    bool Running,
    string? DnsName,
    string? OwnerLogin,
    bool PrivateAccess,
    bool PublicAccess,
    IReadOnlyList<TailnetPerson> People,
    string? Problem)
{
    /// <summary>Flexarr's address on the tailnet, with a real HTTPS certificate from Tailscale.</summary>
    public string? Url => DnsName is null ? null : $"https://{DnsName}:{TailscaleService.Port}";
}

/// <summary>Who a tailnet address belongs to, according to Tailscale itself.</summary>
public interface ITailnetWhois
{
    /// <summary>The Tailscale login of the person whose device has <paramref name="address"/>, or null if unknown.</summary>
    Task<string?> LoginAsync(IPAddress address, CancellationToken ct);

    /// <summary>
    /// The tags of the device with <paramref name="address"/> (a tagged device's login is "tagged-devices"); null
    /// when Tailscale couldn't say just now (it timed out or failed), which is never the same as "no tags".
    /// </summary>
    Task<IReadOnlyList<string>?> TagsAsync(IPAddress address, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>?>([]);
}

/// <summary>How a tailnet device's traffic reaches this server.</summary>
public enum TailnetPath
{
    Unknown,

    /// <summary>Straight between the two devices.</summary>
    Direct,

    /// <summary>Through a peer relay (this server's own, when the faster relay is on).</summary>
    PeerRelay,

    /// <summary>Through Tailscale's shared relays (DERP), which slow down to about 2 Mbps.</summary>
    Derp,
}

/// <summary>Another device on the tailnet, as this server's Tailscale sees it.</summary>
public sealed record TailnetPeer(string HostName, string? Address, IReadOnlyList<string> Tags, bool Online, DateTime? LastSeen, TailnetPath Path);

/// <summary>This server's place on the tailnet.</summary>
/// <param name="Address">This server's tailnet IPv4 address.</param>
/// <param name="RelayPort">The UDP port this server's peer relay listens on, or null when it isn't one.</param>
public sealed record TailnetSnapshot(bool Running, string? Address, string? DnsName, IReadOnlyList<TailnetPeer> Peers, int? RelayPort = null);

/// <summary>The tailnet as this server's Tailscale sees it, and the one setting family invites change.</summary>
public interface ITailnet
{
    /// <summary>The tailnet now (kept for a few seconds); null when Tailscale isn't installed or answering.</summary>
    Task<TailnetSnapshot?> SnapshotAsync(CancellationToken ct);

    /// <summary>Makes this server a peer relay on <paramref name="port"/>, or stops it (null). Returns the problem, if any.</summary>
    Task<string?> SetRelayPortAsync(int? port, CancellationToken ct);
}

/// <summary>
/// Drives the local <c>tailscale</c> command: reads the tailnet's state and publishes Flexarr on it with
/// <c>tailscale serve</c>, which gives it an HTTPS address, a certificate, and each visitor's verified identity.
/// Flexarr uses its own port (8443) so anything already served on 443 is left alone.
/// </summary>
public sealed partial class TailscaleService(ILogger<TailscaleService> logger, TimeProvider clock) : ITailnetWhois, ITailnet
{
    /// <summary>How long <see cref="SnapshotAsync"/>'s answer is kept.</summary>
    public static readonly TimeSpan SnapshotLifetime = TimeSpan.FromSeconds(10);

    private readonly Lock _snapshotLock = new();
    private (DateTimeOffset At, Task<TailnetSnapshot?> Lookup)? _snapshot;

    public const int Port = 8443;

    private readonly WhoisCache _whois = new(WhoisAsync, clock);

    /// <summary>How long the answer to <see cref="RemoteUrlAsync"/> is kept: every app asks at launch.</summary>
    public static readonly TimeSpan RemoteUrlLifetime = TimeSpan.FromMinutes(1);

    private readonly Lock _remoteLock = new();
    private (DateTimeOffset At, Task<string?> Lookup)? _remote;

    private static readonly string[] Candidates =
    [
        "/usr/local/bin/tailscale",
        "/opt/homebrew/bin/tailscale",
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
        "/usr/bin/tailscale",
        "/usr/sbin/tailscale",
        @"C:\Program Files\Tailscale\tailscale.exe",
    ];

    /// <summary>
    /// Set at startup in the Mac app, whose sandbox forbids running other programs: there's never a tailscale command
    /// to run, even with Tailscale's app installed.
    /// </summary>
    public static bool Sandboxed { get; set; }

    public static string? Cli => Sandboxed ? null : Candidates.FirstOrDefault(File.Exists);

    public async Task<TailscaleState> StateAsync(int flexarrPort, CancellationToken ct)
    {
        if (Cli is null)
        {
            return new TailscaleState(false, false, null, null, false, false, [], "Tailscale isn't installed on the server.");
        }

        var (ok, statusJson, error) = await RunAsync(ct, "status", "--json");
        if (!ok)
        {
            return new TailscaleState(true, false, null, null, false, false, [], error);
        }

        // The Mac app's own binary, run from a background service (a LaunchAgent), says "The Tailscale GUI failed to
        // start" instead of answering. Anything that isn't Tailscale's status is Tailscale being unavailable, not an
        // error in every request that asks.
        using var status = ParseStatus(statusJson);
        if (status is null)
        {
            var said = statusJson.Trim() is { Length: > 0 } text ? text.Split('\n')[0].Trim() : error;
            return new TailscaleState(true, false, null, null, false, false, [],
                $"Tailscale's command line didn't answer as expected ({said}). On a Mac, install it from the Tailscale app: Settings › Install CLI.");
        }

        var root = status.RootElement;
        var running = root.TryGetProperty("BackendState", out var backend) && backend.GetString() == "Running";
        var self = root.GetProperty("Self");
        var dns = self.TryGetProperty("DNSName", out var d) ? d.GetString()?.TrimEnd('.') : null;
        var people = new List<TailnetPerson>();
        string? owner = null;
        if (root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object)
        {
            foreach (var user in users.EnumerateObject())
            {
                var login = user.Value.TryGetProperty("LoginName", out var l) ? l.GetString() : null;
                if (login is null || login == "tagged-devices")
                {
                    continue;
                }

                people.Add(new TailnetPerson(login, user.Value.TryGetProperty("DisplayName", out var n) ? n.GetString() ?? login : login));
                if (self.TryGetProperty("UserID", out var selfId) && selfId.ToString() == user.Name)
                {
                    owner = login;
                }
            }
        }

        var (privateAccess, publicAccess) = (false, false);
        var (serveOk, serveJson, _) = await RunAsync(ct, "serve", "status", "--json");
        if (serveOk && dns is not null && serveJson.TrimStart().StartsWith('{'))
        {
            using var serve = JsonDocument.Parse(serveJson);
            var key = $"{dns}:{Port}";
            if (serve.RootElement.TryGetProperty("Web", out var web) && web.TryGetProperty(key, out var site)
                && site.TryGetProperty("Handlers", out var handlers) && handlers.TryGetProperty("/", out var handler)
                && handler.TryGetProperty("Proxy", out var proxy) && proxy.GetString()?.EndsWith($":{flexarrPort}", StringComparison.Ordinal) == true)
            {
                privateAccess = true;
            }

            publicAccess = privateAccess && serve.RootElement.TryGetProperty("AllowFunnel", out var funnel)
                && funnel.TryGetProperty(key, out var allowed) && allowed.GetBoolean();
        }

        return new TailscaleState(true, running, dns, owner, privateAccess, publicAccess, people.OrderBy(p => p.Name).ToList(),
            running ? null : "Tailscale is installed but not connected. Sign in to Tailscale on the server.");
    }

    /// <summary>
    /// Flexarr's address for away from home: its Tailscale URL while Tailscale is connected and private access is on,
    /// else null. Asked by every app at launch (<c>/system/status</c>), so the answer is kept for a minute rather than
    /// running <c>tailscale</c> each time.
    /// </summary>
    public Task<string?> RemoteUrlAsync(int flexarrPort, CancellationToken ct)
    {
        if (Cli is null)
        {
            return Task.FromResult<string?>(null);
        }

        Task<string?> lookup;
        lock (_remoteLock)
        {
            var now = clock.GetUtcNow();
            // Callers arriving together share one run; a run that failed is asked again by the next caller.
            if (_remote is { } kept && !kept.Lookup.IsFaulted && !kept.Lookup.IsCanceled && (!kept.Lookup.IsCompleted || now - kept.At < RemoteUrlLifetime))
            {
                lookup = kept.Lookup;
            }
            else
            {
                lookup = LookupRemoteUrlAsync(flexarrPort);
                _remote = (now, lookup);
            }
        }

        return lookup.WaitAsync(ct);
    }

    /// <summary>On its own clock, so a caller that gives up doesn't spoil the answer for the others.</summary>
    private async Task<string?> LookupRemoteUrlAsync(int flexarrPort)
    {
        var state = await StateAsync(flexarrPort, CancellationToken.None);
        return state.Running && state.PrivateAccess ? state.Url : null;
    }

    /// <summary>Publishes Flexarr to your tailnet (private: only your Tailscale devices can reach it).</summary>
    public async Task<string?> EnableAsync(int flexarrPort, CancellationToken ct)
    {
        var (ok, _, error) = await RunAsync(ct, "serve", "--bg", $"--https={Port}", $"http://127.0.0.1:{flexarrPort}");
        if (ok)
        {
            lock (_remoteLock)
            {
                _remote = null;
            }

            LogChanged("on");
        }

        return ok ? null : error;
    }

    public async Task<string?> DisableAsync(CancellationToken ct)
    {
        var (ok, _, error) = await RunAsync(ct, "serve", $"--https={Port}", "off");
        if (ok)
        {
            lock (_remoteLock)
            {
                _remote = null;
            }

            LogChanged("off");
        }

        return ok ? null : error;
    }

    /// <summary>
    /// Asks Tailscale who owns a tailnet address (<c>tailscale whois</c>). Used to check the identity
    /// <c>tailscale serve</c> puts on a request against the address it says the request came from. See
    /// <see cref="WhoisCache"/> for how answers are shared and kept.
    /// </summary>
    public Task<string?> LoginAsync(IPAddress address, CancellationToken ct) => _whois.LoginAsync(address, ct);

    public async Task<IReadOnlyList<string>?> TagsAsync(IPAddress address, CancellationToken ct) =>
        await _whois.AnswerAsync(address, ct) is { Definite: true } answer ? answer.Tags ?? [] : null;

    public Task<TailnetSnapshot?> SnapshotAsync(CancellationToken ct)
    {
        if (Cli is null)
        {
            return Task.FromResult<TailnetSnapshot?>(null);
        }

        Task<TailnetSnapshot?> lookup;
        lock (_snapshotLock)
        {
            var now = clock.GetUtcNow();
            if (_snapshot is { } kept && !kept.Lookup.IsFaulted && (!kept.Lookup.IsCompleted || now - kept.At < SnapshotLifetime))
            {
                lookup = kept.Lookup;
            }
            else
            {
                lookup = LookupSnapshotAsync();
                _snapshot = (now, lookup);
            }
        }

        return lookup.WaitAsync(ct);
    }

    private static async Task<TailnetSnapshot?> LookupSnapshotAsync()
    {
        var (ok, json, _) = await RunAsync(CancellationToken.None, "status", "--json");
        if (!ok)
        {
            return null;
        }

        using var status = ParseStatus(json);
        if (status is null)
        {
            return null;
        }

        var snapshot = Snapshot(status.RootElement);
        var (prefsOk, prefsJson, _) = await RunAsync(CancellationToken.None, "debug", "prefs");
        return prefsOk ? snapshot with { RelayPort = RelayPortOf(prefsJson) } : snapshot;
    }

    /// <summary>Reads <c>tailscale status --json</c>.</summary>
    internal static TailnetSnapshot Snapshot(JsonElement root)
    {
        var running = root.TryGetProperty("BackendState", out var backend) && backend.GetString() == "Running";
        var self = root.GetProperty("Self");
        var peers = new List<TailnetPeer>();
        if (root.TryGetProperty("Peer", out var all) && all.ValueKind == JsonValueKind.Object)
        {
            foreach (var peer in all.EnumerateObject())
            {
                var p = peer.Value;
                var curAddr = Text(p, "CurAddr");
                var peerRelay = Text(p, "PeerRelay");
                var relay = Text(p, "Relay");
                var path = curAddr is { Length: > 0 } ? TailnetPath.Direct
                    : peerRelay is { Length: > 0 } ? TailnetPath.PeerRelay
                    : relay is { Length: > 0 } && p.TryGetProperty("Active", out var active) && active.ValueKind == JsonValueKind.True ? TailnetPath.Derp
                    : TailnetPath.Unknown;
                DateTime? lastSeen = p.TryGetProperty("LastSeen", out var seen) && seen.ValueKind == JsonValueKind.String && seen.TryGetDateTime(out var at) && at.Year > 2000
                    ? at.ToUniversalTime()
                    : null;
                peers.Add(new TailnetPeer(Text(p, "HostName") ?? "", Ipv4(p), Strings(p, "Tags"),
                    p.TryGetProperty("Online", out var online) && online.ValueKind == JsonValueKind.True, lastSeen, path));
            }
        }

        return new TailnetSnapshot(running, Ipv4(self), Text(self, "DNSName")?.TrimEnd('.'), peers);
    }

    /// <summary>The peer relay's port from <c>tailscale debug prefs</c>, when this server is one.</summary>
    internal static int? RelayPortOf(string prefsJson)
    {
        try
        {
            using var prefs = JsonDocument.Parse(prefsJson);
            return prefs.RootElement.TryGetProperty("RelayServerPort", out var port) && port.ValueKind == JsonValueKind.Number && port.GetInt32() > 0
                ? port.GetInt32()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<string?> SetRelayPortAsync(int? port, CancellationToken ct)
    {
        if (Cli is null)
        {
            return "Tailscale isn't installed on the server.";
        }

        // An empty port turns the relay off.
        var (ok, _, error) = await RunAsync(ct, "set", $"--relay-server-port={(port is { } p ? p.ToString(System.Globalization.CultureInfo.InvariantCulture) : "")}");
        lock (_snapshotLock)
        {
            _snapshot = null;
        }

        return ok ? null : error;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> Strings(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList()
            : [];

    private static string? Ipv4(JsonElement element) =>
        Strings(element, "TailscaleIPs").FirstOrDefault(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);

    /// <summary>One <c>tailscale whois</c> run, with its own time limit (not any one request's).</summary>
    private static async Task<WhoisAnswer> WhoisAsync(IPAddress address)
    {
        if (Cli is null)
        {
            return new WhoisAnswer(null, Definite: true);
        }

        var (ok, json, error) = await RunAsync(TimeSpan.FromSeconds(5), CancellationToken.None, "whois", "--json", address.ToString());
        if (!ok)
        {
            // Didn't answer in time: worth asking again straight away. Anything else (not a peer) is an answer.
            return new WhoisAnswer(null, Definite: error != TimedOut);
        }

        try
        {
            using var whois = JsonDocument.Parse(json);
            var login = whois.RootElement.TryGetProperty("UserProfile", out var user) && user.TryGetProperty("LoginName", out var name)
                ? name.GetString()
                : null;
            var tags = whois.RootElement.TryGetProperty("Node", out var node) ? Strings(node, "Tags") : [];
            return new WhoisAnswer(login, Definite: true, tags);
        }
        catch (JsonException)
        {
            // Not what whois normally prints: treat the address as unknown.
            return new WhoisAnswer(null, Definite: true);
        }
    }

    /// <summary><c>tailscale status --json</c>'s answer, or null when it's something else.</summary>
    internal static JsonDocument? ParseStatus(string output)
    {
        try
        {
            var document = JsonDocument.Parse(output);
            if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("Self", out var self)
                && self.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Task<(bool Ok, string Output, string Error)> RunAsync(CancellationToken ct, params string[] args) =>
        RunAsync(TimeSpan.FromSeconds(20), ct, args);

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(TimeSpan limit, CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(Cli!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        Process? process = null;
        try
        {
            process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode == 0, await output, (await error).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (ex is OperationCanceledException)
            {
                // A stuck tailscale would otherwise be left running, and a whois that timed out is asked again at once.
                Stop(process);
            }

            return (false, "", ex is OperationCanceledException ? TimedOut : ex.Message);
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static void Stop(Process? process)
    {
        try
        {
            process?.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // It finished (or was never started) in the meantime.
        }
    }

    private const string TimedOut = "Tailscale didn't answer in time.";

    [LoggerMessage(Level = LogLevel.Information, Message = "Private access over Tailscale turned {State}")]
    private partial void LogChanged(string state);
}

/// <param name="Definite">Tailscale answered (even "nobody"), rather than timing out.</param>
/// <param name="Tags">The device's tags: a tagged device belongs to no one ("tagged-devices").</param>
public sealed record WhoisAnswer(string? Login, bool Definite, IReadOnlyList<string>? Tags = null);

/// <summary>
/// Who owns each tailnet address, shared by every request. A browser over Tailscale has no session cookie, so each of
/// its requests depends on this: a page of posters asks dozens at once. They wait on one lookup per address, which
/// runs on its own clock, so a request that's given up (the user scrolled on) can't spoil the answer for the rest.
/// A login is kept for a minute and "nobody" for a few seconds; a lookup that timed out isn't kept at all.
/// </summary>
public sealed class WhoisCache(Func<IPAddress, Task<WhoisAnswer>> lookup, TimeProvider clock)
{
    public static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan UnknownLifetime = TimeSpan.FromSeconds(5);
    private const int MaxRemembered = 1000;

    private readonly Lock _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];

    private sealed class Entry(Task<WhoisAnswer> lookup, WhoisAnswer? stale = null)
    {
        public Task<WhoisAnswer> Lookup { get; } = lookup;

        /// <summary>The answer before this lookup, given to requests while it runs, so a refresh never holds one up.</summary>
        public WhoisAnswer? Stale { get; } = stale;

        /// <summary>When the answer goes stale; set once the lookup finishes.</summary>
        public DateTime? KeepUntil { get; set; }
    }

    public async Task<string?> LoginAsync(IPAddress address, CancellationToken ct) => (await AnswerAsync(address, ct))?.Login;

    /// <summary>Tailscale's whole answer for <paramref name="address"/>, or null when the lookup failed.</summary>
    public async Task<WhoisAnswer?> AnswerAsync(IPAddress address, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(address);
        Entry? started = null;
        Entry entry;
        lock (_lock)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            if (!_entries.TryGetValue(address, out entry!) || entry.KeepUntil is { } until && until <= now)
            {
                if (_entries.Count >= MaxRemembered)
                {
                    foreach (var stale in _entries.Where(e => e.Value.KeepUntil <= now).Select(e => e.Key).ToList())
                    {
                        _entries.Remove(stale);
                    }
                }

                // A device's answer that's gone stale is still given out while the new one is fetched ("nobody" isn't:
                // that's asked again, so a device just linked or joined is seen at once).
                var previous = entry is { KeepUntil: not null, Lookup.IsCompletedSuccessfully: true } && entry.Lookup.Result.Login is not null ? entry.Lookup.Result : null;
                entry = started = new Entry(Task.Run(() => lookup(address), CancellationToken.None), previous);
                _entries[address] = entry;
            }
        }

        if (started is not null)
        {
            _ = started.Lookup.ContinueWith(done => Settle(address, started, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        if (entry.Stale is { } kept && !entry.Lookup.IsCompleted)
        {
            return kept;
        }

        try
        {
            return await entry.Lookup.WaitAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null; // the lookup itself failed; Settle has already let the next request try again
        }
    }

    private void Settle(IPAddress address, Entry entry, Task<WhoisAnswer> done)
    {
        lock (_lock)
        {
            var answer = done.IsCompletedSuccessfully ? done.Result : null;
            if (answer is not { Definite: true })
            {
                // Timed out or failed: the next request asks again (with the answer before this one, if there was one).
                if (_entries.TryGetValue(address, out var current) && ReferenceEquals(current, entry))
                {
                    if (entry.Stale is { } previous)
                    {
                        _entries[address] = new Entry(Task.FromResult(previous)) { KeepUntil = clock.GetUtcNow().UtcDateTime };
                    }
                    else
                    {
                        _entries.Remove(address);
                    }
                }

                return;
            }

            entry.KeepUntil = clock.GetUtcNow().UtcDateTime + (answer.Login is null ? UnknownLifetime : LoginLifetime);
        }
    }
}
