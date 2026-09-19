using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/settings")]
public sealed class SettingsController(
    CurrentUser current,
    AppDbContext db,
    KeyHierarchyService crypto) : ControllerBase
{
    /// <summary>Never returns the API key.</summary>
    [HttpGet]
    public async Task<ActionResult<SettingsResponse>> Get(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        var fresh = await db.Users.AsNoTracking().FirstAsync(u => u.Id == user.Id, ct);
        return Ok(new SettingsResponse(
            fresh.LlmEndpointUrl,
            fresh.LlmModel,
            fresh.LlmApiKeyEnc is { Length: > 0 }));
    }

    /// <summary>apiKey: omitted/null keeps the stored key, "" clears it, anything else stores it.</summary>
    [HttpPut]
    public async Task<ActionResult<SettingsResponse>> Update(UpdateSettingsRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        var ukek = crypto.UnwrapUkekUnderMaster(user);

        if (request.EndpointUrl is not null)
        {
            user.LlmEndpointUrl = request.EndpointUrl.Trim();
        }
        if (request.Model is not null)
        {
            user.LlmModel = request.Model.Trim();
        }
        if (request.ApiKey is not null)
        {
            user.LlmApiKeyEnc = request.ApiKey.Length == 0
                ? null
                : crypto.EncryptBytes(ukek, System.Text.Encoding.UTF8.GetBytes(request.ApiKey));
        }
        await db.SaveChangesAsync(ct);

        return Ok(new SettingsResponse(
            user.LlmEndpointUrl,
            user.LlmModel,
            user.LlmApiKeyEnc is { Length: > 0 }));
    }
}
