using System.Security.Cryptography;
using System.Text;
using AiDiary.Api.Data;
using AiDiary.Crypto;

namespace AiDiary.Api.Crypto;

/// <summary>
/// Three-level envelope encryption:
///   Master Key (MK)   - deployment, env var only
///   User Key (UKEK)   - one per user, wrapped by MK and by the recovery key
///   Data Key (DEK)    - one per note, wrapped by UKEK
/// AES-256-GCM throughout.
/// </summary>
public sealed class KeyHierarchyService(MasterKeyProvider masterKey)
{
    // ---------- UKEK ----------

    public byte[] CreateUkek() => AesGcmCrypto.GenerateKey();

    public byte[] WrapUkekUnderMaster(byte[] ukek) => AesGcmCrypto.Seal(masterKey.Key, ukek);

    public byte[] UnwrapUkekUnderMaster(byte[] wrappedUkek) => AesGcmCrypto.Open(masterKey.Key, wrappedUkek);

    public byte[] UnwrapUkekUnderMaster(AppUser? user)
    {
        if (user?.WrappedUkek is not { Length: > 0 })
        {
            throw new InvalidOperationException("User keys are not set up yet. Complete recovery setup first.");
        }
        return UnwrapUkekUnderMaster(user.WrappedUkek);
    }

    // ---------- Recovery key ----------

    public string GenerateRecoveryKey() => RecoveryKey.Generate();

    public byte[] WrapUkekUnderRecovery(byte[] ukek, string recoveryKey, Guid userId) =>
        AesGcmCrypto.Seal(RecoveryKey.DeriveKek(recoveryKey, userId), ukek);

    /// <summary>Returns null when the presented recovery key does not authenticate.</summary>
    public byte[]? TryUnwrapUkekUnderRecovery(byte[] blob, string recoveryKey, Guid userId)
    {
        try
        {
            return AesGcmCrypto.Open(RecoveryKey.DeriveKek(recoveryKey, userId), blob);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    // ---------- DEK ----------

    public byte[] CreateDek() => AesGcmCrypto.GenerateKey();

    public byte[] WrapDek(byte[] ukek, byte[] dek) => AesGcmCrypto.Seal(ukek, dek);

    public byte[] UnwrapDek(byte[] ukek, byte[] wrappedDek) => AesGcmCrypto.Open(ukek, wrappedDek);

    // ---------- Field encryption (titles, labels, chat bodies, API keys) ----------

    public byte[] EncryptText(byte[] ukek, string text) => AesGcmCrypto.Seal(ukek, Encoding.UTF8.GetBytes(text));

    public string DecryptText(byte[] ukek, byte[]? blob)
    {
        if (blob is not { Length: > 0 })
        {
            return "";
        }
        return Encoding.UTF8.GetString(AesGcmCrypto.Open(ukek, blob));
    }

    public byte[] EncryptBytes(byte[] ukek, byte[] data) => AesGcmCrypto.Seal(ukek, data);

    public byte[] DecryptBytes(byte[] ukek, byte[] blob) => AesGcmCrypto.Open(ukek, blob);

    // ---------- Misc ----------

    public static byte[] Sha256(ReadOnlySpan<byte> data) => SHA256.HashData(data);
}
