using AiDiary.Api.Contracts;
using AiDiary.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AiDiary.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/access-requests")]
public sealed class AccessRequestsController(AccessRequestService service) : ControllerBase
{
    /// <summary>
    /// The only endpoint strangers can reach. The response is identical
    /// whether the request is new, pending or rejected.
    /// </summary>
    [HttpPost]
    [EnableRateLimiting("public-access-request")]
    public async Task<IActionResult> Submit(AccessRequestRequest request, CancellationToken ct)
    {
        await service.SubmitAsync(request, HttpContext.Connection.RemoteIpAddress?.ToString(), ct);
        return Accepted(new { status = "received" });
    }
}
