using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AiDiary.Crypto;

/// <summary>
/// Self-describing encrypted object format written to object storage.
///
/// Offset  Bytes  Field
/// 0       5      Magic "WNMD1"
/// 5       1      Format version
/// 6       16     Note ID (UUID, big-endian byte order)
/// 22      4      Version number (big-endian u32)
/// 26      12     GCM nonce
/// 38      n      Ciphertext
/// 38+n    16     GCM tag
///
/// AAD for GCM = note ID bytes (16) + version bytes (4, big-endian). This
/// binds each object to its identity: swapping one note's ciphertext into
/// another note's object key fails authentication.
/// </summary>
public static class FileEnvelope
{
    public static ReadOnlySpan<byte> Magic => "WNMD1"u8;
    public const byte FormatVersion = 1;
    /// <summary>5 magic + 1 format + 16 note id + 4 version. The GCM body (nonce||ciphertext||tag) follows.</summary>
    public const int FixedHeaderSize = 5 + 1 + 16 + 4;
    public const int HeaderSize = FixedHeaderSize + AesGcmCrypto.NonceSizeBytes;

    public static byte[] Seal(Guid noteId, int version, byte[] dek, ReadOnlySpan<byte> plaintext)
    {
        var id = noteId.ToByteArray(bigEndian: true);
        Span<byte> versionBytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBytes, version);

        var body = AesGcmCrypto.Seal(dek, plaintext, AesGcmCrypto.Concat(id, versionBytes));

        var result = new byte[FixedHeaderSize + body.Length];
        Magic.CopyTo(result);
        result[5] = FormatVersion;
        id.CopyTo(result.AsSpan(6));
        versionBytes.CopyTo(result.AsSpan(22));
        body.CopyTo(result, FixedHeaderSize);
        return result;
    }

    public static byte[] Open(ReadOnlySpan<byte> file, byte[] dek)
    {
        if (file.Length < HeaderSize + AesGcmCrypto.TagSizeBytes)
        {
            throw new CryptographicException("Encrypted object is truncated.");
        }
        if (!file[..5].SequenceEqual(Magic))
        {
            throw new CryptographicException("Not an AI Diary encrypted object (bad magic).");
        }
        if (file[5] != FormatVersion)
        {
            throw new CryptographicException($"Unsupported envelope version: {file[5]}.");
        }

        var idBytes = file.Slice(6, 16);
        var versionBytes = file.Slice(22, 4);
        var noteId = new Guid(idBytes, bigEndian: true);
        var version = BinaryPrimitives.ReadInt32BigEndian(versionBytes);
        var blob = file[FixedHeaderSize..];

        var plaintext = AesGcmCrypto.Open(dek, blob, AesGcmCrypto.Concat(idBytes, versionBytes));
        return plaintext;
    }
}
