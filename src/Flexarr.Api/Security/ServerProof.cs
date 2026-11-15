using System.Security.Cryptography;
using System.Text;

namespace Flexarr.Api.Security;

/// <summary>
/// How a server shows an app that it holds the app's key before the app sends it there over plain HTTP. The server's
/// id alone isn't enough: anyone may ask for it, and something else at the same address on another network could
/// give it too. So the app sends a fresh random challenge and which key it holds (<see cref="KeyId"/>, from which the
/// key can't be worked out), and the server answers with <see cref="Answer"/>, which only a holder of the key can
/// work out. Both sides derive it from the key's SHA-256 (what the server keeps of a device's token).
/// </summary>
public static class ServerProof
{
    /// <summary>What an app calls the key it holds, given the key's SHA-256 (lowercase hex).</summary>
    public static string KeyId(string keyHash) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes($"flexarr key id|{keyHash}")));

    /// <summary>The answer to <paramref name="challenge"/> from a server with id <paramref name="serverId"/> that holds the key.</summary>
    public static string Answer(string keyHash, string serverId, string challenge) =>
        Hex(HMACSHA256.HashData(Encoding.UTF8.GetBytes(keyHash), Encoding.UTF8.GetBytes($"flexarr proof|{serverId}|{challenge}")));

    /// <summary>The SHA-256 of a key, as both sides start from.</summary>
    public static string HashOf(string key) => Hex(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>A challenge worth answering: 32 to 128 lowercase hex digits.</summary>
    public static bool IsChallenge(string? challenge) => challenge is { Length: >= 32 and <= 128 } && challenge.All(char.IsAsciiHexDigitLower);

    /// <summary>A key id as <see cref="KeyId"/> makes them.</summary>
    public static bool IsKeyId(string? keyId) => keyId is { Length: 64 } && keyId.All(char.IsAsciiHexDigitLower);

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(bytes);
}
