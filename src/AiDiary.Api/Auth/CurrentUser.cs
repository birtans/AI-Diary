using System.Security.Claims;
using AiDiary.Api.Data;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Auth;

/// <summary>
/// Resolves the calling user from the OIDC `sub` claim and loads (or lazily
/// creates) the matching application row. No endpoint ever accepts a user
/// identifier as a parameter; this is the entire tenant isolation mechanism.
/// </summary>
public sealed class CurrentUser(IHttpContextAccessor http, AppDbContext db)
{
    private AppUser? _cached;

    public string? Sub => http.HttpContext?.User.FindFirstValue("sub");

    public string? EmailClaim =>
        http.HttpContext?.User.FindFirstValue("email")
        ?? http.HttpContext?.User.FindFirstValue("preferred_username");

    public bool IsAuthenticated => !string.IsNullOrEmpty(Sub);

    public async Task<AppUser?> GetUserAsync(CancellationToken ct = default)
    {
        if (_cached is not null)
        {
            return _cached;
        }
        var sub = Sub;
        if (string.IsNullOrEmpty(sub))
        {
            return null;
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.KeycloakSub == sub, ct);
        if (user is null)
        {
            user = new AppUser
            {
                Id = Guid.CreateVersion7(),
                KeycloakSub = sub,
                Email = EmailClaim ?? sub,
                CreatedAt = DateTimeOffset.UtcNow,
                IsAdmin = false,
            };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // Lost a race creating the row; use the winner.
                db.Entry(user).State = EntityState.Detached;
                user = await db.Users.FirstOrDefaultAsync(u => u.KeycloakSub == sub, ct);
            }
        }
        _cached = user;
        return user;
    }

    public async Task<AppUser> RequireUserAsync(CancellationToken ct = default) =>
        await GetUserAsync(ct)
        ?? throw new UnauthorizedAccessException("Authentication required.");
}
