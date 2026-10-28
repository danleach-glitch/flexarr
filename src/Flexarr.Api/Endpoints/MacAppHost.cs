using System.Net;
using System.Net.Sockets;
using System.Text;
using Flexarr.Api.Configuration;
using Flexarr.Streaming;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Endpoints;

/// <summary>
/// The Flexarr Server Mac app (clients/macos), when this server runs inside it. The app's sandbox only lets the server
/// read folders someone chose in macOS's folder window, so choosing a folder asks the app to show one; the app also
/// owns starting at login. It passes a local port and a token in Flexarr:MacApp and answers one line per connection:
/// the token, a command and, after a tab, its argument go in, and one line comes back.
/// <list type="bullet">
/// <item>choose [folder to start in, ~ for home]: the chosen folder's path, or nothing if cancelled.</item>
/// <item>login-item [on|off]: whether the app starts at login (after changing it, if asked): on, off, or approval
/// when macOS is waiting for the person to allow it in System Settings.</item>
/// </list>
/// </summary>
public sealed class MacAppHost(IConfiguration configuration)
{
    private readonly int? port = int.TryParse(configuration["Flexarr:MacApp:Port"], out var p) ? p : null;
    private readonly string? token = configuration["Flexarr:MacApp:Token"];

    public bool IsAvailable => port is not null && !string.IsNullOrEmpty(token);

    /// <summary>Shows the folder window on the Mac and waits for the person there. Null when they cancel.</summary>
    public Task<string?> ChooseFolderAsync(string? start, CancellationToken cancellationToken) =>
        SendAsync("choose", start, cancellationToken);

    /// <summary>Whether the app starts when someone logs in to the Mac, changing it first if <paramref name="on"/> is given.</summary>
    public async Task<StartAtLogin> StartAtLoginAsync(bool? on, CancellationToken cancellationToken) =>
        await SendAsync("login-item", on switch { true => "on", false => "off", null => null }, cancellationToken) switch
        {
            "on" => StartAtLogin.On,
            "approval" => StartAtLogin.NeedsApproval,
            _ => StartAtLogin.Off,
        };

    private async Task<string?> SendAsync(string command, string? argument, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            throw new InvalidOperationException("Not running in the Mac app.");
        }

        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port!.Value, cancellationToken);
        await using var stream = client.GetStream();
        var tail = string.IsNullOrWhiteSpace(argument) ? "" : "\t" + argument.ReplaceLineEndings("").Replace('\t', ' ');
        await stream.WriteAsync(Encoding.UTF8.GetBytes($"{token} {command}{tail}\n"), cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var answer = await reader.ReadLineAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(answer) ? null : answer;
    }
}

public enum StartAtLogin
{
    Off,
    On,

    /// <summary>Turned on, but macOS waits for the person to allow it in System Settings › General › Login Items.</summary>
    NeedsApproval,
}

/// <summary>
/// In the Mac app, video is always converted with VideoToolbox: its FFmpeg has no software encoder (libx264 is GPL).
/// A setting saved or restored from another server (software, VA-API) goes back to Auto, which is VideoToolbox on a Mac.
/// </summary>
public sealed class MacAppTranscoding(MacAppHost mac) : IPostConfigureOptions<FlexarrOptions>
{
    public void PostConfigure(string? name, FlexarrOptions options)
    {
        if (mac.IsAvailable && options.Transcoding.HardwareAcceleration is not (HardwareAcceleration.Auto or HardwareAcceleration.VideoToolbox))
        {
            options.Transcoding.HardwareAcceleration = HardwareAcceleration.Auto;
        }
    }
}
