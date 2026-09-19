using AiDiary.Api.Auth;
using Microsoft.AspNetCore.Authorization;

namespace AiDiary.Api.Auth;

/// <summary>Admin authorization is the single operator flag on the user row.</summary>
public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminHandler(CurrentUser current) : AuthorizationHandler<AdminRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }
        var user = await current.GetUserAsync();
        if (user?.IsAdmin == true)
        {
            context.Succeed(requirement);
        }
    }
}
