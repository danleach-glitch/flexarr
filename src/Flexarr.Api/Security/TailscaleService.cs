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
}

/// <summary>
/// Drives the local <c>tailscale</c> command: reads the tailnet's state and publishes Flexarr on it with
/// <c>tailscale serve</c>, which gives it an HTTPS address, a certificate, and each visitor's verified identity.
/// Flexarr uses its own port (8443) so anything already served on 443 is left alone.
/// </summary>
public sealed partial class TailscaleService(ILogger<TailscaleService> logger, TimeProvider clock) : ITailnetWhois
{
    public const int Port = 8443;

    private readonly WhoisCache _whois = new(WhoisAsync, clock);

    private static readonly string[] Candidates =
    [
        "/usr/local/bin/tailscale",
        "/opt/homebrew/bin/tailscale",
        "/Applications/Tailscale.app/Contents/MacOS/Tailscale",
        "/usr/bin/tailscale",
        "/usr/sbin/tailscale",
        @"C:\Program Files\Tailscale\tailscale.exe",
    ];

    public static string? Cli => Candidates.FirstOrDefault(File.Exists);

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

        using var status = JsonDocument.Parse(statusJson);
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

    /// <summary>Publishes Flexarr to your tailnet (private: only your Tailscale devices can reach it).</summary>
    public async Task<string?> EnableAsync(int flexarrPort, CancellationToken ct)
    {
        var (ok, _, error) = await RunAsync(ct, "serve", "--bg", $"--https={Port}", $"http://127.0.0.1:{flexarrPort}");
        if (ok)
        {
            LogChanged("on");
        }

        return ok ? null : error;
    }

    public async Task<string?> DisableAsync(CancellationToken ct)
    {
        var (ok, _, error) = await RunAsync(ct, "serve", $"--https={Port}", "off");
        if (ok)
        {
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
            return new WhoisAnswer(login, Definite: true);
        }
        catch (JsonException)
        {
            // Not what whois normally prints: treat the address as unknown.
            return new WhoisAnswer(null, Definite: true);
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
public sealed record WhoisAnswer(string? Login, bool Definite);

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

    private sealed class Entry(Task<WhoisAnswer> lookup)
    {
        public Task<WhoisAnswer> Lookup { get; } = lookup;

        /// <summary>When the answer goes stale; set once the lookup finishes.</summary>
        public DateTime? KeepUntil { get; set; }
    }

    public async Task<string?> LoginAsync(IPAddress address, CancellationToken ct)
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

                entry = started = new Entry(Task.Run(() => lookup(address), CancellationToken.None));
                _entries[address] = entry;
            }
        }

        if (started is not null)
        {
            _ = started.Lookup.ContinueWith(done => Settle(address, started, done), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        try
        {
            return (await entry.Lookup.WaitAsync(ct)).Login;
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
                // Timed out or failed: the next request asks again.
                if (_entries.TryGetValue(address, out var current) && ReferenceEquals(current, entry))
                {
                    _entries.Remove(address);
                }

                return;
            }

            entry.KeepUntil = clock.GetUtcNow().UtcDateTime + (answer.Login is null ? UnknownLifetime : LoginLifetime);
        }
    }
}
