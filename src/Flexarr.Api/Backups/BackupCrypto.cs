using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Flexarr.Api.Backups;

/// <summary>The passphrase given to open a protected file was wrong (or the file was changed).</summary>
public sealed class WrongPassphraseException() : Exception("That passphrase doesn’t open this file.");

/// <summary>
/// Passphrase protection for backups and settings files: AES-256-GCM in 1 MB chunks, under a key derived from the
/// passphrase with PBKDF2-SHA256. Each chunk's nonce counts up and says whether it's the last, so chunks can't be
/// swapped, dropped or cut off without the file failing to open.
/// </summary>
/// <remarks>
/// Layout: "FLEXARR-ENC1", iterations (int32 LE), salt (16), nonce prefix (4), then chunks of
/// [last (1 byte)][length (int32 LE)][ciphertext][tag (16)].
/// </remarks>
public static class BackupCrypto
{
    public const int MinPassphraseLength = 8;

    private const int Iterations = 600_000;
    private const int ChunkSize = 1 << 20;
    private const int TagSize = 16;
    private const int SaltSize = 16;
    private const int PrefixSize = 4;

    private static ReadOnlySpan<byte> Magic => "FLEXARR-ENC1"u8;

    /// <summary>Whether the file starts like one this class wrote.</summary>
    public static bool IsEncrypted(string path)
    {
        using var file = File.OpenRead(path);
        Span<byte> head = stackalloc byte[Magic.Length];
        return file.ReadAtLeast(head, head.Length, throwOnEndOfStream: false) == head.Length && head.SequenceEqual(Magic);
    }

    public static async Task EncryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var prefix = RandomNumberGenerator.GetBytes(PrefixSize);
        var header = new byte[Magic.Length + 4 + SaltSize + PrefixSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(Magic.Length), Iterations);
        salt.CopyTo(header, Magic.Length + 4);
        prefix.CopyTo(header, Magic.Length + 4 + SaltSize);
        await output.WriteAsync(header, ct);

        using var aes = new AesGcm(DeriveKey(passphrase, salt, Iterations), TagSize);
        var current = new byte[ChunkSize];
        var next = new byte[ChunkSize];
        var cipher = new byte[ChunkSize];
        var tag = new byte[TagSize];
        var chunkHeader = new byte[5];
        var length = await input.ReadAtLeastAsync(current, ChunkSize, throwOnEndOfStream: false, ct);
        for (long counter = 0; ; counter++)
        {
            // Reading one chunk ahead tells whether this one is the last.
            var nextLength = length == ChunkSize ? await input.ReadAtLeastAsync(next, ChunkSize, throwOnEndOfStream: false, ct) : 0;
            var last = nextLength == 0;
            aes.Encrypt(Nonce(prefix, counter, last), current.AsSpan(0, length), cipher.AsSpan(0, length), tag);

            chunkHeader[0] = last ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteInt32LittleEndian(chunkHeader.AsSpan(1), length);
            await output.WriteAsync(chunkHeader, ct);
            await output.WriteAsync(cipher.AsMemory(0, length), ct);
            await output.WriteAsync(tag, ct);
            if (last)
            {
                return;
            }

            (current, next) = (next, current);
            length = nextLength;
        }
    }

    /// <exception cref="WrongPassphraseException">The passphrase is wrong, or the file was changed.</exception>
    /// <exception cref="InvalidDataException">The file isn't one of these, or it's cut short.</exception>
    public static async Task DecryptAsync(Stream input, Stream output, string passphrase, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(output);

        var header = new byte[Magic.Length + 4 + SaltSize + PrefixSize];
        if (await input.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct) < header.Length || !header.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("This isn’t a passphrase-protected Flexarr file.");
        }

        var iterations = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(Magic.Length));
        // A file can't make opening it take forever.
        if (iterations is < 100_000 or > 2_000_000)
        {
            throw new InvalidDataException("This file’s protection isn’t one Flexarr knows.");
        }

        var salt = header.AsSpan(Magic.Length + 4, SaltSize).ToArray();
        var prefix = header.AsSpan(Magic.Length + 4 + SaltSize, PrefixSize).ToArray();
        using var aes = new AesGcm(DeriveKey(passphrase, salt, iterations), TagSize);
        var chunkHeader = new byte[5];
        var cipher = new byte[ChunkSize];
        var plain = new byte[ChunkSize];
        var tag = new byte[TagSize];
        for (long counter = 0; ; counter++)
        {
            if (await input.ReadAtLeastAsync(chunkHeader, chunkHeader.Length, throwOnEndOfStream: false, ct) < chunkHeader.Length)
            {
                throw new InvalidDataException("This file is cut short.");
            }

            var last = chunkHeader[0] == 1;
            var length = BinaryPrimitives.ReadInt32LittleEndian(chunkHeader.AsSpan(1));
            if (length is < 0 or > ChunkSize || chunkHeader[0] > 1)
            {
                throw new InvalidDataException("This file is damaged.");
            }

            if (await input.ReadAtLeastAsync(cipher.AsMemory(0, length), length, throwOnEndOfStream: false, ct) < length
                || await input.ReadAtLeastAsync(tag, TagSize, throwOnEndOfStream: false, ct) < TagSize)
            {
                throw new InvalidDataException("This file is cut short.");
            }

            try
            {
                aes.Decrypt(Nonce(prefix, counter, last), cipher.AsSpan(0, length), tag, plain.AsSpan(0, length));
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new WrongPassphraseException();
            }

            await output.WriteAsync(plain.AsMemory(0, length), ct);
            if (last)
            {
                // Nothing may follow the last chunk.
                if (await input.ReadAsync(chunkHeader.AsMemory(0, 1), ct) > 0)
                {
                    throw new InvalidDataException("This file has more in it than it should.");
                }

                return;
            }
        }
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, iterations, HashAlgorithmName.SHA256, 32);

    /// <summary>The file's random prefix, the chunk's number, and whether it's the last.</summary>
    private static byte[] Nonce(byte[] prefix, long counter, bool last)
    {
        var nonce = new byte[12];
        prefix.CopyTo(nonce, 0);
        BinaryPrimitives.WriteInt64BigEndian(nonce.AsSpan(PrefixSize), counter);
        nonce[PrefixSize] = last ? (byte)1 : (byte)0;
        return nonce;
    }
}
