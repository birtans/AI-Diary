using System.Text.Json.Serialization;

namespace AiDiary.Crypto;

/// <summary>
/// File formats consumed by the reference decryptors (the .NET console tool
/// and the single-file Python script). A decryptable directory contains:
///   - one or more .md.enc objects as written to storage
///   - recovery.dat: the recovery-wrapped UKEK
///   - keys.json:     the wrapped DEK and export filename for each object
/// </summary>
public sealed class RecoveryDat
{
    [JsonPropertyName("user_id")]
    public required Guid UserId { get; init; }

    [JsonPropertyName("recovery_wrapped_ukek")]
    public required string RecoveryWrappedUkek { get; init; }
}

public sealed class KeysFile
{
    [JsonPropertyName("user_id")]
    public required Guid UserId { get; init; }

    [JsonPropertyName("notes")]
    public required List<KeysFileNote> Notes { get; init; }
}

public sealed class KeysFileNote
{
    [JsonPropertyName("object_key")]
    public required string ObjectKey { get; init; }

    [JsonPropertyName("wrapped_dek")]
    public required string WrappedDek { get; init; }

    [JsonPropertyName("filename")]
    public required string Filename { get; init; }
}
