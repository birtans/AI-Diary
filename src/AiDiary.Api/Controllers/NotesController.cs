using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Notes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/notes")]
public sealed class NotesController(CurrentUser current, NoteService notes) : ControllerBase
{
    /// <summary>Sidebar list, grouped by entry date. deleted=true lists the trash.</summary>
    [HttpGet]
    public async Task<ActionResult<List<NoteListItem>>> List(
        [FromQuery] bool deleted = false,
        CancellationToken ct = default)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.ListAsync(user.Id, deleted, ct));
    }

    [HttpPost]
    public async Task<ActionResult<NoteDetail>> Create(CreateNoteRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        var detail = await notes.CreateAsync(user.Id, request, ct);
        return CreatedAtAction(nameof(Get), new { id = detail.Id }, detail);
    }

    [HttpGet("{id:guid}")]
    public async Task<ActionResult<NoteDetail>> Get(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.GetAsync(user.Id, id, ct));
    }

    /// <summary>Save a new version. 409 with the current version on mismatch; no-op on identical content.</summary>
    [HttpPut("{id:guid}")]
    public async Task<ActionResult<NoteDetail>> Save(Guid id, SaveNoteRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.SaveAsync(user.Id, id, request, ct));
    }

    [HttpPatch("{id:guid}/meta")]
    public async Task<ActionResult<NoteDetail>> PatchMeta(Guid id, PatchNoteMetaRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.PatchMetaAsync(user.Id, id, request, ct));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        await notes.SoftDeleteAsync(user.Id, id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/restore")]
    public async Task<ActionResult<NoteDetail>> Restore(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        await notes.RestoreAsync(user.Id, id, ct);
        return Ok(await notes.GetAsync(user.Id, id, ct));
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<List<NoteVersionItem>>> Versions(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.ListVersionsAsync(user.Id, id, ct));
    }

    [HttpGet("{id:guid}/versions/{a:int}/diff/{b:int}")]
    public async Task<ActionResult<DiffResponse>> Diff(Guid id, int a, int b, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.DiffAsync(user.Id, id, a, b, ct));
    }

    /// <summary>History is append-only: restoring writes the old content as a new version.</summary>
    [HttpPost("{id:guid}/versions/{n:int}/restore")]
    public async Task<ActionResult<NoteDetail>> RestoreVersion(Guid id, int n, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.RestoreVersionAsync(user.Id, id, n, ct));
    }
}
