using System.Text.Json;
using AiDiary.Api.Auth;
using AiDiary.Api.Chat;
using AiDiary.Api.Contracts;
using AiDiary.Api.Notes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AiDiary.Api.Controllers;

[ApiController]
[Authorize(Policy = "DiaryUser")]
[Route("api/chat")]
public sealed class ChatController(CurrentUser current, ChatService chat) : ControllerBase
{
    /// <summary>
    /// Streaming relay to the user's own LLM endpoint. SSE events:
    /// {"type":"delta"} {"type":"draft"} {"type":"done"} {"type":"error"}.
    /// </summary>
    [HttpPost]
    public async Task Stream(ChatRequest request, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        try
        {
            await chat.RunAsync(user.Id, request, async json =>
            {
                await Response.WriteAsync($"data: {json}\n\n", ct);
                await Response.Body.FlushAsync(ct);
            }, ct);
        }
        catch (ApiException ex)
        {
            var error = JsonSerializer.Serialize(new { type = "error", content = ex.Message });
            await Response.WriteAsync($"data: {error}\n\n", ct);
            await Response.Body.FlushAsync(ct);
        }
    }

    [HttpGet("conversations")]
    public async Task<ActionResult<List<ConversationListItem>>> Conversations(CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await chat.ListConversationsAsync(user.Id, ct));
    }

    [HttpGet("conversations/{id:guid}")]
    public async Task<ActionResult<List<ChatMessageItem>>> Messages(Guid id, CancellationToken ct)
    {
        var user = await current.RequireUserAsync(ct);
        return Ok(await chat.ListMessagesAsync(user.Id, id, ct));
    }
}
