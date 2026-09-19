using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/recovery")]
public sealed class RecoveryController(CurrentUser current, RecoveryService recovery) : ControllerBase
{
    [HttpGet("status")]
    public async Task<ActionResult<RecoveryStatusResponse>> Status(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await recovery.StatusAsync(user.Id, ct));
    }

    /// <summary>Idempotent setup. The recovery key is returned exactly once.</summary>
    [HttpPost("setup")]
    public async Task<ActionResult<RecoverySetupResponse>> Setup(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await recovery.SetupAsync(user.Id, ct));
    }

    /// <summary>Rotating the recovery key rewraps the UKEK; no note is touched.</summary>
    [HttpPost("rotate")]
    public async Task<ActionResult<RecoverySetupResponse>> Rotate(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await recovery.RotateAsync(user.Id, ct));
    }

    /// <summary>Verify the user saved the key by attempting to unwrap with it.</summary>
    [HttpPost("ack")]
    public async Task<IActionResult> Acknowledge(AckRecoveryRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        await recovery.AcknowledgeAsync(user.Id, request.RecoveryKey, ct);
        return NoContent();
    }

    /// <summary>Wrapped blob for assembling a recovery kit (not secret — it is the stored blob).</summary>
    [HttpGet("export")]
    public async Task<ActionResult<RecoveryExportResponse>> Export(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await recovery.ExportAsync(user.Id, ct));
    }
}
