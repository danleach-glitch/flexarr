using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flexarr.Api.Configuration;
using Microsoft.Extensions.Options;
using Flexarr.Core.Localization;

namespace Flexarr.Api.Security;

/// <summary>
/// Inviting a guest's TV by the code it shows. A far-away TV can't reach this server, so the two meet at Flexarr's
/// relay: the TV leaves its public key there under its code for ten minutes; this server fetches the key, makes an
/// invite, encrypts it to the key and leaves it for the TV to collect once. The relay only ever sees public keys and
/// ciphertext. Encryption: ECDH on P-256 with a fresh key of ours, HKDF-SHA256 over the shared secret (salt: the TV's
/// key then ours, info "flexarr-tv-invite"), AES-256-GCM, laid out as CryptoKit's SealedBox.combined
/// (nonce, ciphertext, tag). Keys travel as uncompressed points (X9.63, 65 bytes), base64. See docs/remote-guests.md.
/// </summary>
public sealed partial class GuestTvCodes(IHttpClientFactory clients, IOptionsMonitor<FlexarrOptions> options, ILogger<GuestTvCodes> logger)
{
    public const string HttpClientName = "pair-relay";

    public static readonly byte[] Info = "flexarr-tv-invite"u8.ToArray();

    /// <summary>Letters and digits that can't be mistaken for each other when read out over the phone.</summary>
    public const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";

    private HttpClient Http => clients.CreateClient(HttpClientName);

    private string PairUrl => options.CurrentValue.RemoteGuests.PairUrl.TrimEnd('/');

    /// <summary>"abcd efgh", "ABCD-EFGH" or "abcdefgh" as the relay keeps it (ABCDEFGH); null when it can't be a code.</summary>
    public static string? Normalize(string? code)
    {
        var letters = new string((code ?? "").Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return letters.Length == 8 && letters.All(c => Alphabet.Contains(c, StringComparison.Ordinal)) ? letters : null;
    }

    /// <summary>
    /// The code a TV's key makes (docs/remote-guests.md, "The code proves the key"): the SHA-256 of
    /// <paramref name="publicKey"/>'s UTF-8 bytes (the base64 exactly as the TV sends it), its first seven bytes each
    /// picking a character, then <see cref="CheckCharacter"/>.
    /// </summary>
    public static string CodeFor(string publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(publicKey));
        var first = new string([.. h.Take(7).Select(b => Alphabet[b % Alphabet.Length])]);
        return first + CheckCharacter(first);
    }

    /// <summary>The eighth character of a code made from a key: the sum of each character's place (1 to 7) times its index, mod 31.</summary>
    private static char CheckCharacter(string first) => Alphabet[first.Take(7).Select((c, i) => (i + 1) * Alphabet.IndexOf(c, StringComparison.Ordinal)).Sum() % Alphabet.Length];

    /// <summary>
    /// True when a (normalized) code ends in its check character, so it was made from the TV's key and the key the
    /// relay hands back must make it. TV apps from before that make their codes at random, and those aren't checked.
    /// </summary>
    public static bool IsKeyCode(string code) => code is { Length: 8 } && CheckCharacter(code) == code[7];

