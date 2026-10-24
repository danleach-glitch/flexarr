using System.Diagnostics;
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

/// <summary>
/// Drives the local <c>tailscale</c> command: reads the tailnet's state and publishes Flexarr on it with
/// <c>tailscale serve</c>, which gives it an HTTPS address, a certificate, and each visitor's verified identity.
/// Flexarr uses its own port (8443) so anything already served on 443 is left alone.
/// </summary>
public sealed partial class TailscaleService(ILogger<TailscaleService> logger)
{
    public const int Port = 8443;

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

    private static async Task<(bool Ok, string Output, string Error)> RunAsync(CancellationToken ct, params string[] args)
    {
        var start = new ProcessStartInfo(Cli!) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode == 0, await output, (await error).Trim());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            return (false, "", ex is OperationCanceledException ? "Tailscale didn't answer in time." : ex.Message);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Private access over Tailscale turned {State}")]
    private partial void LogChanged(string state);
}
