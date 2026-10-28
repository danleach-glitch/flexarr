using System.Security.Cryptography;

namespace Flexarr.Api.Updates;

/// <summary>
/// Checks a release's SHA256SUMS against SHA256SUMS.sig, made with the update signing key on the releasing Mac
/// (scripts/release.sh publish). The checksums alone only catch a damaged download: anyone who could publish a release
/// on GitHub could publish matching ones. Only a release signed with this key is installed by itself.
/// </summary>
public static class UpdateSignature
{
    public const string FileName = "SHA256SUMS.sig";

    /// <summary>The public half of the update signing key (P-256), from scripts/release.sh signing-key.</summary>
    internal const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEmsHy0ODzQcCbdlUlKp9Z5NfhFr+e
        2FPlOeEjQiK1Gh8rFo6ZT0ZItELOQmjUEa1jnT0IRGNzPYU815b1UlBb6g==
        -----END PUBLIC KEY-----
        """;

    /// <summary>Whether <paramref name="signature"/> (openssl's DER ECDSA) is the signing key's, over exactly <paramref name="sums"/>.</summary>
    public static bool Verify(byte[] sums, byte[] signature, string publicKey = PublicKey)
    {
        ArgumentNullException.ThrowIfNull(sums);
        ArgumentNullException.ThrowIfNull(signature);
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromPem(publicKey);
            return key.VerifyData(sums, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return false;
        }
    }
}
