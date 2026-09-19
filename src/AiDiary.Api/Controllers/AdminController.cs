using AiDiary.Api.Admin;
using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize(Policy = "Admin")]
[Route("api/admin/access-requests")]
public sealed class AdminController(CurrentUser current, AdminService admin) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<List<AccessRequestItem>>> List(
        [FromQuery] string? status, CancellationToken ct)
    {
        await current.RequireUserAsync(ct);
        return Ok(await admin.ListAsync(status, ct));
    }

    /// <summary>Creates the Keycloak user, sends the execute-actions email, creates the app row.</summary>
    [HttpPost("{id:guid}/approve")]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        await current.RequireUserAsync(ct);
        await admin.ApproveAsync(id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/reject")]
    public async Task<IActionResult> Reject(Guid id, CancellationToken ct)
    {
        await current.RequireUserAsync(ct);
        await admin.RejectAsync(id, ct);
        return NoContent();
    }
}
