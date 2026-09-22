using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AiDiary.Api.Contracts;
using AiDiary.Api.Crypto;
using AiDiary.Api.Data;
using AiDiary.Api.Notes;
using AiDiary.Api.Search;
using Microsoft.EntityFrameworkCore;

namespace AiDiary.Api.Chat;

/// <summary>
/// Relays to the user's own OpenAI-compatible endpoint. The agent can search
/// notes and propose a draft, never write. Falls back to a JSON-only prompt
/// when the endpoint does not implement tool calling. The relay never logs
/// request or response bodies and never persists the decrypted API key.
/// </summary>
public sealed partial class ChatService(
    AppDbContext db,
    KeyHierarchyService crypto,
    SearchService search,
    ILogger<ChatService> logger)
{
    private const int MaxHistoryMessages = 20;
    private const int MaxToolTurns = 4;

    // -------------------------------------------------------------- DTOs

    private sealed class ApiMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = "";
        [JsonPropertyName("content")] public string? Content { get; set; }
        [JsonPropertyName("tool_calls")] public List<ApiToolCall>? ToolCalls { get; set; }
        [JsonPropertyName("tool_call_id")] public string? ToolCallId { get; set; }
    }

    private sealed class ApiToolCall
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public ApiFunctionCall Function { get; set; } = new();
    }

    private sealed class ApiFunctionCall
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("arguments")] public string Arguments { get; set; } = "";
    }

    private sealed class ApiTool
    {
        [JsonPropertyName("type")] public string Type { get; set; } = "function";
        [JsonPropertyName("function")] public ApiToolFunction Function { get; set; } = new();
    }

    private sealed class ApiToolFunction
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("parameters")] public object Parameters { get; set; } = new();
    }

    private sealed class ChatResponse
    {
        [JsonPropertyName("choices")] public List<Choice> Choices { get; set; } = [];
    }

    private sealed class Choice
    {
        [JsonPropertyName("message")] public ApiMessage Message { get; set; } = new();
        [JsonPropertyName("delta")] public ApiMessage Delta { get; set; } = new();
    }

    private sealed record ToolResult(string Name, string Content, List<Guid> CitedNoteIds);

    // ------------------------------------------------------------------ run

    public async Task RunAsync(
        Guid userId,
        ChatRequest request,
        Func<string, Task> sendEvent,
        CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User does not exist.");
        if (user.WrappedUkek is not { Length: > 0 })
        {
            throw new ConflictException("Complete recovery setup before using the assistant.");
        }
        if (string.IsNullOrWhiteSpace(user.LlmEndpointUrl))
        {
            throw new ConflictException("No LLM endpoint configured. Set one in Settings first.");
        }

        var ukek = crypto.UnwrapUkekUnderMaster(user.WrappedUkek);
        var apiKey = user.LlmApiKeyEnc is { Length: > 0 }
            ? crypto.DecryptBytes(ukek, user.LlmApiKeyEnc)
            : null;
        var model = string.IsNullOrWhiteSpace(user.LlmModel) ? null : user.LlmModel;
        var baseUrl = NormalizeEndpoint(user.LlmEndpointUrl);

        var conversation = await GetOrCreateConversationAsync(userId, request, ukek, ct);
        var history = await LoadHistoryAsync(conversation.Id, ukek, ct);

        var userMessage = new ApiMessage { Role = "user", Content = request.Message };
        var messages = BuildMessages(history, userMessage, jsonMode: false);

        var citedIds = new List<Guid>();
        string? draftJson = null;
        string? finalText = null;
        var jsonMode = false;

        // Tool loop (non-streaming). When the endpoint rejects tools, fall
        // back to the JSON-only prompt.
        for (var turn = 0; turn < MaxToolTurns; turn++)
        {
            var (text, toolCalls, toolsUnsupported) = await CompleteAsync(
                baseUrl, apiKey, model, messages, jsonMode ? null : Tools(), ct);

            if (toolsUnsupported && !jsonMode)
            {
                jsonMode = true;
                messages = BuildMessages(history, userMessage, jsonMode: true);
                logger.LogInformation("LLM endpoint does not support tools; switching to JSON prompt.");
                continue;
            }

            if (jsonMode)
            {
                // The model replies with JSON: either a tool call or a reply.
                var parsed = ParseJsonReply(text);
                if (parsed is null)
                {
                    throw new ConflictException(
                        "The LLM endpoint did not produce a valid response and does not support tool calling. " +
                        "Check the endpoint and model configuration.");
                }
                if (parsed.Value.IsTool)
                {
                    var result = await ExecuteToolAsync(parsed.Value.ToolName, parsed.Value.ToolArgs, userId, ukek, ct, sendEvent);
                    if (result is null)
                    {
                        continue;
                    }
                    citedIds.AddRange(result.CitedNoteIds);
                    if (result.Name == "propose_note")
                    {
                        draftJson = result.Content;
                        finalText = await FinalReplyAsync(baseUrl, apiKey, model, ct) ??
                            throw new ConflictException("The LLM endpoint produced no response after drafting.");
                        break;
                    }
                    messages.Add(new ApiMessage { Role = "assistant", Content = parsed.Value.ToolResultMessage });
                    messages.Add(new ApiMessage { Role = "tool", Content = result.Content });
                    continue;
                }
                finalText = parsed.Value.Reply;
                break;
            }

            if (toolCalls.Count > 0)
            {
                messages.Add(new ApiMessage
                {
                    Role = "assistant",
                    Content = null,
                    ToolCalls = toolCalls,
                });
                foreach (var call in toolCalls)
                {
                    var (name, args) = ParseToolCall(call);
                    var result = await ExecuteToolAsync(name, args, userId, ukek, ct, sendEvent);
                    if (result is null)
                    {
                        messages.Add(new ApiMessage { Role = "tool", ToolCallId = call.Id, Content = "Tool not found." });
                        continue;
                    }
                    citedIds.AddRange(result.CitedNoteIds);
                    messages.Add(new ApiMessage { Role = "tool", ToolCallId = call.Id, Content = result.Content });
                    if (result.Name == "propose_note")
                    {
                        draftJson = result.Content;
                    }
                }
                continue;
            }

            finalText = text;
            break;
        }

        if (finalText is null)
        {
            throw new ConflictException("The LLM endpoint produced no response. Check the endpoint configuration.");
        }

        // Final answer, streamed. The model already decided on content in the
        // tool loop; stream a fresh generation for UX and fall back to the
        // non-streamed text if the stream misbehaves.
        var streamed = await StreamFinalAsync(baseUrl, apiKey, model, messages, jsonMode, sendEvent, ct);
        var assistantText = streamed ?? finalText;
        if (streamed is null)
        {
            await sendEvent(Event("delta", content: assistantText));
        }
        await sendEvent(Event("done",
            conversationId: conversation.Id,
            extra: JsonSerializer.Serialize(new { cited_note_ids = citedIds.Distinct().Select(x => x.ToString()) })));

        // Persist the assistant message.
        var now = DateTimeOffset.UtcNow;
        db.Messages.Add(new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Role = "assistant",
            BodyEnc = crypto.EncryptText(ukek, assistantText),
            CitedNoteIds = citedIds.Distinct().ToArray(),
            CreatedAt = now,
        });
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------- helpers

    private async Task<Conversation> GetOrCreateConversationAsync(
        Guid userId, ChatRequest request, byte[] ukek, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        Conversation conversation;
        if (request.ConversationId is { } conversationId)
        {
            conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
                ?? throw new NotFoundException("Conversation not found.");
        }
        else
        {
            var title = request.Message.Length <= 60 ? request.Message : request.Message[..60];
            conversation = new Conversation
            {
                Id = Guid.CreateVersion7(),
                UserId = userId,
                TitleEnc = crypto.EncryptText(ukek, title),
                CreatedAt = now,
                LastMessageAt = now,
            };
            db.Conversations.Add(conversation);
        }

        db.Messages.Add(new Message
        {
            Id = Guid.CreateVersion7(),
            ConversationId = conversation.Id,
            Role = "user",
            BodyEnc = crypto.EncryptText(ukek, request.Message),
            CreatedAt = now,
        });
        conversation.LastMessageAt = now;
        await db.SaveChangesAsync(ct);
        return conversation;
    }

    private async Task<List<(string Role, string Content)>> LoadHistoryAsync(
        Guid conversationId, byte[] ukek, CancellationToken ct)
    {
        var history = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxHistoryMessages)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return history.Select(m => (m.Role, crypto.DecryptText(ukek, m.BodyEnc))).ToList();
    }

    private static List<ApiMessage> BuildMessages(
        List<(string Role, string Content)> history, ApiMessage userMessage, bool jsonMode)
    {
        var messages = new List<ApiMessage> { new() { Role = "system", Content = SystemPrompt(jsonMode) } };
        messages.AddRange(history.Select(h => new ApiMessage { Role = h.Role, Content = h.Content }));
        messages.Add(userMessage);
        return messages;
    }

    private static string SystemPrompt(bool jsonMode) => jsonMode ? JsonSystemPrompt : ToolsSystemPrompt;

    private const string ToolsSystemPrompt = """
        You are the assistant inside a personal diary application. You help the
        user reflect on and find their own past writing. You are warm, concise
        and precise.

        You have two tools. Use search_notes to look through the user's diary
        before answering questions about their past. Use propose_note to draft
        a diary entry for them — you may never write directly; the user always
        reviews and saves a draft themselves. Search before claiming the user
        wrote something. Cite the notes you actually used.
        """;

    private const string JsonSystemPrompt = """
        You are the assistant inside a personal diary application.

        Your endpoint does not support function calling, so you MUST answer with
        exactly one JSON object and nothing else, in one of these shapes:

        {"tool":"search_notes","query":"...","date_from":"YYYY-MM-DD","date_to":"YYYY-MM-DD"}
        {"tool":"propose_note","title":"...","body":"...","entry_date":"YYYY-MM-DD"}
        {"reply":"your markdown answer"}

        Use search_notes before answering questions about the user's past.
        Use propose_note to draft an entry — never write it directly.
        No prose outside the JSON.
        """;

    private static List<ApiTool> Tools() =>
    [
        new ApiTool
        {
            Function = new ApiToolFunction
            {
                Name = "search_notes",
                Description = "Semantically search the user's diary entries. Returns note id, entry date, title, an excerpt and a relevance score.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        query = new { type = "string", description = "What to search for" },
                        date_from = new { type = "string", description = "Optional inclusive start date, YYYY-MM-DD" },
                        date_to = new { type = "string", description = "Optional inclusive end date, YYYY-MM-DD" },
                        limit = new { type = "integer", description = "Optional result count, default 8" },
                    },
                    required = new[] { "query" },
                },
            },
        },
        new ApiTool
        {
            Function = new ApiToolFunction
            {
                Name = "propose_note",
                Description = "Propose a diary entry draft. Writes nothing; the user reviews and saves it.",
                Parameters = new
                {
                    type = "object",
                    properties = new
                    {
                        title = new { type = "string" },
                        body = new { type = "string", description = "Markdown body" },
                        entry_date = new { type = "string", description = "YYYY-MM-DD, defaults to today" },
                    },
                    required = new[] { "title", "body" },
                },
            },
        },
    ];

    private async Task<(string? Text, List<ApiToolCall> ToolCalls, bool ToolsUnsupported)> CompleteAsync(
        string baseUrl, byte[]? apiKey, string? model, List<ApiMessage> messages, List<ApiTool>? tools, CancellationToken ct)
    {
        using var http = CreateClient(baseUrl, apiKey);
        var body = new
        {
            model = model ?? "default",
            messages,
            tools,
            stream = false,
        };
        var response = await http.PostAsJsonAsync(baseUrl + "/chat/completions", body, ct);
        if (tools is not null && response.StatusCode == System.Net.HttpStatusCode.BadRequest)
        {
            var bodyText = await response.Content.ReadAsStringAsync(ct);
            if (bodyText.Contains("tool", StringComparison.OrdinalIgnoreCase) ||
                bodyText.Contains("function", StringComparison.OrdinalIgnoreCase))
            {
                return (null, [], ToolsUnsupported: true);
            }
        }
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        var message = result?.Choices.FirstOrDefault()?.Message;
        if (message is null)
        {
            return (null, [], false);
        }
        return (message.Content, message.ToolCalls ?? [], false);
    }

    private async Task<string?> StreamFinalAsync(
        string baseUrl,
        byte[]? apiKey,
        string? model,
        List<ApiMessage> messages,
        bool jsonMode,
        Func<string, Task> sendEvent,
        CancellationToken ct)
    {
        // JSON-only endpoints cannot stream a JSON envelope; skip streaming.
        if (jsonMode)
        {
            return null;
        }
        try
        {
            using var http = CreateClient(baseUrl, apiKey);
            var body = new
            {
                model = model ?? "default",
                messages,
                tools = Tools(),
                stream = true,
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
            {
                Content = JsonContent.Create(body),
            };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream);
            var buffer = new StringBuilder();
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data:", StringComparison.Ordinal))
                {
                    continue;
                }
                var payload = line["data:".Length..].Trim();
                if (payload == "[DONE]")
                {
                    break;
                }
                using var doc = JsonDocument.Parse(payload);
                var choices = doc.RootElement.GetProperty("choices");
                if (choices.GetArrayLength() == 0)
                {
                    continue;
                }
                var delta = choices[0].GetProperty("delta");
                if (delta.TryGetProperty("tool_calls", out _))
                {
                    // Model went off-script mid-stream: clear what was sent and
                    // fall back to the buffered non-streamed text.
                    await sendEvent(Event("reset"));
                    return null;
                }
                if (delta.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                {
                    var chunk = content.GetString();
                    if (!string.IsNullOrEmpty(chunk))
                    {
                        buffer.Append(chunk);
                        await sendEvent(Event("delta", content: chunk));
                    }
                }
            }
            return buffer.Length > 0 ? buffer.ToString() : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Streaming failed, falling back to buffered response.");
            await sendEvent(Event("reset"));
            return null;
        }
    }

    private async Task<string?> FinalReplyAsync(string baseUrl, byte[]? apiKey, string? model, CancellationToken ct)
    {
        try
        {
            var messages = new List<ApiMessage>
            {
                new() { Role = "system", Content = "Reply in one short markdown paragraph confirming the draft is ready for the user to review." },
            };
            var (text, _, _) = await CompleteAsync(baseUrl, apiKey, model, messages, null, ct);
            return text;
        }
        catch
        {
            return "I've drafted an entry for you — review it in the panel and save it when you're happy.";
        }
    }

    private async Task<ToolResult?> ExecuteToolAsync(
        string name, JsonElement args, Guid userId, byte[] ukek, CancellationToken ct, Func<string, Task> sendEvent)
    {
        switch (name)
        {
            case "search_notes":
                var query = args.TryGetProperty("query", out var q) ? q.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(query))
                {
                    return new ToolResult(name, "{}", []);
                }
                var dateFrom = TryGetDate(args, "date_from");
                var dateTo = TryGetDate(args, "date_to");
                var limit = args.TryGetProperty("limit", out var l) && l.TryGetInt32(out var parsedLimit)
                    ? parsedLimit
                    : 8;
                var results = await search.SearchAsync(userId, new SearchRequest(query, dateFrom, dateTo, limit), ct);
                var cited = results.Select(r => r.NoteId).ToList();
                var json = JsonSerializer.Serialize(results.Select(r => new
                {
                    note_id = r.NoteId,
                    entry_date = r.EntryDate.ToString("yyyy-MM-dd"),
                    title = r.Title,
                    excerpt = r.Excerpt,
                    score = r.Score,
                }));
                return new ToolResult(name, json, cited);

            case "propose_note":
                var title = args.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var body = args.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
                var entryDate = TryGetDate(args, "entry_date") ?? DateOnly.FromDateTime(DateTime.UtcNow);
                var draft = new
                {
                    title,
                    body,
                    entry_date = entryDate.ToString("yyyy-MM-dd"),
                };
                await sendEvent(Event("draft", extra: JsonSerializer.Serialize(draft)));
                return new ToolResult(name, JsonSerializer.Serialize(draft), []);

            default:
                return null;
        }
    }

    private static (string Name, JsonElement Args) ParseToolCall(ApiToolCall call)
    {
        var args = new JsonElement();
        if (!string.IsNullOrWhiteSpace(call.Function.Arguments))
        {
            try
            {
                using var doc = JsonDocument.Parse(call.Function.Arguments);
                args = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                args = new JsonElement();
            }
        }
        return (call.Function.Name, args);
    }

    private static DateOnly? TryGetDate(JsonElement args, string property)
    {
        if (args.TryGetProperty(property, out var value) &&
            value.ValueKind == JsonValueKind.String &&
            DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out var date))
        {
            return date;
        }
        return null;
    }

    private static JsonReply? ParseJsonReply(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var match = JsonBlockRegex().Match(text);
        if (!match.Success)
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(match.Value);
            var root = doc.RootElement;
            if (root.TryGetProperty("reply", out var reply) && reply.ValueKind == JsonValueKind.String)
            {
                return new JsonReply(false, "", default, reply.GetString(), null);
            }
            if (root.TryGetProperty("tool", out var tool) && tool.ValueKind == JsonValueKind.String)
            {
                var toolName = tool.GetString() ?? "";
                if (root.TryGetProperty("args", out var args))
                {
                    return new JsonReply(true, toolName, args.Clone(), null, toolName + ": " + root.GetRawText());
                }
                var obj = new Dictionary<string, JsonElement>();
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Name is not ("tool" or "reply"))
                    {
                        obj[property.Name] = property.Value.Clone();
                    }
                }
                using var buffer = JsonSerializer.SerializeToDocument(obj);
                return new JsonReply(true, toolName, buffer.RootElement.Clone(), null, toolName + ": " + root.GetRawText());
            }
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private readonly record struct JsonReply(
        bool IsTool,
        string ToolName,
        JsonElement ToolArgs,
        string? Reply,
        string? ToolResultMessage);

    private static HttpClient CreateClient(string baseUrl, byte[]? apiKey)
    {
        var client = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "text/event-stream, application/json");
        if (apiKey is { Length: > 0 })
        {
            client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", "Bearer " + Encoding.UTF8.GetString(apiKey));
        }
        return client;
    }

    private static string NormalizeEndpoint(string url)
    {
        var trimmed = url.TrimEnd('/');
        if (trimmed.EndsWith("/chat/completions", StringComparison.Ordinal))
        {
            return trimmed[..^"/chat/completions".Length];
        }
        if (trimmed.EndsWith("/v1", StringComparison.Ordinal))
        {
            return trimmed;
        }
        return trimmed + "/v1";
    }

    private static string Event(string type, string? content = null, Guid? conversationId = null, string? extra = null)
    {
        var json = JsonSerializer.Serialize(new
        {
            type,
            content,
            conversation_id = conversationId,
            extra = extra is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(extra),
        });
        return json;
    }

    public async Task<List<ConversationListItem>> ListConversationsAsync(Guid userId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User does not exist.");
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var conversations = await db.Conversations.AsNoTracking()
            .Where(c => c.UserId == userId)
            .OrderByDescending(c => c.LastMessageAt)
            .Take(50)
            .ToListAsync(ct);
        return conversations
            .Select(c => new ConversationListItem(c.Id, crypto.DecryptText(ukek, c.TitleEnc), c.LastMessageAt))
            .ToList();
    }

    public async Task<List<ChatMessageItem>> ListMessagesAsync(Guid userId, Guid conversationId, CancellationToken ct)
    {
        var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("User does not exist.");
        var conversation = await db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId && c.UserId == userId, ct)
            ?? throw new NotFoundException("Conversation not found.");
        var ukek = crypto.UnwrapUkekUnderMaster(user);
        var messages = await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);
        return messages
            .Select(m => new ChatMessageItem(
                m.Id,
                m.Role,
                crypto.DecryptText(ukek, m.BodyEnc),
                m.CitedNoteIds.Select(id => id.ToString()).ToArray(),
                m.CreatedAt))
            .ToList();
    }

    [GeneratedRegex(@"\{.*\}", RegexOptions.Singleline)]
    private static partial Regex JsonBlockRegex();
}
