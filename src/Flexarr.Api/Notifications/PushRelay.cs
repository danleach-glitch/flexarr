using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Flexarr.Core.Notifications;
using Microsoft.Extensions.Options;

namespace Flexarr.Api.Notifications;

/// <summary>
/// What a notification says, as the phone's notification extension reads it once it's unsealed (see
/// docs/notifications.md): the short names keep it well inside Apple's 4 KB.
/// </summary>
/// <param name="Category">Decides the buttons: flexarr.video, flexarr.podcast, flexarr.recording, flexarr.upcoming or flexarr.test.</param>
/// <param name="Thread">Groups a show's notifications together on the phone.</param>
/// <param name="Image">Artwork from a public host, which the phone fetches itself.</param>
/// <param name="Open">What tapping opens and what Play plays (item, play, podcast, episode).</param>
public sealed record PushMessage(
    [property: JsonPropertyName("t")] string Title,
    [property: JsonPropertyName("b")] string Body,
    [property: JsonPropertyName("c")] string Category,
    [property: JsonPropertyName("g")] string? Thread = null,
    [property: JsonPropertyName("i")] string? Image = null,
    [property: JsonPropertyName("u")] IReadOnlyDictionary<string, string>? Open = null);

public enum PushOutcome
{
    Sent,

    /// <summary>Apple or Google no longer knows the phone (the app was deleted, or notifications turned off for good).</summary>
    Gone,

    Failed,
}

/// <summary>
/// Seals notifications with each phone's own key and hands them to the relay, which passes them to Apple, or to Google
/// for an Android phone.
/// </summary>
public sealed partial class PushRelay(HttpClient http, IOptionsMonitor<FlexarrOptions> options, ILogger<PushRelay> logger)
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    public bool IsEnabled => options.CurrentValue.Notifications is { Enabled: true, RelayUrl.Length: > 0 };

    public async Task<(PushOutcome Outcome, string? Reason)> SendAsync(NotificationDevice device, PushMessage message, string? collapseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(message);
        if (!IsEnabled || !Uri.TryCreate(options.CurrentValue.Notifications.RelayUrl, UriKind.Absolute, out var relay))
        {
            return (PushOutcome.Failed, "Notifications are turned off on this server.");
        }

        var payload = Seal(device.Key, JsonSerializer.SerializeToUtf8Bytes(message, Json));
        try
        {
            using var response = await http.PostAsJsonAsync(relay, new RelayRequest(device.Token, device.Environment, payload, collapseId,
                device.Platform == NotificationPlatforms.Android ? NotificationPlatforms.Android : null), Json, ct);
            if (response.IsSuccessStatusCode)
            {
                return (PushOutcome.Sent, null);
            }

            var reason = await ReasonAsync(response, ct);
            LogRefused((int)response.StatusCode, reason);
            return response.StatusCode == HttpStatusCode.Gone || reason is "BadDeviceToken" or "DeviceTokenNotForTopic"
                ? (PushOutcome.Gone, reason)
                : (PushOutcome.Failed, reason ?? $"The relay answered {(int)response.StatusCode}.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            LogUnreachable(ex.Message);
            return (PushOutcome.Failed, "Couldn't reach the notification relay.");
        }
    }

    /// <summary>AES-256-GCM, laid out as CryptoKit's <c>SealedBox.combined</c>: nonce, ciphertext, tag.</summary>
    public static string Seal(byte[] key, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        var sealedBox = new byte[NonceSize + plaintext.Length + TagSize];
        var nonce = sealedBox.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, sealedBox.AsSpan(NonceSize, plaintext.Length), sealedBox.AsSpan(NonceSize + plaintext.Length));
        return Convert.ToBase64String(sealedBox);
    }

    public static byte[] Open(byte[] key, string sealedBase64)
    {
        var sealedBox = Convert.FromBase64String(sealedBase64);
        var plaintext = new byte[sealedBox.Length - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(sealedBox.AsSpan(0, NonceSize), sealedBox.AsSpan(NonceSize, plaintext.Length), sealedBox.AsSpan(NonceSize + plaintext.Length), plaintext);
        return plaintext;
    }

    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            return body.RootElement.TryGetProperty("reason", out var reason) ? reason.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <param name="Platform">"android" for Firebase; left out for Apple, as relays from before Android expect.</param>
    private sealed record RelayRequest(string Token, string Environment, string Payload, string? CollapseId, string? Platform);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The notification relay refused a push ({Status}: {Reason})")]
    private partial void LogRefused(int status, string? reason);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Couldn't reach the notification relay: {Error}")]
    private partial void LogUnreachable(string error);
}

/// <summary>Sends a notification to every phone a profile has asked for that kind of news on.</summary>
public sealed partial class Notifier(PushRelay relay, Authentication.SignIns signIns, ILogger<Notifier> logger)
{
    /// <returns>How many phones it reached.</returns>
    public async Task<int> SendAsync(Core.Data.FlexarrDbContext db, IEnumerable<NotificationDevice> devices, PushMessage message, string? collapseId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(devices);
        var live = signIns.List().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var sent = 0;
        foreach (var device in devices)
        {
            // Signed out (or revoked from the web): it hears nothing more.
            if (device.SignInId is { } signIn && !live.Contains(signIn))
            {
                db.NotificationDevices.Remove(device);
                continue;
            }

            var (outcome, _) = await relay.SendAsync(device, message, collapseId, ct);
            switch (outcome)
            {
                case PushOutcome.Sent:
                    sent++;
                    break;
                case PushOutcome.Gone:
                    LogForgotten(device.Id);
                    db.NotificationDevices.Remove(device);
                    break;
            }
        }

        await db.SaveChangesAsync(ct);
        return sent;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The push service no longer knows phone {Id}; it won't be sent notifications")]
    private partial void LogForgotten(string id);
}
