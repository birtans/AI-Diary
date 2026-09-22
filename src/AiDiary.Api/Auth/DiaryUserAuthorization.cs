using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using AiDiary.Api.Data;

namespace AiDiary.Api.Auth;

/// <summary>
/// Gates the app to approved diary users, so a Keycloak realm shared with
/// other applications doesn't let their users in just by having a valid
/// token. Deliberately does not go through <see cref="CurrentUser"/> — that
/// auto-provisions an AppUser row on first sight, which would defeat the
/// point by creating an account for exactly the callers this should reject.
/// </summary>
public sealed class DiaryUserRequirement : IAuthorizationRequirement;

public sealed class DiaryUserHandler(AppDbContext db) : AuthorizationHandler<DiaryUserRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, DiaryUserRequirement requirement)
    {
        if (context.User.IsInRole("diary-user"))
        {
            context.Succeed(requirement);
            return;
        }

        var sub = context.User.FindFirstValue("sub");
        if (string.IsNullOrEmpty(sub))
        {
            return;
        }

        var isAdmin = await db.Users.AsNoTracking()
            .Where(u => u.KeycloakSub == sub)
            .Select(u => u.IsAdmin)
            .FirstOrDefaultAsync();
        if (isAdmin)
        {
            context.Succeed(requirement);
        }
    }
}
