using AiDiary.Api.Admin;
using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/users")]
public sealed class AdminUsersController(CurrentUser current, AdminService admin) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AdminUserItem>>> List(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await admin.ListUsersAsync(user.Id, ct));
    }

    /// <summary>
    /// Permanently deletes the account: the Keycloak user, every stored object,
    /// notes, chats, access requests and the app row. 409 for your own account.
    /// </summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        await admin.DeleteUserAsync(user.Id, id, ct);
        return NoContent();
    }
}