    /// <summary>The public key the TV showing <paramref name="code"/> left at the relay; null when no TV is showing it.</summary>
    public async Task<byte[]?> TvKeyAsync(string code, CancellationToken ct)
    {
        using var response = await Http.GetAsync(new Uri($"{PairUrl}?code={Uri.EscapeDataString(code)}"), ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        if ((int)response.StatusCode >= 500)
        {
            // The relay had a problem part way: it may have kept the invite all the same.
            throw new MaybeDeliveredException($"Flexarr's relay answered {(int)response.StatusCode}.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InviteException(Loc.T("Flexarr’s relay answered {status}. Try again in a minute.", new { status = (int)response.StatusCode }), StatusCodes.Status502BadGateway);
        }

        string? publicKey;
        byte[] key;
        try
        {
            var body = await response.Content.ReadFromJsonAsync<TvKey>(ct);
            publicKey = body?.PublicKey;
            key = Convert.FromBase64String(publicKey ?? "");
            if (key.Length != 65 || key[0] != 0x04)
            {
                return null;
            }

            // A point that isn't on the curve can't be sealed to: found now, before an invite is made for it.
            using var _ = ECDiffieHellman.Create(PublicParameters(key));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or NotSupportedException or CryptographicException)
        {
            return null;
        }

        // The code was made from the TV's key, and this key doesn't make it: it isn't that TV's, so nothing is sealed to it.
        if (IsKeyCode(code) && !string.Equals(CodeFor(publicKey!), code, StringComparison.Ordinal))
        {
            LogKeyMismatch();
            throw new InviteException(Loc.T("That code doesn’t match the device showing it. Check the one on their screen now, and try again."), StatusCodes.Status502BadGateway);
        }

        return key;
    }

    /// <summary>Encrypts <paramref name="invite"/> (the encoded payload) to the TV's key and leaves it at the relay.</summary>
    public async Task DeliverAsync(string code, byte[] tvKey, string invite, CancellationToken ct)
    {
        var (serverKey, sealedBox) = Seal(tvKey, Encoding.UTF8.GetBytes(invite));
        using var response = await Http.PostAsJsonAsync(new Uri($"{PairUrl}?code={Uri.EscapeDataString(code)}&action=invite"),
            new SealedInvite(Convert.ToBase64String(serverKey), Convert.ToBase64String(sealedBox)), ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
        {
            throw new InviteException(Loc.T("Their device stopped showing that code. Check the one on their screen now."), StatusCodes.Status410Gone);
        }

        if (response.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InviteException(Loc.T("That code has been used. Check the one on their screen now."), StatusCodes.Status409Conflict);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InviteException(Loc.T("Flexarr’s relay answered {status}. Try again in a minute.", new { status = (int)response.StatusCode }), StatusCodes.Status502BadGateway);
        }

        LogDelivered();
    }

    /// <summary>Seals <paramref name="plaintext"/> for the holder of <paramref name="tvKey"/>; returns our one-off public key and the box.</summary>
    public static (byte[] ServerKey, byte[] SealedBox) Seal(byte[] tvKey, byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(tvKey);
        ArgumentNullException.ThrowIfNull(plaintext);
        using var tv = ECDiffieHellman.Create(PublicParameters(tvKey));
        using var ours = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var serverKey = Uncompressed(ours.ExportParameters(includePrivateParameters: false));
        var key = DeriveKey(ours.DeriveRawSecretAgreement(tv.PublicKey), tvKey, serverKey);
        var box = new byte[12 + plaintext.Length + 16];
        RandomNumberGenerator.Fill(box.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(box.AsSpan(0, 12), plaintext, box.AsSpan(12, plaintext.Length), box.AsSpan(12 + plaintext.Length));
        return (serverKey, box);
    }

    /// <summary>What the TV does to open the box (here for tests, and as the reference for the apps).</summary>
    public static byte[] Open(ECDiffieHellman tv, byte[] serverKey, byte[] sealedBox)
    {
        ArgumentNullException.ThrowIfNull(tv);
        ArgumentNullException.ThrowIfNull(serverKey);
        ArgumentNullException.ThrowIfNull(sealedBox);
        using var server = ECDiffieHellman.Create(PublicParameters(serverKey));
        var tvKey = Uncompressed(tv.ExportParameters(includePrivateParameters: false));
        var key = DeriveKey(tv.DeriveRawSecretAgreement(server.PublicKey), tvKey, serverKey);
        var plaintext = new byte[sealedBox.Length - 28];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(sealedBox.AsSpan(0, 12), sealedBox.AsSpan(12, plaintext.Length), sealedBox.AsSpan(12 + plaintext.Length), plaintext);
        return plaintext;
    }

    private static byte[] DeriveKey(byte[] shared, byte[] tvKey, byte[] serverKey) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, [.. tvKey, .. serverKey], Info);

    public static byte[] Uncompressed(ECParameters key) => [0x04, .. key.Q.X!, .. key.Q.Y!];

    private static ECParameters PublicParameters(byte[] uncompressed)
    {
        if (uncompressed.Length != 65 || uncompressed[0] != 0x04)
        {
            throw new CryptographicException("Not an uncompressed P-256 public key.");
        }

        return new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..] } };
    }

    /// <summary>Sending the invite went wrong in a way that may have left it at the relay all the same.</summary>
    public sealed class MaybeDeliveredException(string message) : Exception(message);

    private sealed record TvKey([property: JsonPropertyName("publicKey")] string? PublicKey);

    private sealed record SealedInvite([property: JsonPropertyName("serverKey")] string ServerKey, [property: JsonPropertyName("sealed")] string Sealed);

    [LoggerMessage(Level = LogLevel.Information, Message = "Left a sealed invite at the relay for a device")]
    private partial void LogDelivered();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Flexarr's relay handed back a key that doesn't make the code typed; no invite was sealed to it")]
    private partial void LogKeyMismatch();
}
