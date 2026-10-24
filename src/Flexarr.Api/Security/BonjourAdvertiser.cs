using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Flexarr.Api.Security;

/// <summary>
/// Announces Flexarr on the local network (Bonjour, "_flexarr._tcp") so apps can list it instead of asking for an
/// address. The TXT record carries the address to use. Uses the system's own tools: dns-sd on macOS, avahi-publish on
/// Linux when installed; elsewhere apps fall back to typing the address.
/// </summary>
public sealed partial class BonjourAdvertiser(IServer server, IHostApplicationLifetime lifetime, ILogger<BonjourAdvertiser> logger) : BackgroundService
{
    public const string ServiceType = "_flexarr._tcp";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Wait until Kestrel is listening, so the port is known.
        var started = new TaskCompletionSource();
        using var _ = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);

        var port = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a.Replace("*", "localhost", StringComparison.Ordinal).Replace("+", "localhost", StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(u => u is { Scheme: "http" })?.Port;
        if (port is null)
        {
            return;
        }

        var host = await LocalHostNameAsync(stoppingToken);
        var url = $"http://{host}.local:{port}";
        var name = $"Flexarr on {host}";
        string[]? command = OperatingSystem.IsMacOS() ? ["/usr/bin/dns-sd", "-R", name, ServiceType, "local", port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), $"url={url}"]
            : File.Exists("/usr/bin/avahi-publish") ? ["/usr/bin/avahi-publish", "-s", name, ServiceType, port.Value.ToString(System.Globalization.CultureInfo.InvariantCulture), $"url={url}"]
            : null;
        if (command is null)
        {
            return;
        }

        // The tool keeps the announcement up while it runs; restart it if it stops.
        while (!stoppingToken.IsCancellationRequested)
        {
            var start = new ProcessStartInfo(command[0]) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in command[1..])
            {
                start.ArgumentList.Add(arg);
            }

            try
            {
                using var process = Process.Start(start)!;
                LogAdvertising(name, url);
                try
                {
                    await process.WaitForExitAsync(stoppingToken);
                }
                finally
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                LogUnavailable(ex.Message);
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        }
    }

    /// <summary>macOS's Bonjour name ("Dans-Mac-Mini"), else the machine name.</summary>
    private static async Task<string> LocalHostNameAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsMacOS())
        {
            try
            {
                var start = new ProcessStartInfo("/usr/sbin/scutil") { UseShellExecute = false, RedirectStandardOutput = true };
                start.ArgumentList.Add("--get");
                start.ArgumentList.Add("LocalHostName");
                using var process = Process.Start(start)!;
                var name = (await process.StandardOutput.ReadToEndAsync(ct)).Trim();
                await process.WaitForExitAsync(ct);
                if (name.Length > 0)
                {
                    return name;
                }
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // Fall back to the machine name.
            }
        }

        return Environment.MachineName;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Announcing \"{Name}\" on the local network at {Url}")]
    private partial void LogAdvertising(string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not announcing on the local network: {Reason}")]
    private partial void LogUnavailable(string reason);
}
