using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Flexarr.Api.Security;

/// <summary>
/// Announces Flexarr on the local network (Bonjour, "_flexarr._tcp") so apps can list it instead of asking for an
/// address. The TXT record carries the address to use. Uses the system's own tools when there are some (dns-sd on
/// macOS, avahi-publish on Linux), and otherwise answers itself (<see cref="MdnsResponder"/>): in Docker, on Linux
/// without Avahi, and on Windows. Not in the Mac app, whose sandbox leaves the server only its own FFmpeg to run: the
/// app announces the server itself (ServerProcess.swift). The announcement carries the server's name
/// (<see cref="Configuration.ServerName"/>), and starts again under the new one when an admin renames it.
/// </summary>
public sealed partial class BonjourAdvertiser(IServer server, IHostApplicationLifetime lifetime, Endpoints.MacAppHost macApp,
    Microsoft.Extensions.Options.IOptionsMonitor<Configuration.FlexarrOptions> options, ILogger<BonjourAdvertiser> logger)
    : BackgroundService
{
    public const string ServiceType = "_flexarr._tcp";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (macApp.IsAvailable)
        {
            await TellMacAppAsync(stoppingToken);
            return;
        }

        // Wait until Kestrel is listening, so the port is known.
        var started = new TaskCompletionSource();
        using var _ = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(stoppingToken);

        // A server listening only on loopback (say, behind Tailscale Funnel) can't be reached from the network: don't announce it.
        var port = server.Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => Uri.TryCreate(a.Replace("*", "0.0.0.0", StringComparison.Ordinal).Replace("+", "0.0.0.0", StringComparison.Ordinal), UriKind.Absolute, out var uri) ? uri : null)
            .FirstOrDefault(u => u is { Scheme: "http", IsLoopback: false })?.Port;
        if (port is null)
        {
            return;
        }

        // The same lookup the server's name defaults to, done once, off any request.
        var host = await Task.Run(() => Configuration.ServerName.MachineName ?? Environment.MachineName, stoppingToken);
        var url = $"http://{host}.local:{port}";
        while (!stoppingToken.IsCancellationRequested)
        {
            // Announce under the current name until it changes, then withdraw that and announce the new one. The
            // change is listened for before the name is read, so a rename in between isn't missed.
            using var renamed = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            string? name = null;
            using var watch = options.OnChange((o, _) =>
            {
                if (name is not null && Configuration.ServerName.Of(o) != name)
                {
                    renamed.Cancel();
                }
            });
            name = Configuration.ServerName.Of(options.CurrentValue);

            try
            {
                if (!await AnnounceAsync(name, host, port.Value, url, renamed.Token))
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
            {
                LogRenamed(name);
            }
        }
    }

    /// <summary>
    /// In the Mac app, which announces the server itself: tells it the name once the server's up, and again after
    /// each rename. An app from before names doesn't know the command, and keeps its own.
    /// </summary>
    private async Task TellMacAppAsync(CancellationToken ct)
    {
        var started = new TaskCompletionSource();
        using var _ = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await started.Task.WaitAsync(ct);
        var renamed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest });
        using var watch = options.OnChange((_, _) => renamed.Writer.TryWrite(true));
        string? told = null;
        while (!ct.IsCancellationRequested)
        {
            var name = Configuration.ServerName.Of(options.CurrentValue);
            if (name != told)
            {
                // A few seconds each, so an app that stops answering can't hold up later renames.
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                try
                {
                    await macApp.AnnounceNameAsync(name, timeout.Token);
                    told = name;
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException
                                               || (ex is OperationCanceledException && !ct.IsCancellationRequested))
                {
                    LogUnavailable(ex.Message);
                    // Tried again shortly, rather than only after the next settings change.
                    await Task.WhenAny(renamed.Reader.ReadAsync(ct).AsTask(), Task.Delay(TimeSpan.FromSeconds(10), ct));
                    continue;
                }
            }

            await renamed.Reader.ReadAsync(ct);
        }
    }

    /// <summary>Announces until <paramref name="ct"/> ends; false when there's no way to announce at all.</summary>
    private async Task<bool> AnnounceAsync(string name, string host, int port, string url, CancellationToken ct)
    {
        var portText = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        string[]? command = OperatingSystem.IsMacOS() ? ["/usr/bin/dns-sd", "-R", name, ServiceType, "local", portText, $"url={url}"]
            // "--": a name starting with "-" is the name, not an option.
            : File.Exists("/usr/bin/avahi-publish") ? ["/usr/bin/avahi-publish", "-s", "--", name, ServiceType, portText, $"url={url}"]
            : null;
        if (command is null)
        {
            // This responder doesn't settle clashes as dns-sd and Avahi do, and every unnamed container is "Flexarr":
            // a few letters of the server's id keep two apart.
            var unique = !Configuration.ServerName.IsNamed(options.CurrentValue) && options.CurrentValue.ServerId is { Length: >= 4 } id
                ? $"{name} ({id[..4]})" : name;
            return await RespondAsync(new MdnsService(unique, ServiceType, host, port, url), ct);
        }

        // The tool keeps the announcement up while it runs; restart it if it stops.
        while (true)
        {
            ct.ThrowIfCancellationRequested();
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
                    await process.WaitForExitAsync(ct);
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
                return false;
            }

            await Task.Delay(TimeSpan.FromSeconds(30), ct);
        }
    }

    /// <summary>No system tool to announce with: answer mDNS queries here. If port 5353 can't be had, apps fall back to typing the address.</summary>
    private async Task<bool> RespondAsync(MdnsService service, CancellationToken ct)
    {
        try
        {
            using var responder = new MdnsResponder(service, logger);
            LogAdvertising(service.InstanceName, service.Url);
            await responder.RunAsync(ct);
            ct.ThrowIfCancellationRequested();
            return true;
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            LogUnavailable(ex.Message);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Announcing \"{Name}\" on the local network at {Url}")]
    private partial void LogAdvertising(string name, string url);

    [LoggerMessage(Level = LogLevel.Information, Message = "Renamed: no longer announcing \"{Name}\"")]
    private partial void LogRenamed(string name);

    [LoggerMessage(Level = LogLevel.Information, Message = "Not announcing on the local network: {Reason}")]
    private partial void LogUnavailable(string reason);
}
