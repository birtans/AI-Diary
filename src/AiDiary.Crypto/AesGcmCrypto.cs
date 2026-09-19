using System.Security.Cryptography;

namespace AiDiary.Crypto;

/// <summary>
/// AES-256-GCM with a fresh 96-bit nonce and a 128-bit tag per operation.
/// Every sealed blob is laid out as: nonce (12) || ciphertext || tag (16).
/// </summary>
public static class AesGcmCrypto
{
    public const int KeySizeBytes = 32;
    public const int NonceSizeBytes = 12;
    public const int TagSizeBytes = 16;

    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySizeBytes);

    public static byte[] Seal(byte[] key, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"AES key must be {KeySizeBytes} bytes.", nameof(key));
        }

        var nonce = RandomNumberGenerator.GetBytes(NonceSizeBytes);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSizeBytes];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, aad);

        var result = new byte[NonceSizeBytes + ciphertext.Length + TagSizeBytes];
        nonce.CopyTo(result, 0);
        ciphertext.CopyTo(result, NonceSizeBytes);
        tag.CopyTo(result, NonceSizeBytes + ciphertext.Length);
        return result;
    }

    public static byte[] Open(byte[] key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> aad = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length != KeySizeBytes)
        {
            throw new ArgumentException($"AES key must be {KeySizeBytes} bytes.", nameof(key));
        }
        if (blob.Length < NonceSizeBytes + TagSizeBytes)
        {
            throw new CryptographicException("Sealed blob is too short to contain a nonce and tag.");
        }

        var nonce = blob[..NonceSizeBytes];
        var tag = blob[^TagSizeBytes..];
        var ciphertext = blob[NonceSizeBytes..^TagSizeBytes];
        var plaintext = new byte[ciphertext.Length];

        using var aes = new AesGcm(key, TagSizeBytes);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, aad);
        return plaintext;
    }

    public static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        var result = new byte[a.Length + b.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        return result;
    }

    public static byte[] Concat(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b, ReadOnlySpan<byte> c)
    {
        var result = new byte[a.Length + b.Length + c.Length];
        a.CopyTo(result);
        b.CopyTo(result.AsSpan(a.Length));
        c.CopyTo(result.AsSpan(a.Length + b.Length));
        return result;
    }
}
