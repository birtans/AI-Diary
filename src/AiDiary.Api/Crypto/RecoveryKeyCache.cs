using System.Collections.Concurrent;

namespace AiDiary.Api.Crypto;

/// <summary>
/// Holds freshly generated recovery keys in memory for a short window so the
/// client can download a complete recovery kit. Keys are never persisted in
/// plaintext; once an entry expires, the kit can only be recreated by
/// rotating the recovery key.
/// </summary>
public sealed class RecoveryKeyCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    private sealed record Entry(string Key, DateTimeOffset ExpiresAt);

    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public void Put(Guid userId, string key) => _entries[userId] = new Entry(key, DateTimeOffset.UtcNow + Lifetime);

    public string? Get(Guid userId) =>
        _entries.TryGetValue(userId, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow ? entry.Key : null;

    public void Remove(Guid userId) => _entries.TryRemove(userId, out _);
}
