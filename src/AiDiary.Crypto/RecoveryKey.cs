using System.Security.Cryptography;

namespace AiDiary.Crypto;

/// <summary>
/// A recovery key is a 16-byte random seed presented as 26 base32 characters
/// in five groups. The seed is never used directly as a cipher key: a 256-bit
/// KEK is derived with HKDF-SHA256, salted with the user id, so the wrapping
/// stays AES-256-GCM and the key is bound to exactly one user.
/// </summary>
public static class RecoveryKey
{
    public const int SeedBytes = 16;

    private static readonly byte[] KekInfo = "ai-diary/recovery-kek/v1"u8.ToArray();

    public static string Generate() => Base32.Encode(RandomNumberGenerator.GetBytes(SeedBytes));

    /// <summary>Formats the 26-character key as five groups (5-5-5-5-6).</summary>
    public static string Format(string key)
    {
        var clean = key.Replace("-", "").Replace(" ", "").ToUpperInvariant();
        if (clean.Length != 26)
        {
            throw new FormatException("A recovery key must decode to 26 base32 characters.");
        }
        return string.Join("-", [
            clean[..5],
            clean[5..10],
            clean[10..15],
            clean[15..20],
            clean[20..26],
        ]);
    }

    public static byte[] Parse(string key)
    {
        var seed = Base32.Decode(key);
        if (seed.Length != SeedBytes)
        {
            throw new FormatException("A recovery key must decode to 16 bytes.");
        }
        return seed;
    }

    public static byte[] DeriveKek(byte[] seed, Guid userId) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, seed, 32, userId.ToByteArray(bigEndian: true), KekInfo);

    public static byte[] DeriveKek(string key, Guid userId) => DeriveKek(Parse(key), userId);
}
