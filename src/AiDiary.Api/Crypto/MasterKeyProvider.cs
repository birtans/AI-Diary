using AiDiary.Crypto;
using AiDiary.Api.Configuration;

namespace AiDiary.Api.Crypto;

/// <summary>
/// Holds the deployment master key from DIARY_MASTER_KEY (256-bit, base64).
/// Read lazily so EF design-time tooling never fails on a missing variable.
/// </summary>
public sealed class MasterKeyProvider(DiaryOptions options)
{
    private byte[]? _key;

    public byte[] Key => _key ??= Load();

    private byte[] Load()
    {
        var value = Environment.GetEnvironmentVariable(options.MasterKeyEnv);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"Environment variable {options.MasterKeyEnv} is not set. The master key is required for this service.");
        }
        var key = Convert.FromBase64String(value.Trim());
        if (key.Length != AesGcmCrypto.KeySizeBytes)
        {
            throw new InvalidOperationException(
                $"{options.MasterKeyEnv} must decode to {AesGcmCrypto.KeySizeBytes} bytes (256 bits).");
        }
        return key;
    }
}
