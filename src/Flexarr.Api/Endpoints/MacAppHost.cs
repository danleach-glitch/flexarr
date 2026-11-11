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

    /// <summary>Set ("true") by the app when it writes the token on the server's standard input rather than in its environment.</summary>
    public const string TokenOnInputKey = "Flexarr:MacApp:TokenOnInput";

    /// <summary>
    /// The app writes the token, and a newline, on standard input before the server starts, where other programs can't
    /// read it as they could the environment. Read once, at the start, and only when the app says it sent it there;
    /// Flexarr:MacApp:Token in the environment, as earlier builds of the app pass it, works too.
    /// </summary>
    public static void ReadTokenFromInput(ConfigurationManager configuration, TextReader input)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(input);
        if (!string.Equals(configuration[TokenOnInputKey], "true", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (input.ReadLine()?.Trim() is { Length: > 0 } token)
        {
            configuration.AddInMemoryCollection([new KeyValuePair<string, string?>("Flexarr:MacApp:Token", token)]);
        }
    }

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

    /// <summary>Tells the app what the server is called, which it announces on the network and shows in its menu.</summary>
    public Task AnnounceNameAsync(string name, CancellationToken cancellationToken) => SendAsync("name", name, cancellationToken);

    /// <summary>What the key that seals the app's Reset Admin PIN is derived with (HKDF-SHA256 of the token, salted with the request's nonce).</summary>
    public static readonly byte[] PinResetInfo = "flexarr-admin-pin-reset"u8.ToArray();

    /// <summary>How far a sealed reset's time may be from now, and how long its nonce is remembered.</summary>
    public static readonly TimeSpan PinResetWindow = TimeSpan.FromMinutes(2);

    private readonly Dictionary<string, DateTimeOffset> usedNonces = new(StringComparer.Ordinal);

    /// <summary>
    /// Opens the app's Reset Admin PIN (AdminPinReset.swift): <paramref name="sealedBox"/> is AES-256-GCM, as CryptoKit's
    /// SealedBox.combined (12-byte nonce, ciphertext, tag), keyed by HKDF-SHA256 over the token's UTF-8 bytes with
    /// <paramref name="nonce"/> as salt and <see cref="PinResetInfo"/>, around <c>{"pin": "…", "at": unix seconds}</c>.
    /// Only the app has the token, and nothing secret travels: whatever else answers on the port learns neither the
    /// token nor the PIN. The new PIN, or null when it isn't the app's, is stale or was sent before.
    /// </summary>
    public string? OpenPinReset(string? nonce, string? sealedBox, DateTimeOffset now)
    {
        if (!IsAvailable || nonce is null || sealedBox is null)
        {
            return null;
        }

        try
        {
            var salt = Convert.FromBase64String(nonce);
            var box = Convert.FromBase64String(sealedBox);
            if (salt.Length < 16 || box.Length < 28 || box.Length > 1024)
            {
                return null;
            }

            var key = System.Security.Cryptography.HKDF.DeriveKey(System.Security.Cryptography.HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(token!), 32, salt, PinResetInfo);
            var plaintext = new byte[box.Length - 28];
            using (var aes = new System.Security.Cryptography.AesGcm(key, 16))
            {
                aes.Decrypt(box.AsSpan(0, 12), box.AsSpan(12, plaintext.Length), box.AsSpan(12 + plaintext.Length), plaintext);
            }

            using var json = System.Text.Json.JsonDocument.Parse(plaintext);
            var at = DateTimeOffset.FromUnixTimeSeconds(json.RootElement.GetProperty("at").GetInt64());
            var pin = json.RootElement.GetProperty("pin").GetString();
            if ((now - at).Duration() > PinResetWindow)
            {
                return null;
            }

            lock (usedNonces)
            {
                foreach (var old in usedNonces.Where(n => now - n.Value > PinResetWindow * 2).Select(n => n.Key).ToList())
                {
                    usedNonces.Remove(old);
                }

                // By the bytes, not the text: base64 decoding skips whitespace, so a padded copy of the text would be new.
                if (!usedNonces.TryAdd(Convert.ToBase64String(salt), now))
                {
                    return null;
                }
            }

            return pin;
        }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException
            or KeyNotFoundException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

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

        if (mac.IsAvailable)
        {
            options.Transcoding.SoftwareFallback = false;
        }
    }
}
