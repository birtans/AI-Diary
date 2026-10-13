using System.Collections.Concurrent;

namespace AiDiary.Api.Auth;

/// <summary>
/// OIDC subjects of accounts an admin deleted. Deleting the Keycloak user ends
/// its sessions, but an access token already issued stays valid until it
/// expires — and <see cref="CurrentUser"/> would re-provision an empty row for
/// it on the next request. Tokens for these subjects are rejected outright.
/// In memory: single-instance deployment, and the window only has to outlive
/// the longest-lived access token.
/// </summary>
public sealed class RevokedSubjects
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<string, DateTimeOffset> _entries = new(StringComparer.Ordinal);

    public void Add(string sub)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, expiresAt) in _entries)
        {
            if (expiresAt <= now)
            {
                _entries.TryRemove(key, out _);
            }
        }
        _entries[sub] = now + Lifetime;
    }

    public bool Contains(string sub) =>
        _entries.TryGetValue(sub, out var expiresAt) && expiresAt > DateTimeOffset.UtcNow;
}
