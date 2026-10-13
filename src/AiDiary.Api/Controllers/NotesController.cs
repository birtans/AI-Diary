using AiDiary.Api.Auth;
using AiDiary.Api.Contracts;
using AiDiary.Api.Notes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize(Policy = "DiaryUser")]
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

    /// <summary>Save into the working copy. 409 with the current revision on mismatch; no-op on identical content.</summary>
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

    /// <summary>Permanently deletes a trashed note, including its stored versions. 409 if not in the trash.</summary>
    [HttpDelete("{id:guid}/permanent")]
    public async Task<IActionResult> Purge(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        await notes.PurgeAsync(user.Id, id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/versions")]
    public async Task<ActionResult<List<NoteVersionItem>>> Versions(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.ListVersionsAsync(user.Id, id, ct));
    }

    /// <summary>"Save Version": keep the working copy as a version for the retention period.</summary>
    [HttpPost("{id:guid}/versions")]
    public async Task<ActionResult<NoteDetail>> SaveVersion(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.SaveVersionAsync(user.Id, id, ct));
    }

    [HttpGet("{id:guid}/versions/{a:int}/diff/{b:int}")]
    public async Task<ActionResult<DiffResponse>> Diff(Guid id, int a, int b, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.DiffAsync(user.Id, id, a, b, ct));
    }

    /// <summary>Restores into the working copy; the text it replaces is kept in history.</summary>
    [HttpPost("{id:guid}/versions/{n:int}/restore")]
    public async Task<ActionResult<NoteDetail>> RestoreVersion(Guid id, int n, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await notes.RestoreVersionAsync(user.Id, id, n, ct));
    }
}
