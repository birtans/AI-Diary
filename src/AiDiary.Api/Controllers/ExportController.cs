using AiDiary.Api.Auth;
using AiDiary.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize(Policy = "DiaryUser")]
[Route("api/export")]
public sealed class ExportController(CurrentUser current, ExportService export) : ControllerBase
{
    /// <summary>Full archive as a zip of plain .md files.</summary>
    [HttpPost]
    public async Task ExportPlain(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"diary-{DateTime.UtcNow:yyyyMMdd}.zip\"";
        await export.ExportPlainAsync(user.Id, Response.Body, ct);
    }

    /// <summary>
    /// Encrypted archive: ciphertext objects exactly as stored plus keys.json
    /// and recovery.dat, decryptable with the reference decryptor and a
    /// recovery key alone.
    /// </summary>
    [HttpPost("encrypted")]
    public async Task ExportEncrypted(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = $"attachment; filename=\"diary-encrypted-{DateTime.UtcNow:yyyyMMdd}.zip\"";
        await export.ExportEncryptedAsync(user.Id, Response.Body, ct);
    }
}
