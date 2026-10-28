using System.Security.Cryptography;
using System.Text;
using Flexarr.Api.Updates;

namespace Flexarr.Api.Tests;

public class UpdateSignatureTests
{
    [Fact]
    public void ChecksumsSignedWithTheKey_AreAccepted_AndAnythingElseIsnt()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = key.ExportSubjectPublicKeyInfoPem();
        var sums = Encoding.UTF8.GetBytes("abc123  flexarr-1.0.0-osx-arm64.tar.gz\n");
        // As openssl dgst -sha256 -sign writes it.
        var signature = key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        Assert.True(UpdateSignature.Verify(sums, signature, publicKey));
        Assert.False(UpdateSignature.Verify(Encoding.UTF8.GetBytes("evil123  flexarr-1.0.0-osx-arm64.tar.gz\n"), signature, publicKey));
        Assert.False(UpdateSignature.Verify(sums, other.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), publicKey));
        Assert.False(UpdateSignature.Verify(sums, [1, 2, 3], publicKey));
    }

    [Fact]
    public void TheBuiltInKey_IsARealKey()
    {
        using var key = ECDsa.Create();
        key.ImportFromPem(UpdateSignature.PublicKey);
        Assert.Equal(256, key.KeySize);
    }
}
